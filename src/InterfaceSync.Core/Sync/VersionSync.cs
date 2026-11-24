using System.Security.Cryptography;
using System.Text;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;

namespace InterfaceSync.Core.Sync;

public enum SyncStatus
{
    Idle,
    GameRunning,
    WaitingAfterClose,
    Syncing,
    WaitingForCloud,
    NeedsAttention,
    Paused,
    Error,
}

public enum SyncOutcome
{
    NothingToDo,
    Pushed,
    Pulled,
    Waiting,
    Conflict,
    Refused,
    Cancelled,
    Failed,
    Busy,
}

/// <summary>Timing rules, kept together so tests can see them.</summary>
public sealed class SyncTiming
{
    /// <summary>Wait after the game closes before pushing, so WoW finishes writing.</summary>
    public TimeSpan WaitAfterClose { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Pull only when the cloud's newest file is at least this much newer than the local newest.</summary>
    public TimeSpan PullMargin { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Keeps one game version's WTF\Account in step with its cloud copy. One instance per enabled
/// version; never runs two operations on the same version at once. Call <see cref="Tick"/> on every poll.
/// </summary>
public sealed class VersionSync
{
    private static readonly string StagingSuffix = "." + ProductInfo.Id.ToLowerInvariant() + "-staging";
    private static readonly string OldSuffix = "." + ProductInfo.Id.ToLowerInvariant() + "-old";

    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly BackupStore _backups;
    private readonly StateStore _states;
    private readonly ILog _log;
    private readonly string _machineName;
    private readonly Func<bool> _isGameRunning;
    private readonly SyncTiming _timing;
    private readonly object _gate = new();

    private bool _wasRunning;
    private DateTime? _closedAtUtc;
    private string? _lastLoggedProblem;

    public VersionSync(
        GameVersion version,
        CloudLayout cloud,
        IFileSystem fs,
        IClock clock,
        BackupStore backups,
        StateStore states,
        ILog log,
        string machineName,
        Func<bool> isGameRunning,
        SyncTiming? timing = null)
    {
        Version = version;
        Cloud = cloud;
        _fs = fs;
        _clock = clock;
        _backups = backups;
        _states = states;
        _log = log;
        _machineName = machineName;
        _isGameRunning = isGameRunning;
        _timing = timing ?? new SyncTiming();
        State = _states.Load(version.FolderName);
        Status = State.Conflict != null ? SyncStatus.NeedsAttention : SyncStatus.Idle;
    }

    public GameVersion Version { get; }
    public CloudLayout Cloud { get; }
    public VersionState State { get; private set; }
    public SyncStatus Status { get; private set; }
    public string StatusText { get; private set; } = "";
    public bool Paused { get; set; }

    /// <summary>"Choose what syncs": what this computer leaves out (set by the service from the settings).</summary>
    public SyncRules Rules { get; set; } = SyncRules.None;

    /// <summary>The rules this sync runs with (see <see cref="ChooseRules"/>).</summary>
    private SyncRules _active = SyncRules.None;

    /// <summary>This computer's settings, without what it leaves out.</summary>
    private FolderScan ScanLocal() => _active.Keep(FolderScanner.Scan(_fs, LocalAccount));

    /// <summary>Raised after each upload, download, restore or conflict, for the activity timeline.</summary>
    public event Action<ActivityEntry>? Activity;

    /// <summary>Raised when the game is seen starting (to check whether it was just updated).</summary>
    public event Action<VersionSync>? GameStarted;

    private void Record(ActivityEntry entry)
    {
        try { Activity?.Invoke(entry); }
        catch (Exception e) { _log.Warn($"{Name}: couldn't record activity ({e.Message})."); }
    }

    /// <summary>The player's setup choice for this version's first sync (only used while not yet joined).</summary>
    public Settings.FirstSyncChoice FirstSyncChoice { get; set; }

    /// <summary>What the first sync would find, for the setup window. Reads only; never writes anything.</summary>
    public FirstSyncPreview Preview()
    {
        lock (_gate)
        {
            var cloud = ReadCloud();
            _active = Rules;
            var local = ScanLocal().Manifest;
            bool cloudHasData = cloud.Readiness is CloudReadiness.Ready or CloudReadiness.Incomplete or CloudReadiness.OnlineOnly or CloudReadiness.Writing;
            return new FirstSyncPreview(
                Version.FolderName, Cloud.ProviderName, State.Joined, cloudHasData,
                cloud.Commit?.PushedBy is { Length: > 0 } by ? by : null,
                cloud.Files?.NewestUtc, local.IsEmpty ? null : local.NewestUtc,
                cloud.Readiness == CloudReadiness.Stale ? cloud.Problem : null);
        }
    }

    private string Name => Version.FolderName;
    private string LocalAccount => Version.AccountPath;

    // ------------------------------------------------------------------ polling

    /// <summary>Called on every poll (15 s in the app). Detects game start/exit and runs the sync rules.</summary>
    public SyncOutcome Tick()
    {
        if (!Monitor.TryEnter(_gate)) return SyncOutcome.Busy;
        try
        {
            bool running = _isGameRunning();
            var now = _clock.UtcNow;

            if (running)
            {
                if (!_wasRunning)
                {
                    _log.Info($"{Name}: game started. Syncing waits until it closes.");
                    try { GameStarted?.Invoke(this); }
                    catch (Exception e) { _log.Warn($"{Name}: {e.Message}"); }
                }
                _wasRunning = true;
                _closedAtUtc = null;
                SetStatus(SyncStatus.GameRunning, "Game running");
                return SyncOutcome.Waiting;
            }

            if (_wasRunning)
            {
                _wasRunning = false;
                _closedAtUtc = now;
                State.PendingPush = true; // survives a reboot during the wait
                Save();
                if (Paused)
                {
                    _log.Info($"{Name}: game closed. Syncing is paused, so nothing is uploaded until you resume.");
                    SetStatus(SyncStatus.Paused, "Paused (upload waiting)");
                    return SyncOutcome.NothingToDo;
                }
                _log.Info($"{Name}: game closed. Pushing in {_timing.WaitAfterClose.TotalSeconds:0} seconds.");
                SetStatus(SyncStatus.WaitingAfterClose, "Game closed, waiting for it to finish writing");
                return SyncOutcome.Waiting;
            }

            if (_closedAtUtc is { } closed && now - closed < _timing.WaitAfterClose)
                return SyncOutcome.Waiting;
            _closedAtUtc = null;

            if (State.Conflict != null)
            {
                SetStatus(SyncStatus.NeedsAttention, "Settings changed on this PC and in the cloud: choose which to keep");
                return SyncOutcome.Conflict;
            }

            if (Paused)
            {
                SetStatus(SyncStatus.Paused, State.PendingPush ? "Paused (push waiting)" : "Paused");
                return SyncOutcome.NothingToDo;
            }

            return Guard(() => Evaluate(manual: false));
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Manual "Sync now": same rules, without the after-close wait.</summary>
    public SyncOutcome SyncNow()
    {
        lock (_gate)
        {
            if (_isGameRunning())
            {
                SetStatus(SyncStatus.GameRunning, "Game running");
                return SyncOutcome.Waiting;
            }
            if (State.Conflict != null) return SyncOutcome.Conflict;
            _closedAtUtc = null;
            return Guard(() => Evaluate(manual: true));
        }
    }

    private SyncOutcome Evaluate(bool manual)
    {
        CheckCloudFolderChange();
        if (!State.Joined) return FirstJoin();

        var cloud = ReadCloud();
        if (ChooseRules(cloud) is { } included) return included;
        var local = ScanLocal();
        bool localChanged = !local.Manifest.IsEquivalentTo(_active.Keep(State.LocalManifest));

        if (State.PendingPush || localChanged)
        {
            if (!localChanged)
            {
                State.PendingPush = false;
                Save();
                _log.Info($"{Name}: nothing changed locally, no push needed.");
            }
            else
            {
                return Push(local, cloud, force: false);
            }
        }

        if (cloud.Readiness == CloudReadiness.Stale) return Refuse(cloud.Problem!);

        if (cloud.Readiness == CloudReadiness.Ready && cloud.CommitId != State.LastCommitId)
        {
            // The cloud moved on only in what this computer leaves out (or to the same settings): nothing to download.
            if (_active.Keep(cloud.Files).IsEquivalentTo(local.Manifest))
            {
                State.LastCommitId = cloud.CommitId;
                Save();
                SetIdle();
                return SyncOutcome.NothingToDo;
            }
            var cloudNewest = _active.Keep(cloud.Files).NewestUtc;
            var localNewest = local.Manifest.NewestUtc;
            if (localNewest == null || (cloudNewest != null && cloudNewest.Value >= localNewest.Value + _timing.PullMargin))
                return Pull(cloud, force: false, reason: "before-pull");

            return RaiseConflict(local, cloud,
                "The cloud copy changed, but it isn't clearly newer than this PC's settings.");
        }

        if (cloud.Readiness is CloudReadiness.Writing or CloudReadiness.Incomplete or CloudReadiness.OnlineOnly
            && cloud.CommitId != State.LastCommitId)
        {
            SetStatus(SyncStatus.WaitingForCloud, cloud.Problem ?? $"Waiting for {Cloud.ProviderName}");
            return SyncOutcome.Waiting;
        }

        SetIdle();
        if (manual) _log.Info($"{Name}: already in sync.");
        return SyncOutcome.NothingToDo;
    }

    /// <summary>
    /// Picks the "choose what syncs" rules for this sync. Leaving more out applies at once (those files are simply
    /// ignored). Switching something back on waits until this computer is in step with the cloud, then downloads the
    /// cloud's copy of just those files (this computer's are backed up first), so an old local copy is never uploaded
    /// over the one the other computers share. Returns the outcome when it did that download, else null.
    /// </summary>
    private SyncOutcome? ChooseRules(CloudSnapshot cloud)
    {
        var applied = State.AppliedRules ?? SyncRules.None;
        if (applied.SameAs(Rules) || !applied.IncludesLessThan(Rules) || !State.Joined
            || cloud.Readiness is CloudReadiness.Missing or CloudReadiness.Empty)
        {
            _active = Rules;
            if (!applied.SameAs(Rules))
            {
                State.AppliedRules = Rules.IsEmpty ? null : Rules.Copy();
                Save();
            }
            return null;
        }

        if (cloud.Readiness == CloudReadiness.Ready && cloud.CommitId == State.LastCommitId)
        {
            _active = Rules;
            var outcome = Pull(cloud, force: true, reason: "before-sync-again", only: p => applied.Excludes(p) && !Rules.Excludes(p));
            if (outcome == SyncOutcome.Pulled)
            {
                State.AppliedRules = Rules.IsEmpty ? null : Rules.Copy();
                Save();
            }
            return outcome;
        }

        // Not in step yet: sync as before (leaving out both old and new choices) and switch back on next time.
        _active = applied.Union(Rules);
        return null;
    }

    /// <summary>
    /// The player switched cloud app or folder. If the new folder holds the same commit (the folder simply
    /// moved), carry on. Otherwise treat it as this PC's first sync there, so the safe first-sync rule applies
    /// instead of assuming the new folder is in step.
    /// </summary>
    private void CheckCloudFolderChange()
    {
        if (!State.Joined) return;
        if (State.CloudFolder == null)
        {
            State.CloudFolder = Cloud.VersionRoot; // state from before this was tracked
            Save();
            return;
        }
        if (PathUtil.SamePath(State.CloudFolder, Cloud.VersionRoot)) return;

        var cloud = ReadCloud();
        if (cloud.CommitId != null && cloud.CommitId == State.LastCommitId)
        {
            _log.Info($"{Name}: the cloud folder moved from {State.CloudFolder} to {Cloud.VersionRoot}; it holds the same settings, so syncing carries on.");
        }
        else
        {
            _log.Info($"{Name}: now using {Cloud.VersionRoot} ({Cloud.ProviderName}) instead of {State.CloudFolder}. Treating this as this PC's first sync there.");
            State.Joined = false;
            State.PendingPush = false;
            State.LastCommitId = null;
        }
        State.CloudFolder = Cloud.VersionRoot;
        Save();
    }

    /// <summary>First sync on this PC: if the cloud already has data, the cloud wins and local is backed up.</summary>
    private SyncOutcome FirstJoin()
    {
        var cloud = ReadCloud();
        _active = Rules;
        State.AppliedRules = Rules.IsEmpty ? null : Rules.Copy();
        switch (cloud.Readiness)
        {
            case CloudReadiness.Ready when FirstSyncChoice == Settings.FirstSyncChoice.UseCloud:
                LogOnce($"{Name}: first sync on this computer. As chosen in setup, the settings already in {Cloud.ProviderName} replace this computer's (backed up first).");
                return Pull(cloud, force: true, reason: "first-join");

            case CloudReadiness.Ready when FirstSyncChoice == Settings.FirstSyncChoice.UseThisComputer:
            {
                LogOnce($"{Name}: first sync on this computer. As chosen in setup, this computer's settings replace the ones in {Cloud.ProviderName} (backed up first).");
                return Push(ScanLocal(), cloud, force: true);
            }

            case CloudReadiness.Ready:
            {
                // A copy with newer settings than the cloud's may mean this PC's cloud folder is out of date, or that
                // this PC was played last: never decide silently. Ask, with both dates and PC names.
                var mine = ScanLocal();
                var localNewest = mine.Manifest.NewestUtc;
                var cloudNewest = _active.Keep(cloud.Files).NewestUtc;
                if (localNewest != null && (cloudNewest == null || localNewest.Value > cloudNewest.Value + Manifest.TimeTolerance))
                {
                    var from = cloud.Commit?.PushedBy is { Length: > 0 } by ? by : "an older version (no PC recorded)";
                    return RaiseConflict(mine, cloud,
                        $"First sync on this PC: its settings (last changed {localNewest.Value.ToLocalTime():g}) are newer than the cloud's " +
                        $"(from {from}, last changed {cloudNewest?.ToLocalTime().ToString("g") ?? "never"}). If {Cloud.ProviderName} on this PC " +
                        "isn't fully synced, the cloud copy shown here may be out of date.");
                }
                LogOnce($"{Name}: first sync on this PC. The cloud already has settings, so they replace this PC's (backed up first).");
                return Pull(cloud, force: true, reason: "first-join");
            }

            case CloudReadiness.Stale:
                return Refuse(cloud.Problem!);

            case CloudReadiness.Writing:
            case CloudReadiness.Incomplete:
            case CloudReadiness.OnlineOnly:
                SetStatus(SyncStatus.WaitingForCloud, cloud.Problem ?? $"Waiting for {Cloud.ProviderName}");
                return SyncOutcome.Waiting;

            default: // Missing or Empty
                var local = ScanLocal();
                if (local.Manifest.IsEmpty)
                {
                    SetStatus(SyncStatus.Idle, "Nothing to sync yet: launch this game version once");
                    return SyncOutcome.NothingToDo;
                }
                var why = cloud.Readiness == CloudReadiness.Missing
                    ? $"the folder {Cloud.Account} does not exist"
                    : $"the folder {Cloud.Account} has no files";
                LogOnce($"{Name}: first sync on this PC. {Cloud.ProviderName} has no settings for this version yet ({why}), so this PC's settings are uploaded.{OtherCloudFolders()}");
                return Push(local, cloud, force: true);
        }
    }

    /// <summary>Names other folders in My Drive\InterfaceSync, so a folder-name mismatch with v1.1 shows in the log.</summary>
    private string OtherCloudFolders()
    {
        var appFolder = PathUtil.Parent(Cloud.VersionRoot);
        if (appFolder == null || !_fs.DirectoryExists(appFolder)) return "";
        var others = _fs.EnumerateDirectories(appFolder).Select(PathUtil.Name)
            .Where(n => !n.Equals(PathUtil.Name(Cloud.VersionRoot), StringComparison.OrdinalIgnoreCase)).ToList();
        return others.Count == 0 ? "" : $" Other folders in {appFolder}: {string.Join(", ", others)}.";
    }

    // ------------------------------------------------------------------ push

    private SyncOutcome Push(FolderScan local, CloudSnapshot cloud, bool force)
    {
        if (!local.Exists || local.Manifest.IsEmpty)
        {
            State.PendingPush = false;
            return Refuse("This PC's settings folder is empty, so it was not uploaded over the cloud copy.");
        }

        if (!force && cloud.Readiness == CloudReadiness.Writing && cloud.Commit?.PushedBy != _machineName)
        {
            SetStatus(SyncStatus.WaitingForCloud, $"{cloud.Commit?.PushedBy} is uploading right now");
            return SyncOutcome.Waiting;
        }

        bool cloudMovedOn = cloud.CommitId != null && cloud.CommitId != State.LastCommitId
                            && cloud.Readiness != CloudReadiness.Writing;
        if (!force && cloudMovedOn)
            return RaiseConflict(local, cloud, "Settings changed on this PC and on another PC since the last sync.");

        if (cloud.Readiness == CloudReadiness.Stale) return Refuse(cloud.Problem!);
        if (!Cloud.IsSafeWriteTarget(out var unsafeReason))
            return Refuse($"Refused to upload to {Cloud.Account}: {unsafeReason}. Nothing was written.");

        SetStatus(SyncStatus.Syncing, "Uploading settings");
        foreach (var link in local.Links) _log.Warn($"{Name}: skipped folder link '{link}' (links are never synced).");
        ReportConflictCopies(local, "this PC");

        _fs.CreateDirectory(Cloud.Account);
        var cloudScan = FolderScanner.Scan(_fs, Cloud.Account);
        ReportConflictCopies(cloudScan, "the cloud copy");
        var cloudFiles = cloudScan.Manifest.ToLookup();
        var commitId = NewCommitId();

        new CommitFile
        {
            State = CommitState.Writing,
            CommitId = commitId,
            PushedBy = _machineName,
            PushedAtUtc = _clock.UtcNow,
            Files = cloud.Commit?.Files ?? [],
        }.Write(_fs, Cloud.CommitFile);

        // Never overwrite or delete a cloud file without a backup. With no commit file the cloud copy
        // wasn't written by 2.0 (e.g. v1.1 data), so back up all of it; otherwise just what changes.
        var localLookup = local.Manifest.ToLookup();
        var replaced = local.Manifest.Entries
            .Where(e => cloudFiles.TryGetValue(e.RelativePath, out var c) && !Manifest.SameFile(e, c))
            .Select(e => cloudFiles[e.RelativePath]);
        var removed = cloudScan.Manifest.Entries.Where(e => !localLookup.ContainsKey(e.RelativePath) && !_active.Excludes(e.RelativePath));
        var atRisk = replaced.Concat(removed).ToList();
        if (cloud.Commit == null && FolderScanner.AllFiles(_fs, Cloud.Account).Count > 0)
        {
            var whole = _backups.Create(Name, Cloud.Account, "cloud-before-push-all");
            _log.Info($"{Name}: the cloud copy had no {ProductInfo.Name} commit file, so all of it was backed up first ({whole?.FileCount} files, {whole?.Path}).");
        }
        else if (atRisk.Count > 0)
        {
            var partial = _backups.CreateFromFiles(Name, Cloud.Account, atRisk, "cloud-before-push" + BackupStore.ChangesSuffix);
            _log.Info($"{Name}: backed up {atRisk.Count} cloud file(s) about to be replaced or removed ({partial.Path}).");
        }

        int copied = 0;
        var changedPaths = new List<string>();
        foreach (var entry in local.Manifest.Entries)
        {
            if (cloudFiles.TryGetValue(entry.RelativePath, out var existing) && Manifest.SameFile(entry, existing))
                continue;
            var source = PathUtil.FromRelative(LocalAccount, entry.RelativePath);
            var target = PathUtil.FromRelative(Cloud.Account, entry.RelativePath);
            var temp = target + FolderScanner.TempSuffix;
            _fs.CopyFile(source, temp);
            _fs.MoveFile(temp, target);
            copied++;
            changedPaths.Add(entry.RelativePath);
        }

        // Mirror deletions (already backed up above).
        var toDelete = cloudScan.Manifest.Entries.Where(e => !localLookup.ContainsKey(e.RelativePath) && !_active.Excludes(e.RelativePath)).ToList();
        if (toDelete.Count > 0)
        {
            foreach (var e in toDelete) _fs.DeleteFile(PathUtil.FromRelative(Cloud.Account, e.RelativePath));
            _log.Info($"{Name}: removed {toDelete.Count} file(s) from the cloud that were deleted on this PC.");
        }
        foreach (var temp in cloudScan.TempFiles.Where(t => t.EndsWith(FolderScanner.TempSuffix, StringComparison.OrdinalIgnoreCase)
                                                            || t.EndsWith(ProductInfo.Legacy.TempSuffix, StringComparison.OrdinalIgnoreCase)))
            _fs.DeleteFile(PathUtil.FromRelative(Cloud.Account, temp));

        var pushedAt = _clock.UtcNow;
        new CommitFile
        {
            State = CommitState.Committed,
            CommitId = commitId,
            PushedBy = _machineName,
            PushedAtUtc = pushedAt,
            // What this computer leaves out stays in the cloud as other computers uploaded it.
            Files = local.Manifest.Entries.Concat(cloudScan.Manifest.Entries.Where(e => _active.Excludes(e.RelativePath)))
                .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
        }.Write(_fs, Cloud.CommitFile);
        if (_fs.FileExists(Cloud.LegacyCommitFile)) _fs.DeleteFile(Cloud.LegacyCommitFile); // superseded by the file just written

        State.Joined = true;
        State.PendingPush = false;
        State.LastPushUtc = pushedAt;
        State.LastCommitId = commitId;
        State.CloudFolder = Cloud.VersionRoot;
        State.LocalManifest = local.Manifest;
        State.Conflict = null;
        ClearError();
        Save();
        _log.Info($"{Name}: pushed settings to the cloud ({copied} changed of {local.Manifest.Count} files).");
        changedPaths.AddRange(toDelete.Select(e => e.RelativePath));
        if (changedPaths.Count > 0) Record(ActivityEntry.ForFiles(pushedAt, Name, ActivityKind.Uploaded, changedPaths));
        Devices.Write(_fs, Cloud, _machineName, "Uploaded", pushedAt);
        SetIdle();
        return SyncOutcome.Pushed;
    }


    // ------------------------------------------------------------------ pull

    private SyncOutcome Pull(CloudSnapshot cloud, bool force, string reason, Func<string, bool>? only = null)
    {
        if (cloud.Readiness != CloudReadiness.Ready)
        {
            if (cloud.Readiness is CloudReadiness.Missing or CloudReadiness.Empty)
                return Refuse("The cloud copy is empty, so it was not downloaded over this PC's settings.");
            SetStatus(SyncStatus.WaitingForCloud, cloud.Problem ?? $"Waiting for {Cloud.ProviderName}");
            return SyncOutcome.Waiting;
        }
        if (!force && State.PendingPush)
            return SyncOutcome.Waiting;

        SetStatus(SyncStatus.Syncing, "Downloading settings");
        // Only what this computer syncs (and, when switching something back on, only that).
        bool Wanted(string path) => only == null || only(path);
        var files = _active.Keep(cloud.Files);
        var localScan = ScanLocal();
        var localFiles = localScan.Manifest.ToLookup();

        // Only what differs is written: the cloud files that are new or changed, and the local files the cloud no
        // longer has are removed. Unchanged files aren't touched (no SSD writes, no Drive downloads).
        var cloudLookup = files.ToLookup();
        var changed = files.Entries.Where(e => Wanted(e.RelativePath) && !(localFiles.TryGetValue(e.RelativePath, out var l) && Manifest.SameFile(e, l))).ToList();
        // Switching something back on never deletes: files only this computer has are uploaded by the next sync.
        var removed = only != null ? [] : localScan.Manifest.Entries.Where(l => !cloudLookup.ContainsKey(l.RelativePath)).ToList();
        var atRisk = localScan.Manifest.Entries
            .Where(l => Wanted(l.RelativePath) && (cloudLookup.TryGetValue(l.RelativePath, out var c) ? !Manifest.SameFile(l, c) : only == null))
            .ToList();

        // Back up exactly the local files about to be replaced or removed.
        BackupInfo? backup = null;
        if (atRisk.Count > 0)
        {
            backup = _backups.CreateFromFiles(Name, LocalAccount, atRisk, reason + BackupStore.ChangesSuffix);
            if (backup.Reused) LogOnce($"{Name}: this PC's files about to change are unchanged since the backup {backup.Path}, so they weren't copied again.");
            else _log.Info($"{Name}: backed up this PC's settings that are about to change ({atRisk.Count} file(s), {backup.Path}).");
        }

        // 1. Download the changed files next to WTF\Account and check each one arrived whole.
        var staging = FreeAside(LocalAccount + StagingSuffix);
        _fs.CreateDirectory(staging);
        foreach (var entry in changed)
        {
            var target = PathUtil.FromRelative(staging, entry.RelativePath);
            _fs.CopyFile(PathUtil.FromRelative(Cloud.Account, entry.RelativePath), target);
            var copiedSize = _fs.GetFileInfo(target).Size;
            if (copiedSize != entry.Size)
            {
                RemoveLeftover(staging);
                SetStatus(SyncStatus.WaitingForCloud, $"A file was still downloading from {Cloud.ProviderName}");
                _log.Warn($"{Name}: '{entry.RelativePath}' was {copiedSize} bytes, expected {entry.Size}. Pull postponed.");
                return SyncOutcome.Waiting;
            }
        }

        if (_isGameRunning())
        {
            RemoveLeftover(staging);
            _log.Warn($"{Name}: the game started during the download, so the pull was cancelled. Nothing on this PC changed.");
            SetStatus(SyncStatus.GameRunning, "Game running");
            _wasRunning = true;
            return SyncOutcome.Cancelled;
        }

        // 2. Move them into place (a rename on the same disk: no second copy), and remove what the cloud dropped.
        //    If a file is in use (the game started), everything already changed is put back from the backup.
        var applied = new List<ManifestEntry>();
        var deleted = new List<ManifestEntry>();
        try
        {
            _fs.CreateDirectory(LocalAccount);
            foreach (var entry in changed)
            {
                var target = PathUtil.FromRelative(LocalAccount, entry.RelativePath);
                var parent = PathUtil.Parent(target);
                if (parent != null) _fs.CreateDirectory(parent);
                _fs.MoveFile(PathUtil.FromRelative(staging, entry.RelativePath), target);
                applied.Add(entry);
            }
            foreach (var entry in removed)
            {
                _fs.DeleteFile(PathUtil.FromRelative(LocalAccount, entry.RelativePath));
                deleted.Add(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UndoPartialPull(applied, deleted, localFiles, backup);
            RemoveLeftover(staging);
            _log.Warn($"{Name}: settings files were in use (did the game start?), so the pull was undone. Nothing on this PC changed.");
            SetStatus(SyncStatus.Idle, "Pull undone: files were in use");
            return SyncOutcome.Cancelled;
        }
        RemoveLeftover(staging);

        if (cloud.Commit == null && Cloud.IsSafeWriteTarget(out _))
        {
            // Adopted data InterfaceSync 2.0 didn't write (v1.1): describe it with a commit file so every PC
            // reads it the same way from now on. The id is the same content hash other PCs already hold.
            new CommitFile
            {
                State = CommitState.Committed,
                CommitId = cloud.CommitId!,
                PushedBy = $"{_machineName} (adopted existing data)",
                PushedAtUtc = _clock.UtcNow,
                Files = cloud.Files!.Entries,
            }.Write(_fs, Cloud.CommitFile);
            _log.Info($"{Name}: the existing cloud settings (no {ProductInfo.Name} commit file, e.g. from v1.1) were used, and a commit file was written for them.");
        }

        State.LastPullUtc = _clock.UtcNow;
        if (only != null)
        {
            // Just the files switched back on: the rest stays as it was (including a pending upload).
            var after = ScanLocal().Manifest.Entries.Where(e => only(e.RelativePath) && cloudLookup.ContainsKey(e.RelativePath));
            var kept = (State.LocalManifest?.Entries ?? []).Where(e => !only(e.RelativePath) && !_active.Excludes(e.RelativePath));
            State.LocalManifest = new Manifest { Entries = kept.Concat(after).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList() };
            ClearError();
            Save();
            var back = changed.Concat(removed).Select(e => e.RelativePath).ToList();
            _log.Info($"{Name}: now syncing {(back.Count > 0 ? SettingsItem.Summarize(back) : "what you switched back on")} again; downloaded the cloud's copy ({back.Count} file(s)).");
            if (back.Count > 0) Record(ActivityEntry.ForFiles(_clock.UtcNow, Name, ActivityKind.Downloaded, back, cloud.Commit?.PushedBy is { Length: > 0 } w && w != _machineName ? w : null));
            SetIdle();
            return SyncOutcome.Pulled;
        }
        State.Joined = true;
        State.PendingPush = false;
        State.LastCommitId = cloud.CommitId;
        State.CloudFolder = Cloud.VersionRoot;
        State.LocalManifest = ScanLocal().Manifest;
        State.Conflict = null;
        ClearError();
        Save();
        var from = cloud.Commit?.PushedBy is { Length: > 0 } by ? $" from {by}" : "";
        _log.Info($"{Name}: pulled settings{from} ({files.Count} files).");
        var pulledPaths = changed.Concat(removed).Select(e => e.RelativePath).ToList();
        Devices.Write(_fs, Cloud, _machineName, "Downloaded", State.LastPullUtc.Value);
        if (pulledPaths.Count > 0)
            Record(ActivityEntry.ForFiles(_clock.UtcNow, Name, ActivityKind.Downloaded, pulledPaths,
                cloud.Commit?.PushedBy is { Length: > 0 } who && who != _machineName ? who : null));
        SetIdle();
        return SyncOutcome.Pulled;
    }

    // ------------------------------------------------------------------ conflicts

    private SyncOutcome RaiseConflict(FolderScan local, CloudSnapshot cloud, string reason)
    {
        var backup = _backups.Create(Name, Cloud.Account, "cloud-copy-conflict");
        State.Conflict = new ConflictInfo
        {
            DetectedUtc = _clock.UtcNow,
            Reason = reason,
            LocalNewestUtc = local.Manifest.NewestUtc,
            CloudNewestUtc = cloud.Files?.NewestUtc,
            CloudPushedBy = cloud.Commit?.PushedBy,
            LocalMachine = _machineName,
            CloudCommitId = cloud.CommitId,
            CloudBackupPath = backup?.Path,
        };
        Save();
        Record(new ActivityEntry
        {
            Utc = _clock.UtcNow, Version = Name, Kind = ActivityKind.Conflict, Computer = cloud.Commit?.PushedBy,
            Summary = "Settings changed on two computers",
        });
        _log.Warn($"{Name}: {reason} This PC's settings were kept; the cloud copy was saved to {backup?.Path ?? "(nothing to save)"}. Choose which to keep in the status window.");
        SetStatus(SyncStatus.NeedsAttention, "Choose which settings to keep");
        return SyncOutcome.Conflict;
    }

    /// <summary>The player's answer to a conflict. The game must be closed.</summary>
    public SyncOutcome ResolveConflict(KeepChoice keep)
    {
        lock (_gate)
        {
            if (_isGameRunning()) return Refuse("Close the game before choosing which settings to keep.");
            return Guard(() =>
            {
                var cloud = ReadCloud();
                if (keep == KeepChoice.Local)
                {
                    _log.Info($"{Name}: you chose to keep this PC's settings. Uploading them.");
                    _active = (State.AppliedRules ?? SyncRules.None).Union(Rules);
                    return Push(ScanLocal(), cloud, force: true);
                }
                _log.Info($"{Name}: you chose to keep the cloud settings. Downloading them (this PC's are backed up first).");
                _active = (State.AppliedRules ?? SyncRules.None).Union(Rules);
                return Pull(cloud, force: true, reason: "before-keep-cloud");
            });
        }
    }

    /// <summary>Puts back what a failed pull already changed: the backed-up originals, and removes files it added.</summary>
    private void UndoPartialPull(List<ManifestEntry> applied, List<ManifestEntry> deleted, Dictionary<string, ManifestEntry> before, BackupInfo? backup)
    {
        foreach (var entry in applied.Concat(deleted))
        {
            var target = PathUtil.FromRelative(LocalAccount, entry.RelativePath);
            try
            {
                if (before.ContainsKey(entry.RelativePath) && backup != null)
                    _fs.CopyFile(PathUtil.FromRelative(backup.Path, entry.RelativePath), target);
                else if (!before.ContainsKey(entry.RelativePath))
                    _fs.DeleteFile(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"{Name}: couldn't put back '{entry.RelativePath}' ({e.Message}). Its original is in {backup?.Path}.");
            }
        }
    }

    // ------------------------------------------------------------------ snapshots

    /// <summary>Saves a named copy of this computer's settings for this game version (kept until deleted).</summary>
    public BackupInfo? SaveSnapshot(string name)
    {
        lock (_gate)
        {
            var snap = _backups.CreateSnapshot(Name, LocalAccount, name);
            if (snap == null)
            {
                _log.Warn($"{Name}: there are no settings to save in a snapshot yet.");
                return null;
            }
            _log.Info($"{Name}: saved the snapshot \"{snap.SnapshotName}\" ({snap.FileCount} files, {snap.Path}).");
            Record(new ActivityEntry { Utc = snap.CreatedUtc, Version = Name, Kind = ActivityKind.Snapshot,
                Summary = $"Saved the snapshot \"{snap.SnapshotName}\"", FileCount = snap.FileCount });
            return snap;
        }
    }

    /// <summary>
    /// Called with the game's version from Battle.net (at start and whenever the game starts). The first time a new
    /// version is seen, the settings are copied as they were, so an update that resets or breaks them can be undone.
    /// </summary>
    public BackupInfo? NoteClientVersion(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion)) return null;
        lock (_gate)
        {
            var previous = State.LastClientVersion;
            if (string.Equals(previous, clientVersion, StringComparison.OrdinalIgnoreCase)) return null;
            BackupInfo? snap = null;
            if (previous != null)
            {
                try { snap = _backups.CreateGameUpdateSnapshot(Name, LocalAccount, clientVersion); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"{Name}: the game was updated to {clientVersion}, but its settings couldn't be copied ({e.Message}).");
                    return null;   // try again next time
                }
                if (snap != null)
                {
                    _log.Info($"{Name}: the game was updated ({previous} → {clientVersion}). Saved a copy of the settings first ({snap.Path}).");
                    Record(new ActivityEntry { Utc = snap.CreatedUtc, Version = Name, Kind = ActivityKind.GameUpdated,
                        Summary = $"Game updated to {clientVersion}: saved a copy of the settings", FileCount = snap.FileCount });
                }
            }
            State.LastClientVersion = clientVersion;
            TrySave();
            return snap;
        }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Puts this game version's settings back as they were when <paramref name="backup"/> was made, then uploads them.
    /// A whole-folder backup replaces the settings with that copy. A changed-files backup puts its files back, after
    /// the newer changed-files backups of the same kind (newest first), so every file returns to how it was at that
    /// moment. Only files that differ are written, and the current versions of those are backed up first.
    /// </summary>
    public SyncOutcome RestoreBackup(BackupInfo backup)
    {
        lock (_gate)
        {
            if (_isGameRunning()) return Refuse("Close the game before restoring a backup.");
            return Guard(() =>
            {
                if (FolderScanner.AllFiles(_fs, backup.Path).Count == 0) return Refuse("That backup is empty.");

                // What each file should become: relative path → (backup folder holding it, its entry there).
                var target = new Dictionary<string, (string root, ManifestEntry entry)>(StringComparer.OrdinalIgnoreCase);
                if (!backup.Partial)
                    foreach (var e in FolderScanner.Scan(_fs, backup.Path).Manifest.Entries) target[e.RelativePath] = (backup.Path, e);
                else
                {
                    bool cloudKind = BackupStore.IsCloudReason(backup.Reason);
                    var chain = _backups.List(Name)
                        .Where(b => b.Partial && BackupStore.IsCloudReason(b.Reason) == cloudKind && b.CreatedUtc > backup.CreatedUtc)
                        .OrderByDescending(b => b.CreatedUtc)
                        .Append(backup);                                       // the chosen one last, so it has the final say
                    foreach (var b in chain)                                   // newest first; older ones override
                        foreach (var e in FolderScanner.Scan(_fs, b.Path).Manifest.Entries) target[e.RelativePath] = (b.Path, e);
                }

                var current = FolderScanner.Scan(_fs, LocalAccount).Manifest.ToLookup();
                var writes = target.Where(t => !(current.TryGetValue(t.Key, out var c) && Manifest.SameFile(c, t.Value.entry))).ToList();
                var removes = backup.Partial ? [] : current.Values.Where(c => !target.ContainsKey(c.RelativePath)).ToList();
                if (writes.Count == 0 && removes.Count == 0)
                {
                    _log.Info($"{Name}: the settings already match the backup from {backup.CreatedUtc.ToLocalTime():g}; nothing to restore.");
                    return SyncOutcome.NothingToDo;
                }

                var atRisk = current.Values.Where(c => removes.Contains(c) || writes.Any(w => w.Key.Equals(c.RelativePath, StringComparison.OrdinalIgnoreCase))).ToList();
                BackupInfo? before = atRisk.Count > 0 ? _backups.CreateFromFiles(Name, LocalAccount, atRisk, "before-restore" + BackupStore.ChangesSuffix) : null;

                var staging = FreeAside(LocalAccount + StagingSuffix);
                _fs.CreateDirectory(staging);
                foreach (var (rel, (root, _)) in writes)
                    _fs.CopyFile(PathUtil.FromRelative(root, rel), PathUtil.FromRelative(staging, rel));
                var applied = new List<ManifestEntry>();
                var deleted = new List<ManifestEntry>();
                try
                {
                    _fs.CreateDirectory(LocalAccount);
                    foreach (var (rel, (_, entry)) in writes)
                    {
                        var dest = PathUtil.FromRelative(LocalAccount, rel);
                        if (PathUtil.Parent(dest) is { } parent) _fs.CreateDirectory(parent);
                        _fs.MoveFile(PathUtil.FromRelative(staging, rel), dest);
                        applied.Add(entry);
                    }
                    foreach (var entry in removes)
                    {
                        _fs.DeleteFile(PathUtil.FromRelative(LocalAccount, entry.RelativePath));
                        deleted.Add(entry);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    UndoPartialPull(applied, deleted, current, before);
                    RemoveLeftover(staging);
                    return Refuse($"Some settings files were in use (is the game open?), so the restore was undone: {e.Message}");
                }
                RemoveLeftover(staging);

                State.PendingPush = true;
                Save();
                Record(ActivityEntry.ForFiles(_clock.UtcNow, Name, ActivityKind.Restored,
                    writes.Select(w => w.Key).Concat(removes.Select(r => r.RelativePath)).ToList()));
                _log.Info($"{Name}: restored the settings from {backup.CreatedUtc.ToLocalTime():g} ({writes.Count} file(s) put back" +
                          (removes.Count > 0 ? $", {removes.Count} removed" : "") + $"; the replaced ones were saved to {before?.Path ?? "nowhere: none"}).");
                return Evaluate(manual: true);
            });
        }
    }

    // ------------------------------------------------------------------ cloud state

    internal enum CloudReadiness { Missing, Empty, Writing, Incomplete, OnlineOnly, Ready, Stale }

    internal sealed class CloudSnapshot
    {
        public CloudReadiness Readiness { get; init; }
        public CommitFile? Commit { get; init; }
        public string? CommitId { get; init; }
        public Manifest? Files { get; init; }
        public string? Problem { get; init; }
    }

    internal CloudSnapshot ReadCloud()
    {
        var commit = CommitFile.TryRead(_fs, Cloud.CommitFile) ?? CommitFile.TryRead(_fs, Cloud.LegacyCommitFile);
        var scan = FolderScanner.Scan(_fs, Cloud.Account);

        if (commit == null)
        {
            // Legacy (v1.1) data or a cloud folder never written by 2.0.
            if (scan.Exists && OtherPcsCommit() is { } elsewhere)
                return new CloudSnapshot
                {
                    Readiness = CloudReadiness.Stale,
                    Problem = $"This PC's copy of the {Cloud.ProviderName} folder looks out of date: {Cloud.Account} has no sync record, " +
                              $"but {elsewhere.PushedBy} has synced into {elsewhere.Where}. Make sure {Cloud.ProviderName} is running and fully " +
                              "synced on this PC, then sync again. Nothing was changed.",
                };
            if (!scan.Exists) return new CloudSnapshot { Readiness = CloudReadiness.Missing };
            if (scan.Manifest.IsEmpty)
            {
                // "Has data" means any file at all. A folder holding only Drive conflict copies or temp files
                // is never treated as empty (it would be uploaded over); the player is asked to look.
                int anyFiles = FolderScanner.AllFiles(_fs, Cloud.Account).Count;
                return anyFiles == 0
                    ? new CloudSnapshot { Readiness = CloudReadiness.Empty }
                    : new CloudSnapshot
                    {
                        Readiness = CloudReadiness.Incomplete,
                        Problem = $"{Cloud.Account} holds only {Cloud.ProviderName} conflict copies or temporary files ({anyFiles}); check that folder",
                    };
            }
            if (scan.TempFiles.Count > 0)
                return new CloudSnapshot { Readiness = CloudReadiness.Incomplete, Problem = $"{Cloud.ProviderName} is still transferring files" };
            if (scan.OnlineOnly.Count > 0)
                return OnlineOnly(scan.OnlineOnly.Count);
            return new CloudSnapshot
            {
                Readiness = CloudReadiness.Ready,
                CommitId = LegacyId(scan.Manifest),
                Files = scan.Manifest,
            };
        }

        if (commit.State == CommitState.Writing)
            return new CloudSnapshot
            {
                Readiness = CloudReadiness.Writing, Commit = commit, CommitId = commit.CommitId,
                Problem = $"{commit.PushedBy} is uploading settings",
            };

        var files = commit.ToManifest();
        if (files.IsEmpty) return new CloudSnapshot { Readiness = CloudReadiness.Empty, Commit = commit, CommitId = commit.CommitId };

        var present = scan.Manifest.ToLookup();
        int missing = 0;
        foreach (var e in files.Entries)
            if (!present.TryGetValue(e.RelativePath, out var p) || p.Size != e.Size) missing++;
        if (missing > 0)
            return new CloudSnapshot
            {
                Readiness = CloudReadiness.Incomplete, Commit = commit, CommitId = commit.CommitId, Files = files,
                Problem = $"{Cloud.ProviderName} is still downloading {missing} file(s) from {commit.PushedBy}",
            };

        var wanted = new HashSet<string>(files.Entries.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
        int onlineOnly = scan.OnlineOnly.Count(wanted.Contains);
        if (onlineOnly > 0)
        {
            var s = OnlineOnly(onlineOnly);
            return new CloudSnapshot { Readiness = s.Readiness, Commit = commit, CommitId = commit.CommitId, Files = files, Problem = s.Problem };
        }

        return new CloudSnapshot { Readiness = CloudReadiness.Ready, Commit = commit, CommitId = commit.CommitId, Files = files };
    }

    /// <summary>Another PC's commit file anywhere in the app's cloud folders (current or earlier name), if any.</summary>
    private (string PushedBy, string Where)? OtherPcsCommit()
    {
        var roots = new[] { Cloud.AppRoot }
            .Concat(ProductInfo.Legacy.CloudFolderNames.Select(n => PathUtil.Join(Cloud.MyDrive, n)))
            .Where(_fs.DirectoryExists);
        foreach (var root in roots)
            foreach (var versionDir in _fs.EnumerateDirectories(root))
                foreach (var name in new[] { CloudLayout.CommitFileName, ProductInfo.Legacy.CommitFileName })
                {
                    var c = CommitFile.TryRead(_fs, PathUtil.Join(versionDir, name));
                    if (c != null && c.PushedBy.Length > 0 && !c.PushedBy.StartsWith(_machineName, StringComparison.OrdinalIgnoreCase))
                        return (c.PushedBy, versionDir);
                }
        return null;
    }

    private CloudSnapshot OnlineOnly(int count) => new()
    {
        Readiness = CloudReadiness.OnlineOnly,
        Problem = $"{count} file(s) are online-only. {Cloud.KeepOfflineHint}",
    };

    private static string LegacyId(Manifest m)
    {
        var sb = new StringBuilder();
        foreach (var e in m.Entries)
            sb.Append(e.RelativePath.ToUpperInvariant()).Append('|').Append(e.Size).Append('|')
              .Append(e.LastWriteUtc.Ticks / TimeSpan.TicksPerSecond / 2).Append('\n');
        return "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string NewCommitId() => Guid.NewGuid().ToString("N");

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A free name for a staging or set-aside folder next to WTF\Account. A leftover from an earlier
    /// attempt is removed if possible; if Windows won't let go of it, a numbered name is used instead,
    /// so a stuck leftover can never block syncing.
    /// </summary>
    private string FreeAside(string preferred)
    {
        // Sweep every leftover of this kind ("...-old", "...-old-2", ...) from earlier attempts.
        var parent = PathUtil.Parent(preferred);
        var baseName = PathUtil.Name(preferred);
        if (parent != null && _fs.DirectoryExists(parent))
            foreach (var dir in _fs.EnumerateDirectories(parent).ToList())
            {
                var name = PathUtil.Name(dir);
                if (name.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                    (name.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[(baseName.Length + 1)..], out _)))
                    RemoveLeftover(dir);
            }
        if (!_fs.DirectoryExists(preferred)) return preferred;
        for (int i = 2; ; i++)
        {
            var candidate = $"{preferred}-{i}";
            if (!_fs.DirectoryExists(candidate)) return candidate;
        }
    }

    /// <summary>Deletes a leftover folder; on failure logs it and carries on (its contents are in the backup store).</summary>
    private void RemoveLeftover(string folder)
    {
        try
        {
            _fs.DeleteDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{Name}: couldn't remove the leftover folder {folder} ({e.Message}). It isn't used by the game; " +
                      "it will be tried again next time, or you can delete it yourself.");
        }
    }

    private SyncOutcome Guard(Func<SyncOutcome> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            State.PendingPush = State.PendingPush || State.Joined;
            State.LastError = e.Message;
            State.LastErrorUtc = _clock.UtcNow;
            TrySave();
            var message = $"{Name}: sync failed: {e.Message} It will be retried automatically.";
            if (message != _lastLoggedProblem) _log.Error(message);
            _lastLoggedProblem = message;
            SetStatus(SyncStatus.Error, "Sync failed, retrying: " + e.Message);
            return SyncOutcome.Failed;
        }
    }

    private SyncOutcome Refuse(string reason)
    {
        var message = $"{Name}: {reason}";
        if (message != _lastLoggedProblem) _log.Warn(message);
        _lastLoggedProblem = message;
        State.LastError = reason;
        State.LastErrorUtc = _clock.UtcNow;
        Save();
        SetStatus(SyncStatus.NeedsAttention, reason);
        return SyncOutcome.Refused;
    }

    private readonly HashSet<string> _reportedConflictCopies = new(StringComparer.OrdinalIgnoreCase);

    private void ReportConflictCopies(FolderScan scan, string where)
    {
        var fresh = scan.ConflictCopies.Where(_reportedConflictCopies.Add).ToList();
        if (fresh.Count > 0)
            _log.Warn($"{Name}: found {fresh.Count} cloud conflict copies in {where} (not synced): {string.Join(", ", fresh.Take(5))}{(fresh.Count > 5 ? ", ..." : "")}");
    }

    private readonly HashSet<string> _loggedOnce = [];

    /// <summary>Logs a message once until the next successful sync, so retries don't repeat it every poll.</summary>
    private void LogOnce(string message)
    {
        if (_loggedOnce.Add(message)) _log.Info(message);
    }

    private void ClearError()
    {
        _loggedOnce.Clear();
        State.LastError = null;
        State.LastErrorUtc = null;
        _lastLoggedProblem = null;
    }

    private void SetIdle() => SetStatus(SyncStatus.Idle, "In sync");

    private void SetStatus(SyncStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    private void Save() => _states.Save(Name, State);

    private void TrySave()
    {
        try { Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
