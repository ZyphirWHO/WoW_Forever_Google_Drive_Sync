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
                if (!_wasRunning) _log.Info($"{Name}: game started. Syncing waits until it closes.");
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

        var local = FolderScanner.Scan(_fs, LocalAccount);
        bool localChanged = !local.Manifest.IsEquivalentTo(State.LocalManifest);
        var cloud = ReadCloud();

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

        if (cloud.Readiness == CloudReadiness.Ready && cloud.CommitId != State.LastCommitId)
        {
            var cloudNewest = cloud.Files!.NewestUtc;
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
        switch (cloud.Readiness)
        {
            case CloudReadiness.Ready:
                _log.Info($"{Name}: first sync on this PC. The cloud already has settings, so they replace this PC's (backed up first).");
                return Pull(cloud, force: true, reason: "first-join");

            case CloudReadiness.Writing:
            case CloudReadiness.Incomplete:
            case CloudReadiness.OnlineOnly:
                SetStatus(SyncStatus.WaitingForCloud, cloud.Problem ?? $"Waiting for {Cloud.ProviderName}");
                return SyncOutcome.Waiting;

            default: // Missing or Empty
                var local = FolderScanner.Scan(_fs, LocalAccount);
                if (local.Manifest.IsEmpty)
                {
                    SetStatus(SyncStatus.Idle, "Nothing to sync yet: launch this game version once");
                    return SyncOutcome.NothingToDo;
                }
                var why = cloud.Readiness == CloudReadiness.Missing
                    ? $"the folder {Cloud.Account} does not exist"
                    : $"the folder {Cloud.Account} has no files";
                _log.Info($"{Name}: first sync on this PC. {Cloud.ProviderName} has no settings for this version yet ({why}), so this PC's settings are uploaded.{OtherCloudFolders()}");
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
        var removed = cloudScan.Manifest.Entries.Where(e => !localLookup.ContainsKey(e.RelativePath));
        var atRisk = replaced.Concat(removed).ToList();
        if (cloud.Commit == null && FolderScanner.AllFiles(_fs, Cloud.Account).Count > 0)
        {
            var whole = _backups.Create(Name, Cloud.Account, "cloud-before-push");
            _log.Info($"{Name}: the cloud copy had no {ProductInfo.Name} commit file, so all of it was backed up first ({whole?.FileCount} files, {whole?.Path}).");
        }
        else if (atRisk.Count > 0)
        {
            var partial = _backups.CreateFromFiles(Name, Cloud.Account, atRisk, "cloud-before-push");
            _log.Info($"{Name}: backed up {atRisk.Count} cloud file(s) about to be replaced or removed ({partial.Path}).");
        }

        int copied = 0;
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
        }

        // Mirror deletions (already backed up above).
        var toDelete = cloudScan.Manifest.Entries.Where(e => !localLookup.ContainsKey(e.RelativePath)).ToList();
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
            Files = local.Manifest.Entries,
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
        SetIdle();
        return SyncOutcome.Pushed;
    }


    // ------------------------------------------------------------------ pull

    private SyncOutcome Pull(CloudSnapshot cloud, bool force, string reason)
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
        var files = cloud.Files!;
        var localScan = FolderScanner.Scan(_fs, LocalAccount);
        var localFiles = localScan.Manifest.ToLookup();

        var backup = _backups.Create(Name, LocalAccount, reason);
        if (backup != null) _log.Info($"{Name}: backed up this PC's settings to {backup.Path}.");

        var staging = LocalAccount + StagingSuffix;
        var old = LocalAccount + OldSuffix;
        if (_fs.DirectoryExists(staging)) _fs.DeleteDirectory(staging);
        _fs.CreateDirectory(staging);

        foreach (var entry in files.Entries)
        {
            // Unchanged files come from the local copy: faster and avoids Drive downloads.
            var source = localFiles.TryGetValue(entry.RelativePath, out var l) && Manifest.SameFile(entry, l)
                ? PathUtil.FromRelative(LocalAccount, entry.RelativePath)
                : PathUtil.FromRelative(Cloud.Account, entry.RelativePath);
            var target = PathUtil.FromRelative(staging, entry.RelativePath);
            _fs.CopyFile(source, target);
            var copiedSize = _fs.GetFileInfo(target).Size;
            if (copiedSize != entry.Size)
            {
                _fs.DeleteDirectory(staging);
                SetStatus(SyncStatus.WaitingForCloud, $"A file was still downloading from {Cloud.ProviderName}");
                _log.Warn($"{Name}: '{entry.RelativePath}' was {copiedSize} bytes, expected {entry.Size}. Pull postponed.");
                return SyncOutcome.Waiting;
            }
        }

        if (_isGameRunning())
        {
            _fs.DeleteDirectory(staging);
            _log.Warn($"{Name}: the game started during the download, so the pull was cancelled. Nothing on this PC changed.");
            SetStatus(SyncStatus.GameRunning, "Game running");
            _wasRunning = true;
            return SyncOutcome.Cancelled;
        }

        if (_fs.DirectoryExists(old)) _fs.DeleteDirectory(old);
        bool hadLocal = _fs.DirectoryExists(LocalAccount);
        try
        {
            if (hadLocal) _fs.MoveDirectory(LocalAccount, old);
            _fs.MoveDirectory(staging, LocalAccount);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_fs.DirectoryExists(LocalAccount) && _fs.DirectoryExists(old)) _fs.MoveDirectory(old, LocalAccount);
            if (_fs.DirectoryExists(staging)) _fs.DeleteDirectory(staging);
            _log.Warn($"{Name}: settings files were in use (did the game start?), so the pull was undone. Nothing on this PC changed.");
            SetStatus(SyncStatus.Idle, "Pull undone: files were in use");
            return SyncOutcome.Cancelled;
        }
        if (hadLocal) _fs.DeleteDirectory(old); // already in the backup store

        if (cloud.Commit == null)
        {
            // Adopted data InterfaceSync 2.0 didn't write (v1.1): describe it with a commit file so every PC
            // reads it the same way from now on. The id is the same content hash other PCs already hold.
            new CommitFile
            {
                State = CommitState.Committed,
                CommitId = cloud.CommitId!,
                PushedBy = $"{_machineName} (adopted existing data)",
                PushedAtUtc = _clock.UtcNow,
                Files = files.Entries,
            }.Write(_fs, Cloud.CommitFile);
            _log.Info($"{Name}: the existing cloud settings (no {ProductInfo.Name} commit file, e.g. from v1.1) were used, and a commit file was written for them.");
        }

        State.Joined = true;
        State.PendingPush = false;
        State.LastPullUtc = _clock.UtcNow;
        State.LastCommitId = cloud.CommitId;
        State.CloudFolder = Cloud.VersionRoot;
        State.LocalManifest = FolderScanner.Scan(_fs, LocalAccount).Manifest;
        State.Conflict = null;
        ClearError();
        Save();
        var from = cloud.Commit?.PushedBy is { Length: > 0 } by ? $" from {by}" : "";
        _log.Info($"{Name}: pulled settings{from} ({files.Count} files).");
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
            CloudCommitId = cloud.CommitId,
            CloudBackupPath = backup?.Path,
        };
        Save();
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
                    return Push(FolderScanner.Scan(_fs, LocalAccount), cloud, force: true);
                }
                _log.Info($"{Name}: you chose to keep the cloud settings. Downloading them (this PC's are backed up first).");
                return Pull(cloud, force: true, reason: "before-keep-cloud");
            });
        }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>Puts a backup back in place. The current settings are backed up first; the restored copy is then pushed.</summary>
    public SyncOutcome RestoreBackup(BackupInfo backup)
    {
        lock (_gate)
        {
            if (_isGameRunning()) return Refuse("Close the game before restoring a backup.");
            return Guard(() =>
            {
                var files = FolderScanner.AllFiles(_fs, backup.Path);
                if (files.Count == 0) return Refuse("That backup is empty.");
                var before = _backups.Create(Name, LocalAccount, "before-restore");

                var staging = LocalAccount + StagingSuffix;
                var old = LocalAccount + OldSuffix;
                if (_fs.DirectoryExists(staging)) _fs.DeleteDirectory(staging);
                TreeCopy.CopyFiles(_fs, backup.Path, staging, files);
                if (_fs.DirectoryExists(old)) _fs.DeleteDirectory(old);
                bool hadLocal = _fs.DirectoryExists(LocalAccount);
                if (hadLocal) _fs.MoveDirectory(LocalAccount, old);
                _fs.MoveDirectory(staging, LocalAccount);
                if (hadLocal) _fs.DeleteDirectory(old);

                State.PendingPush = true;
                Save();
                _log.Info($"{Name}: restored the backup from {backup.CreatedUtc.ToLocalTime():g} (previous settings saved to {before?.Path ?? "nowhere: folder was empty"}).");
                return Evaluate(manual: true);
            });
        }
    }

    // ------------------------------------------------------------------ cloud state

    internal enum CloudReadiness { Missing, Empty, Writing, Incomplete, OnlineOnly, Ready }

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

    private CloudSnapshot OnlineOnly(int count) => new()
    {
        Readiness = CloudReadiness.OnlineOnly,
        Problem = $"{count} file(s) are online-only. {Cloud.Provider.KeepOfflineHint()}",
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

    private void ClearError()
    {
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
