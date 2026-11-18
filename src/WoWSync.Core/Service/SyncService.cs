using WoWSync.Core.Abstractions;
using WoWSync.Core.Backup;
using WoWSync.Core.Detection;
using WoWSync.Core.Logging;
using WoWSync.Core.Settings;
using WoWSync.Core.Sync;

namespace WoWSync.Core.Service;

/// <summary>Where WoWSync keeps its own files: %LOCALAPPDATA%\WoWSync.</summary>
public sealed class AppPaths
{
    public AppPaths(string localAppData)
    {
        Data = PathUtil.Join(localAppData, "WoWSync");
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

    /// <summary>At login Google Drive may mount after WoWSync starts; don't alarm the player before this.</summary>
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
        _log.Info($"WoWSync {AppInfo.Version} started on {_machineName} as {_env.UserName}.");
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
            foreach (var w in _workers)
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
            foreach (var w in _workers)
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

    // ------------------------------------------------------------------ detection

    private bool PathsMissing(out string what)
    {
        what = "";
        if (Drive?.MyDrivePath is { } d && !_fs.DirectoryExists(d)) { what = $"Google Drive folder {d} is gone"; return true; }
        if (Drive is { Found: false }) { what = "Google Drive not found yet"; return true; }
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
        Drive = new DriveDetector(_fs, _registry, _env, Paths.Temp).Detect(Settings.DriveOverride);
        Versions = Wow.Found ? wowDetector.ScanVersions(Wow) : [];

        bool quiet = reason != null && Drive.MyDrivePath == oldDrive && Wow.PrimaryRoot == oldWow;
        if (!quiet)
        {
            foreach (var a in Wow.Attempts) _log.Info($"WoW detection: {a}");
            foreach (var a in Drive.Attempts) _log.Info($"Drive detection: {a}");
            _log.Info(Wow.Found
                ? $"World of Warcraft: {string.Join("; ", Wow.Installs.Select(i => i.Root))} (from {Wow.Source}). Versions: {DescribeVersions()}"
                : "World of Warcraft was not found. Choose its folder in the status window.");
            _log.Info(Drive.Found
                ? $"Google Drive: {Drive.MyDrivePath} ({Drive.Mode}, from {Drive.Source})."
                : "Google Drive was not found. Is Google Drive for desktop running and signed in?");
            if (oldDrive != null && Drive.MyDrivePath != null && !PathUtil.SamePath(oldDrive, Drive.MyDrivePath))
                _log.Warn($"Google Drive moved from {oldDrive} to {Drive.MyDrivePath}. Following it.");
            if (Drive.Mode == DriveMode.Streaming)
                _log.Info("Google Drive is streaming: set My Drive\\WoWSync to 'Available offline' so settings are on this PC when needed.");
        }

        Settings.LastDetectedWowInstall = Wow.PrimaryRoot;
        Settings.LastDetectedDrive = Drive.MyDrivePath;
        TrySaveSettings();
        BuildWorkers();
    }

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
                var cloud = new CloudLayout(myDrive, folderName);
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
            Raise(new Notice(NoticeKind.DriveNotFound, "Google Drive not found",
                "WoWSync can't find your Google Drive folder. Check that Google Drive for desktop is running and signed in."));
        }
    }

    private void CheckLog()
    {
        if (_log.WriteFailure is { } failure)
        {
            if (!_logNoticeSent) Raise(new Notice(NoticeKind.LogFailed, "WoWSync can't write its log", failure));
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
        if (Wow is { Found: false }) (state, text) = (OverallState.NeedsAttention, "World of Warcraft not found");
        else if (Drive is { Found: false } && _driveMissingSinceUtc is { } since && _clock.UtcNow - since >= DriveMissingGrace)
            (state, text) = (OverallState.NeedsAttention, "Google Drive not found");
        else if (Drive is { Found: false }) (state, text) = (OverallState.Waiting, "Waiting for Google Drive");
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
        sb.AppendLine($"WoWSync {AppInfo.Version} on {_machineName} ({Environment.OSVersion}), user {_env.UserName}, elevated: {_env.IsElevated}");
        sb.AppendLine($"Data folder: {Paths.Data}");
        sb.AppendLine($"State: {State} - {StateText}; paused: {Settings.Paused}");
        sb.AppendLine();
        sb.AppendLine($"World of Warcraft ({Wow?.Source}):");
        foreach (var i in Wow?.Installs ?? []) sb.AppendLine($"  {i.Root} [{string.Join(", ", i.Products.Select(p => $"{p.Uid}:{p.GameFolder}:{p.Version}"))}]");
        foreach (var a in Wow?.Attempts ?? []) sb.AppendLine($"  - {a}");
        sb.AppendLine($"Google Drive ({Drive?.Source}, {Drive?.Mode}): {Drive?.MyDrivePath ?? "not found"}");
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

public static class AppInfo
{
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
}
