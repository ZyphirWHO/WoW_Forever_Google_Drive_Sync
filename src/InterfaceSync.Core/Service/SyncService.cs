using System.Text.Json;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Service;

/// <summary>Where InterfaceSync keeps its own files: %LOCALAPPDATA%\InterfaceSync.</summary>
public sealed class AppPaths
{
    public AppPaths(string localAppData)
    {
        Data = PathUtil.Join(localAppData, ProductInfo.Id);
        Logs = PathUtil.Join(Data, "Logs");
        Backups = PathUtil.Join(Data, "Backups");
        Temp = PathUtil.Join(Data, "Temp");
    }

    public string Data { get; }
    public string Logs { get; }
    public string Backups { get; }
    public string Temp { get; }
}

/// <summary>The tray icon's colour.</summary>
public enum OverallState { InSync, Syncing, Waiting, NeedsAttention, Paused }

public enum NoticeKind { Conflict, SyncFailed, DriveNotFound, WowNotFound, LogFailed }

/// <summary>Something the player needs to act on (shown as a Windows notification).</summary>
public sealed record Notice(NoticeKind Kind, string Title, string Message);

/// <summary>
/// Runs everything in one process: detection, one <see cref="VersionSync"/> per enabled game version,
/// and the 15-second poll. No UI here, so it can be tested without Windows.
/// </summary>
public sealed class SyncService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>At login the cloud app may start after InterfaceSync; don't alarm the player before this.</summary>
    public static readonly TimeSpan DriveMissingGrace = TimeSpan.FromMinutes(3);

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;
    private readonly IProcessList _processes;
    private readonly IUserEnvironment _env;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly string _machineName;
    private readonly SyncTiming? _timing;
    private readonly SettingsStore _settingsStore;
    private readonly StateStore _states;
    private readonly BackupStore _backups;
    private readonly object _gate = new();
    private readonly HashSet<(string version, SyncStatus status)> _notified = [];
    private readonly HashSet<string> _folderNotesLogged = [];
    private string? _lastDetectionSummary;

    private List<VersionSync> _workers = [];
    private DateTime _startedUtc;
    private DateTime? _driveMissingSinceUtc;
    private bool _driveNoticeSent;
    private bool _logNoticeSent;

    public SyncService(IFileSystem fs, IRegistry registry, IProcessList processes, IUserEnvironment env,
        IClock clock, ILog log, AppPaths paths, string machineName, SyncTiming? timing = null)
    {
        _fs = fs;
        _registry = registry;
        _processes = processes;
        _env = env;
        _clock = clock;
        _log = log;
        Paths = paths;
        _machineName = machineName;
        _timing = timing;
        _settingsStore = new SettingsStore(fs, paths.Data);
        _states = new StateStore(fs, paths.Data);
        _backups = new BackupStore(fs, clock, paths.Backups);
        Settings = _settingsStore.Load();
        Watcher = new GameWatcher(processes);
    }

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public GameWatcher Watcher { get; }
    public BackupStore Backups => _backups;
    public WowInstallDetection? Wow { get; private set; }
    public DriveDetectionResult? Drive { get; private set; }
    public IReadOnlyList<GameVersion> Versions { get; private set; } = [];
    public IReadOnlyList<VersionSync> Workers { get { lock (_gate) return _workers.ToList(); } }

    public OverallState State { get; private set; } = OverallState.Waiting;
    public string StateText { get; private set; } = "Starting";

    /// <summary>Raised (on the polling thread) for things the player must act on. Each is raised once per occurrence.</summary>
    public event Action<Notice>? NoticeRaised;

    /// <summary>Raised after every poll and manual action, for the tray and status window to refresh.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        _startedUtc = _clock.UtcNow;
        _log.Info($"{ProductInfo.Name} {ProductInfo.Version} started on {_machineName} as {_env.UserName}.");
        lock (_gate) Redetect(reason: null);
        UpdateOverall();
    }

    /// <summary>Re-reads detection and rebuilds the workers (also called after the player changes settings).</summary>
    public void Reload()
    {
        lock (_gate)
        {
            Settings = _settingsStore.Load();
            Redetect(reason: null);
        }
        UpdateOverall();
    }

    public void SaveSettings(AppSettings settings)
    {
        _settingsStore.Save(settings);
        Reload();
    }

    // ------------------------------------------------------------------ polling

    /// <summary>One poll: follow moved folders, then run every enabled version's sync rules.</summary>
    public void Poll()
    {
        lock (_gate)
        {
            if (PathsMissing(out var what)) Redetect(reason: what);
            CheckDriveNotice();
            bool movedAway = CheckMovedAway();
            foreach (var w in movedAway ? [] : _workers)
            {
                w.Paused = Settings.Paused;
                w.Tick();
                NotifyFor(w);
            }
        }
        CheckLog();
        UpdateOverall();
    }

    public void SyncNow()
    {
        lock (_gate)
        {
            if (PathsMissing(out var what)) Redetect(reason: what);
            bool movedAway = CheckMovedAway();
            foreach (var w in movedAway ? [] : _workers)
            {
                w.SyncNow();
                NotifyFor(w);
            }
        }
        UpdateOverall();
    }

    /// <summary>Syncs one version now (used by the setup window to show each version's first sync).</summary>
    public SyncOutcome SyncNow(VersionSync worker)
    {
        SyncOutcome outcome;
        lock (_gate)
        {
            outcome = worker.SyncNow();
            NotifyFor(worker);
        }
        UpdateOverall();
        return outcome;
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            Settings.Paused = paused;
            _settingsStore.Save(Settings);
            foreach (var w in _workers) w.Paused = paused;
        }
        _log.Info(paused ? "Syncing paused." : "Syncing resumed.");
        UpdateOverall();
    }

    public SyncOutcome Resolve(VersionSync worker, KeepChoice keep)
    {
        SyncOutcome outcome;
        lock (_gate)
        {
            outcome = worker.ResolveConflict(keep);
            _notified.RemoveWhere(n => n.version == worker.Version.FolderName);
        }
        UpdateOverall();
        return outcome;
    }

    public SyncOutcome Restore(VersionSync worker, BackupInfo backup)
    {
        SyncOutcome outcome;
        lock (_gate) outcome = worker.RestoreBackup(backup);
        UpdateOverall();
        return outcome;
    }

    // ------------------------------------------------------------------ changing cloud app

    private MovedMarker? _movedAway;
    private bool _movedNoticeSent;

    /// <summary>True (and nothing syncs) when this PC's cloud folder says its data moved to another app.</summary>
    private bool CheckMovedAway()
    {
        _movedAway = Drive?.MyDrivePath is { } root
            ? MovedMarker.TryRead(_fs, PathUtil.Join(root, CloudLayout.AppFolderName))
            : null;
        if (_movedAway == null) { _movedNoticeSent = false; return false; }
        if (!_movedNoticeSent)
        {
            _movedNoticeSent = true;
            var text = MovedAwayText(_movedAway);
            _log.Warn(text + " Nothing is synced here until this PC is switched too.");
            Raise(new Notice(NoticeKind.SyncFailed, $"Your settings moved to {_movedAway.Provider.DisplayName()}", text));
        }
        return true;
    }

    private string MovedAwayText(MovedMarker m) =>
        $"The settings in {ProviderName} were moved to {m.Provider.DisplayName()}" +
        (m.MovedBy.Length > 0 ? $" by {m.MovedBy} on {m.MovedAtUtc.ToLocalTime():g}" : "") +
        $". Switch this PC too: status window → Change cloud folder → {m.Provider.DisplayName()}.";

    /// <summary>
    /// Moves this PC (and the cloud data) to another cloud app. Pending changes are pushed first; the old
    /// cloud copy is backed up; the data is copied to the new app; a marker in the old folder stops every
    /// PC still using it. If the new app already has the settings (another PC moved first), this PC simply
    /// joins it with the normal first-sync rule. Never leaves two apps in use at once.
    /// </summary>
    public ProviderChangeResult ChangeProvider(CloudProvider target, string? folderOverride, bool keepOnDevice)
    {
        string message;
        lock (_gate)
        {
            var targetApp = CloudProviderCatalog.Create(target, _fs, _registry, _env, Paths.Temp);
            var found = targetApp.Detect(folderOverride);
            if (found.MyDrivePath is not { } newRoot)
                return new(false, $"{targetApp.DisplayName} was not found on this PC. Install and sign in to it first, or choose its folder.");
            var oldRoot = Drive?.MyDrivePath;
            if (oldRoot != null && PathUtil.SamePath(oldRoot, newRoot))
                return new(false, $"{ProductInfo.Name} already uses {newRoot}.");

            foreach (var w in _workers)
                if (Watcher.IsRunning(w.Version)) return new(false, $"Close {w.Version.FolderName} first.");
            if (oldRoot != null && _movedAway == null)
            {
                foreach (var w in _workers)
                {
                    var o = w.SyncNow();
                    if (o is SyncOutcome.Conflict or SyncOutcome.Refused or SyncOutcome.Failed || w.State.PendingPush)
                        return new(false, $"{w.Version.FolderName}: {w.StatusText}. Sort that out first, then change the cloud folder.");
                }
            }

            var newAppFolder = PathUtil.Join(newRoot, CloudLayout.AppFolderName);
            var oldAppFolder = oldRoot != null ? PathUtil.Join(oldRoot, CloudLayout.AppFolderName) : null;

            bool targetHasData = FolderScanner.AllFiles(_fs, newAppFolder).Count > 0;
            var staleMarker = MovedMarker.TryRead(_fs, newAppFolder);
            if (targetHasData && staleMarker == null)
            {
                message = $"{targetApp.DisplayName} already has {ProductInfo.Name} settings (another PC moved there first). " +
                          "This PC now uses them: if they differ from this PC's, they replace this PC's after a backup.";
            }
            else if (oldAppFolder == null || _movedAway != null || FolderScanner.AllFiles(_fs, oldAppFolder).Count == 0)
            {
                message = $"Switched to {targetApp.DisplayName}. There were no settings to move.";
            }
            else
            {
                // Back up both copies first (the target may hold an old copy left by an earlier move away from it).
                if (staleMarker != null)
                    foreach (var v in _fs.EnumerateDirectories(newAppFolder))
                        _backups.Create(PathUtil.Name(v), v, "cloud-before-provider-move-old-copy");
                foreach (var v in _fs.EnumerateDirectories(oldAppFolder))
                    _backups.Create(PathUtil.Name(v), v, "cloud-before-provider-move");

                // Copy files only, and only each version's settings and commit file. Folders in a cloud app's folder
                // are never renamed, moved or deleted; an out-of-date file left over in the target is removed, not its folder.
                int count = 0;
                foreach (var oldVersion in _fs.EnumerateDirectories(oldAppFolder).Where(d => !_fs.IsReparsePoint(d)))
                {
                    var version = PathUtil.Name(oldVersion);
                    var source = new CloudLayout(oldRoot, version, Drive?.Provider ?? CloudProvider.GoogleDrive);
                    var dest = new CloudLayout(newRoot, version, target);
                    if (!dest.IsSafeWriteTarget(out var why))
                        return new(false, $"Refused to write to {dest.Account}: {why}. Nothing was switched.");

                    var files = FolderScanner.AllFiles(_fs, source.Account);
                    TreeCopy.CopyFiles(_fs, source.Account, dest.Account, files);
                    foreach (var f in files)
                        if (_fs.GetFileInfo(PathUtil.FromRelative(dest.Account, f.RelativePath)).Size != f.Size)
                            return new(false, $"Copying to {targetApp.DisplayName} failed ({version}\\{f.RelativePath}). Nothing was switched; the original is untouched.");
                    var keep = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
                    foreach (var extra in FolderScanner.AllFiles(_fs, dest.Account).Where(f => !keep.Contains(f.RelativePath)))
                        _fs.DeleteFile(PathUtil.FromRelative(dest.Account, extra.RelativePath));
                    foreach (var commit in new[] { source.CommitFile, source.LegacyCommitFile })
                        if (_fs.FileExists(commit)) { _fs.CopyFile(commit, dest.CommitFile); break; }
                    count += files.Count;
                }
                if (staleMarker != null) _fs.DeleteFile(MovedMarker.PathIn(newAppFolder));

                _fs.WriteAllText(MovedMarker.PathIn(oldAppFolder), JsonSerializer.Serialize(
                    new MovedMarker { Provider = target, MovedBy = _machineName, MovedAtUtc = _clock.UtcNow }, Json.Options));
                message = $"Moved {count} files from {oldAppFolder} to {newAppFolder} (the old copy was backed up and left in place, " +
                          $"marked as moved, so other PCs stop syncing there until they switch too).";
            }

            if (keepOnDevice && targetApp.CanPinFolder)
            {
                targetApp.PinFolder(_fs, newAppFolder);
                message += $" {newAppFolder} is set to always stay on this device.";
            }

            Settings.CloudProvider = target;
            Settings.DriveOverride = folderOverride;
            _settingsStore.Save(Settings);
            _log.Info(message);
            Settings = _settingsStore.Load();
            Redetect(reason: null);
            _movedAway = null;
            _movedNoticeSent = false;
        }
        UpdateOverall();
        return new(true, message);
    }

    /// <summary>Whether the chosen app lets Interface Sync mark its folder "always keep on this device" (OneDrive).</summary>
    public bool CanKeepOnDevice =>
        Drive is { Found: true } d && CloudProviderCatalog.Create(d.Provider, _fs, _registry, _env, Paths.Temp).CanPinFolder;

    /// <summary>Marks the cloud app's Interface Sync folder "always keep on this device". Only on the player's OK.</summary>
    public string? KeepCloudFolderOnDevice()
    {
        if (Drive is not { Found: true } d) return null;
        var app = CloudProviderCatalog.Create(d.Provider, _fs, _registry, _env, Paths.Temp);
        if (!app.CanPinFolder) return null;
        var folder = PathUtil.Join(d.MyDrivePath!, CloudLayout.AppFolderName);
        app.PinFolder(_fs, folder);
        _log.Info($"{folder} is set to always stay on this device ({app.DisplayName}).");
        return folder;
    }

    // ------------------------------------------------------------------ detection

    private bool PathsMissing(out string what)
    {
        what = "";
        if (Drive?.MyDrivePath is { } d && !_fs.DirectoryExists(d)) { what = $"{ProviderName} folder {d} is gone"; return true; }
        if (Drive is { Found: false }) { what = $"{ProviderName} not found yet"; return true; }
        if (Wow is { Found: true } && Wow.Installs.Any(i => !_fs.DirectoryExists(i.Root))) { what = "a WoW folder is gone"; return true; }
        if (Wow is { Found: false }) { what = "WoW not found yet"; return true; }
        return false;
    }

    private void Redetect(string? reason)
    {
        var oldDrive = Drive?.MyDrivePath;
        var oldWow = Wow?.PrimaryRoot;

        var wowDetector = new WowDetector(_fs, _registry, _env, Paths.Temp);
        Wow = wowDetector.DetectInstalls(Settings.WowInstallOverride);
        Drive = CloudDetector.Detect(EffectiveProvider(), _fs, _registry, _env, Paths.Temp, Settings.DriveOverride);
        Versions = Wow.Found ? wowDetector.ScanVersions(Wow) : [];

        // Log detection only when its result changed (setup saving settings re-runs it several times).
        var summary = string.Join("\n", Wow.Attempts.Concat(Drive.Attempts).Append(DescribeVersions()).Append(Drive.Provider.ToString()));
        bool quiet = summary == _lastDetectionSummary
                     || (reason != null && Drive.MyDrivePath == oldDrive && Wow.PrimaryRoot == oldWow);
        _lastDetectionSummary = summary;
        if (!quiet)
        {
            foreach (var a in Wow.Attempts) _log.Info($"WoW detection: {a}");
            foreach (var a in Drive.Attempts) _log.Info($"Cloud folder detection ({ProviderName}): {a}");
            _log.Info(Wow.Found
                ? $"World of Warcraft: {string.Join("; ", Wow.Installs.Select(i => i.Root))} (from {Wow.Source}). Versions: {DescribeVersions()}"
                : "World of Warcraft was not found. Choose its folder in the status window.");
            _log.Info(Drive.Found
                ? $"{ProviderName}: {Drive.MyDrivePath} ({Drive.Mode}, from {Drive.Source})."
                : $"{ProviderName} was not found. Is the {ProviderName} app running and signed in?");
            if (oldDrive != null && Drive.MyDrivePath != null && !PathUtil.SamePath(oldDrive, Drive.MyDrivePath))
                _log.Warn($"The cloud folder moved from {oldDrive} to {Drive.MyDrivePath}. Following it.");
            if (Drive.Mode == DriveMode.Streaming || (Drive.Found && Drive.Provider != CloudProvider.GoogleDrive))
                _log.Info($"{ProviderName} may keep files online-only. {Drive.Provider.KeepOfflineHint(_env.Os)}");
        }

        if (Drive.MyDrivePath is { } root)
        {
            try { CloudFolderMigration.Run(_fs, root, _backups, _log); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Versions = []; // don't sync into a half-renamed layout; the next poll retries
            }
        }

        Settings.LastDetectedWowInstall = Wow.PrimaryRoot;
        Settings.LastDetectedDrive = Drive.MyDrivePath;
        TrySaveSettings();
        BuildWorkers();
    }

    public string ProviderName => (Drive?.Provider ?? EffectiveProvider()).DisplayName();

    /// <summary>
    /// The chosen cloud app. Settings from builds before the choice existed (setup done, nothing chosen)
    /// keep Google Drive; on a new PC, the first app found (Google Drive, OneDrive, Dropbox) is suggested.
    /// </summary>
    public CloudProvider EffectiveProvider()
    {
        if (Settings.CloudProvider is { } chosen) return chosen;
        if (Settings.SetupComplete) return CloudProvider.GoogleDrive;
        foreach (var p in CloudProviders.All)
            if (CloudDetector.Detect(p, _fs, _registry, _env, Paths.Temp, null).Found) return p;
        return CloudProvider.GoogleDrive;
    }

    /// <summary>What each supported cloud app's detection finds on this PC (for the setup window's choice).</summary>
    public IReadOnlyDictionary<CloudProvider, DriveDetectionResult> DetectAllProviders() =>
        CloudProviders.All.ToDictionary(p => p,
            p => CloudDetector.Detect(p, _fs, _registry, _env, Paths.Temp, p == EffectiveProvider() ? Settings.DriveOverride : null));

    private string DescribeVersions() => Versions.Count == 0
        ? "none"
        : string.Join(", ", Versions.Select(v =>
            $"{v.FolderName}{(v.ClientVersion != null ? " " + v.ClientVersion : "")}{(v.CanEnable ? "" : " (not launched yet)")}{(IsEnabled(v) ? " [on]" : "")}"));

    public bool IsEnabled(GameVersion v) =>
        v.CanEnable && Settings.EnabledVersions.TryGetValue(v.FolderName, out var on) && on;

    private void BuildWorkers()
    {
        var workers = new List<VersionSync>();
        if (Drive?.MyDrivePath is { } myDrive)
        {
            foreach (var v in Versions.Where(IsEnabled))
            {
                var existing = _workers.FirstOrDefault(w =>
                    PathUtil.SamePath(w.Version.FolderPath, v.FolderPath) && PathUtil.SamePath(w.Cloud.MyDrive, myDrive));
                if (existing != null) { workers.Add(existing); continue; }

                var version = v;
                var folderName = CloudLayout.ResolveFolderName(_fs, myDrive, v.FolderName, Settings, out var adopted, out var note);
                if (adopted != null)
                {
                    Settings.CloudFolderNames[v.FolderName] = adopted;
                    TrySaveSettings();
                }
                if (note != null && _folderNotesLogged.Add(note)) _log.Warn(note);
                var cloud = new CloudLayout(myDrive, folderName, Drive.Provider);
                workers.Add(new VersionSync(version, cloud, _fs, _clock, _backups, _states, _log, _machineName,
                    () => Watcher.IsRunning(version), _timing) { Paused = Settings.Paused });
            }
        }
        _workers = workers;
    }

    // ------------------------------------------------------------------ status and notices

    private void CheckDriveNotice()
    {
        if (Drive is { Found: true })
        {
            _driveMissingSinceUtc = null;
            _driveNoticeSent = false;
            return;
        }
        _driveMissingSinceUtc ??= _clock.UtcNow;
        var since = _driveMissingSinceUtc.Value > _startedUtc ? _driveMissingSinceUtc.Value : _startedUtc;
        if (!_driveNoticeSent && _clock.UtcNow - since >= DriveMissingGrace)
        {
            _driveNoticeSent = true;
            Raise(new Notice(NoticeKind.DriveNotFound, $"{ProviderName} not found",
                $"{ProductInfo.Name} can't find your {ProviderName} folder. Check that the {ProviderName} app is running and signed in."));
        }
    }

    private void CheckLog()
    {
        if (_log.WriteFailure is { } failure)
        {
            if (!_logNoticeSent) Raise(new Notice(NoticeKind.LogFailed, $"{ProductInfo.Name} can't write its log", failure));
            _logNoticeSent = true;
        }
        else _logNoticeSent = false;
    }

    private void NotifyFor(VersionSync w)
    {
        var key = (w.Version.FolderName, w.Status);
        if (w.Status is SyncStatus.NeedsAttention or SyncStatus.Error)
        {
            if (!_notified.Add(key)) return;
            var kind = w.State.Conflict != null ? NoticeKind.Conflict : NoticeKind.SyncFailed;
            var title = kind == NoticeKind.Conflict ? $"{w.Version.Key}: choose which settings to keep" : $"{w.Version.Key}: sync problem";
            Raise(new Notice(kind, title, w.StatusText));
        }
        else
        {
            _notified.RemoveWhere(n => n.version == w.Version.FolderName);
        }
    }

    private void Raise(Notice notice)
    {
        try { NoticeRaised?.Invoke(notice); }
        catch (Exception e) { _log.Warn($"Could not show a notification: {e.Message}"); }
    }

    private void UpdateOverall()
    {
        List<VersionSync> workers;
        lock (_gate) workers = _workers.ToList();

        (OverallState state, string text) = (OverallState.InSync, "In sync");
        if (Settings.Paused) (state, text) = (OverallState.Paused, "Paused");
        else if (workers.Any(w => w.Status == SyncStatus.Syncing)) (state, text) = (OverallState.Syncing, "Syncing");
        else if (workers.Any(w => w.Status is SyncStatus.GameRunning or SyncStatus.WaitingAfterClose or SyncStatus.WaitingForCloud))
            (state, text) = (OverallState.Waiting, workers.Any(w => w.Status == SyncStatus.GameRunning) ? "Waiting: game running" : "Waiting");

        // Attention overrides everything except an explicit pause of normal syncing.
        if (_movedAway != null) (state, text) = (OverallState.NeedsAttention, $"Settings moved to {_movedAway.Provider.DisplayName()}: switch this PC too");
        else if (Wow is { Found: false }) (state, text) = (OverallState.NeedsAttention, "World of Warcraft not found");
        else if (Drive is { Found: false } && _driveMissingSinceUtc is { } since && _clock.UtcNow - since >= DriveMissingGrace)
            (state, text) = (OverallState.NeedsAttention, $"{ProviderName} not found");
        else if (Drive is { Found: false }) (state, text) = (OverallState.Waiting, $"Waiting for {ProviderName}");
        else if (workers.FirstOrDefault(w => w.Status is SyncStatus.NeedsAttention or SyncStatus.Error) is { } bad)
            (state, text) = (OverallState.NeedsAttention, $"{bad.Version.Key}: {bad.StatusText}");
        else if (_log.WriteFailure != null) (state, text) = (OverallState.NeedsAttention, "Log can't be written");
        else if (Wow is { Found: true } && workers.Count == 0 && Drive is { Found: true })
            (state, text) = (OverallState.NeedsAttention, "No game version is switched on");

        State = state;
        StateText = text;
        try { Changed?.Invoke(); } catch (Exception e) { _log.Warn($"Status refresh failed: {e.Message}"); }
    }

    private void TrySaveSettings()
    {
        try { _settingsStore.Save(Settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not save settings: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ diagnostics

    public IReadOnlyList<LogLine> RecentLog(int max = 200) => _log.Recent(max);

    /// <summary>Paths, versions and the last 200 log lines, for "Copy diagnostics".</summary>
    public string Diagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{ProductInfo.Name} {ProductInfo.Version} on {_machineName} ({Environment.OSVersion}), user {_env.UserName}, elevated: {_env.IsElevated}");
        sb.AppendLine($"Data folder: {Paths.Data}");
        sb.AppendLine($"State: {State} - {StateText}; paused: {Settings.Paused}");
        sb.AppendLine();
        sb.AppendLine($"World of Warcraft ({Wow?.Source}):");
        foreach (var i in Wow?.Installs ?? []) sb.AppendLine($"  {i.Root} [{string.Join(", ", i.Products.Select(p => $"{p.Uid}:{p.GameFolder}:{p.Version}"))}]");
        foreach (var a in Wow?.Attempts ?? []) sb.AppendLine($"  - {a}");
        sb.AppendLine($"{ProviderName} ({Drive?.Source}, {Drive?.Mode}): {Drive?.MyDrivePath ?? "not found"}");
        foreach (var a in Drive?.Attempts ?? []) sb.AppendLine($"  - {a}");
        sb.AppendLine();
        sb.AppendLine("Game versions:");
        foreach (var v in Versions)
            sb.AppendLine($"  {v.FolderName}: {(IsEnabled(v) ? "on" : "off")}, launched: {v.CanEnable}, product: {v.ProductUid ?? "-"} {v.ClientVersion}");
        foreach (var w in Workers)
        {
            var s = w.State;
            sb.AppendLine($"  {w.Version.FolderName}: {w.Status} - {w.StatusText}; cloud {w.Cloud.Account}; joined {s.Joined}; pending push {s.PendingPush}; " +
                          $"last push {s.LastPushUtc?.ToLocalTime():g}; last pull {s.LastPullUtc?.ToLocalTime():g}; conflict {(s.Conflict != null ? s.Conflict.Reason : "none")}; last error {s.LastError ?? "none"}");
        }
        sb.AppendLine();
        sb.AppendLine("Last 200 log lines:");
        foreach (var line in _log.Recent(200)) sb.AppendLine(line.ToString());
        return sb.ToString();
    }
}

