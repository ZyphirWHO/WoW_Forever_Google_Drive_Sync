using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "interfacesync-service", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc));
    private readonly FakeProcessList _processes = new();
    private readonly FakeEnvironment _env;
    private readonly string _wow;
    private readonly PhysicalFileSystem _fs = new();
    private readonly List<Notice> _notices = [];

    public SyncServiceTests()
    {
        _env = new FakeEnvironment
        {
            LocalAppData = Path.Combine(_root, "LocalAppData"),
            UserProfile = Path.Combine(_root, "Users", "Player"),
            ProgramData = Path.Combine(_root, "ProgramData"),
        };
        _wow = Path.Combine(_root, "World of Warcraft");
        foreach (var v in new[] { "_retail_", "_forever_" })
        {
            Directory.CreateDirectory(Path.Combine(_wow, v, "WTF", "Account", "PLAYER#1"));
            File.WriteAllText(Path.Combine(_wow, v, "Wow.exe"), "exe");
            File.WriteAllText(Path.Combine(_wow, v, "WTF", "Account", "PLAYER#1", "macros-cache.txt"), v);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MirrorDrive => Path.Combine(_env.UserProfile, "My Drive");

    private SyncService Service(Action<AppSettings>? configure = null)
    {
        var paths = new AppPaths(_env.LocalAppData);
        var settings = new AppSettings { WowInstallOverride = _wow, SetupComplete = true };
        settings.EnabledVersions["_retail_"] = true;
        settings.EnabledVersions["_forever_"] = true;
        configure?.Invoke(settings);
        new SettingsStore(_fs, paths.Data).Save(settings);
        var service = new SyncService(_fs, new FakeRegistry(), _processes, _env, _clock, new MemoryLog(), paths, "HomePC");
        service.NoticeRaised += _notices.Add;
        return service;
    }

    private void Launch(string version) =>
        _processes.Processes.Add(new ProcessEntry(100, "Wow", Path.Combine(_wow, version, "Wow.exe")));

    private void PollAfter(SyncService s, TimeSpan wait)
    {
        _clock.Advance(wait);
        s.Poll();
    }

    [Fact]
    public void Runs_each_enabled_version_on_its_own_and_one_running_does_not_block_the_other()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service();
        s.Start();
        Assert.Equal(2, s.Workers.Count);

        s.Poll();                                            // first sync: cloud empty, both pushed
        Assert.True(File.Exists(Path.Combine(MirrorDrive, "InterfaceSync", "_retail_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
        Assert.True(File.Exists(Path.Combine(MirrorDrive, "InterfaceSync", "_forever_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
        Assert.Equal(OverallState.InSync, s.State);

        Launch("_forever_");                                 // Forever runs; retail is closed
        PollAfter(s, TimeSpan.FromSeconds(15));
        Assert.Equal(OverallState.Waiting, s.State);
        Assert.Equal(SyncStatus.GameRunning, s.Workers.Single(w => w.Version.FolderName == "_forever_").Status);
        Assert.Equal(SyncStatus.Idle, s.Workers.Single(w => w.Version.FolderName == "_retail_").Status);

        File.WriteAllText(Path.Combine(_wow, "_retail_", "WTF", "Account", "PLAYER#1", "macros-cache.txt"), "retail changed");
        Assert.Equal(SyncOutcome.Pushed, s.Workers.Single(w => w.Version.FolderName == "_retail_").SyncNow());

        _processes.Processes.Clear();
        File.WriteAllText(Path.Combine(_wow, "_forever_", "WTF", "Account", "PLAYER#1", "macros-cache.txt"), "forever changed");
        PollAfter(s, TimeSpan.FromSeconds(15));             // exit seen
        PollAfter(s, TimeSpan.FromSeconds(15));             // pushed
        Assert.Equal("forever changed",
            File.ReadAllText(Path.Combine(MirrorDrive, "InterfaceSync", "_forever_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
        Assert.Equal(OverallState.InSync, s.State);
    }

    [Fact]
    public void Only_versions_switched_on_are_synced()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service(c => c.EnabledVersions["_forever_"] = false);
        s.Start();
        s.Poll();
        Assert.Equal(["_retail_"], s.Workers.Select(w => w.Version.FolderName));
        Assert.False(Directory.Exists(Path.Combine(MirrorDrive, "InterfaceSync", "_forever_")));

        var none = Service(c => c.EnabledVersions.Clear());
        none.Start();
        Assert.Equal((OverallState.NeedsAttention, "No game version is switched on"), (none.State, none.StateText));
    }

    [Fact]
    public void Drive_not_yet_mounted_at_login_waits_quietly_then_syncs_when_it_appears()
    {
        var s = Service();
        s.Start();
        Assert.Equal(OverallState.Waiting, s.State);
        PollAfter(s, TimeSpan.FromMinutes(1));
        Assert.Empty(_notices);                              // within the grace period

        Directory.CreateDirectory(MirrorDrive);              // Drive for desktop finishes starting
        PollAfter(s, TimeSpan.FromSeconds(15));
        Assert.Equal(2, s.Workers.Count);
        Assert.True(Directory.Exists(Path.Combine(MirrorDrive, "InterfaceSync", "_retail_", "WTF-Account")));
        Assert.Empty(_notices);
    }

    [Fact]
    public void Drive_missing_for_three_minutes_raises_one_notice()
    {
        var s = Service();
        s.Start();
        for (int i = 0; i < 16; i++) PollAfter(s, TimeSpan.FromSeconds(15));
        Assert.Equal(OverallState.NeedsAttention, s.State);
        var notice = Assert.Single(_notices);
        Assert.Equal(NoticeKind.DriveNotFound, notice.Kind);
    }

    [Fact]
    public void Drive_folder_moved_while_running_is_followed()
    {
        var oldDrive = Path.Combine(_root, "OldDrive", "My Drive");
        Directory.CreateDirectory(oldDrive);
        var s = Service(c => c.DriveOverride = oldDrive);
        s.Start();
        s.Poll();
        Assert.True(Directory.Exists(Path.Combine(oldDrive, "InterfaceSync", "_retail_", "WTF-Account")));

        // Player switches Drive to mirror mode: the old folder disappears, contents arrive in %USERPROFILE%\My Drive.
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorDrive)!);
        Directory.Move(oldDrive, MirrorDrive);
        PollAfter(s, TimeSpan.FromSeconds(15));

        Assert.Equal(MirrorDrive, s.Drive!.MyDrivePath);
        Assert.All(s.Workers, w => Assert.StartsWith(MirrorDrive, w.Cloud.Account));
        Assert.Equal(OverallState.InSync, s.State);
    }

    [Fact]
    public void Conflict_raises_a_single_notice_until_resolved()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service(c => c.EnabledVersions["_forever_"] = false);
        s.Start();
        s.Poll();
        var retail = s.Workers.Single();

        // Another PC pushes while this PC changes settings too.
        var commit = CommitFile.TryRead(_fs, retail.Cloud.CommitFile)!;
        commit.CommitId = "other-pc";
        commit.PushedBy = "WorkLaptop";
        commit.Write(_fs, retail.Cloud.CommitFile);
        File.WriteAllText(Path.Combine(_wow, "_retail_", "WTF", "Account", "PLAYER#1", "macros-cache.txt"), "local edit");

        PollAfter(s, TimeSpan.FromSeconds(15));
        PollAfter(s, TimeSpan.FromSeconds(15));
        Assert.Equal(OverallState.NeedsAttention, s.State);
        Assert.Equal(NoticeKind.Conflict, Assert.Single(_notices).Kind);

        Assert.Equal(SyncOutcome.Pushed, s.Resolve(retail, KeepChoice.Local));
        Assert.Equal(OverallState.InSync, s.State);
    }

    [Fact]
    public void V1_cloud_folder_named_without_underscores_is_found_and_remembered()
    {
        var legacy = Path.Combine(MirrorDrive, "InterfaceSync", "forever", "WTF-Account", "PLAYER#1");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "macros-cache.txt"), "v1.1 forever settings");
        var s = Service(c => c.EnabledVersions["_retail_"] = false);
        s.Start();
        var forever = s.Workers.Single();
        Assert.Equal(Path.Combine(MirrorDrive, "InterfaceSync", "forever", "WTF-Account"), forever.Cloud.Account);
        Assert.Equal("forever", new SettingsStore(_fs, s.Paths.Data).Load().CloudFolderNames["_forever_"]);

        s.Poll();                                             // first sync: the v1.1 data wins
        Assert.Equal("v1.1 forever settings",
            File.ReadAllText(Path.Combine(_wow, "_forever_", "WTF", "Account", "PLAYER#1", "macros-cache.txt")));
        Assert.False(Directory.Exists(Path.Combine(MirrorDrive, "InterfaceSync", "_forever_")));
    }

    [Fact]
    public void When_both_folder_names_hold_settings_the_2_0_one_is_used_and_the_player_is_told()
    {
        foreach (var name in new[] { "forever", "_forever_" })
        {
            var dir = Path.Combine(MirrorDrive, "InterfaceSync", name, "WTF-Account");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "macros-cache.txt"), name);
        }
        var log = new MemoryLog();
        var paths = new AppPaths(_env.LocalAppData);
        var settings = new AppSettings { WowInstallOverride = _wow, SetupComplete = true };
        settings.EnabledVersions["_forever_"] = true;
        new SettingsStore(_fs, paths.Data).Save(settings);
        var s = new SyncService(_fs, new FakeRegistry(), _processes, _env, _clock, log, paths, "HomePC");
        s.Start();

        Assert.EndsWith(Path.Combine("_forever_", "WTF-Account"), s.Workers.Single().Cloud.Account);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("both") && l.Message.Contains("forever"));
    }

    [Fact]
    public void Every_detection_attempt_is_written_to_the_log()
    {
        Directory.CreateDirectory(MirrorDrive);
        var log = new MemoryLog();
        var paths = new AppPaths(_env.LocalAppData);
        new SettingsStore(_fs, paths.Data).Save(new AppSettings { WowInstallOverride = _wow, SetupComplete = true });
        var s = new SyncService(_fs, new FakeRegistry(), _processes, _env, _clock, log, paths, "HomePC");
        s.Start();

        Assert.Contains(log.Lines, l => l.Message.StartsWith("Cloud folder detection (Google Drive): ") && l.Message.Contains("registry"));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Cloud folder detection (Google Drive): ") && l.Message.Contains("root_preference_sqlite.db"));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Cloud folder detection (Google Drive): Mirrored folder: found"));
    }

    [Fact]
    public void New_pc_suggests_the_first_cloud_app_found_and_settings_from_earlier_builds_keep_google_drive()
    {
        Directory.CreateDirectory(Path.Combine(_env.UserProfile, "Dropbox"));
        var paths = new AppPaths(_env.LocalAppData);
        new SettingsStore(_fs, paths.Data).Save(new AppSettings { WowInstallOverride = _wow });
        var fresh = new SyncService(_fs, new FakeRegistry(), _processes, _env, _clock, new MemoryLog(), paths, "HomePC");
        fresh.Start();
        Assert.Equal(InterfaceSync.Core.Detection.CloudProvider.Dropbox, fresh.Drive!.Provider);
        Assert.Equal("Dropbox", fresh.ProviderName);

        Directory.CreateDirectory(MirrorDrive);
        var upgraded = Service();                     // SetupComplete, no provider recorded (earlier build)
        upgraded.Start();
        Assert.Equal(InterfaceSync.Core.Detection.CloudProvider.GoogleDrive, upgraded.Drive!.Provider);
        Assert.Equal(MirrorDrive, upgraded.Drive.MyDrivePath);
    }

    [Fact]
    public void Choosing_onedrive_syncs_into_the_onedrive_folder()
    {
        var oneDrive = Path.Combine(_env.UserProfile, "OneDrive");
        Directory.CreateDirectory(oneDrive);
        var s = Service(c => { c.CloudProvider = InterfaceSync.Core.Detection.CloudProvider.OneDrive; c.EnabledVersions["_retail_"] = false; });
        s.Start();
        s.Poll();
        Assert.True(File.Exists(Path.Combine(oneDrive, "InterfaceSync", "_forever_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
        Assert.Equal("OneDrive", s.Workers.Single().Cloud.ProviderName);
    }

    [Fact]
    public void Detection_is_logged_once_not_on_every_settings_save()
    {
        Directory.CreateDirectory(MirrorDrive);
        var log = new MemoryLog();
        var paths = new AppPaths(_env.LocalAppData);
        new SettingsStore(_fs, paths.Data).Save(new AppSettings { WowInstallOverride = _wow });
        var s = new SyncService(_fs, new FakeRegistry(), _processes, _env, _clock, log, paths, "HomePC");
        s.Start();
        s.SaveSettings(s.Settings);                 // setup saves settings a few times
        s.SaveSettings(s.Settings);
        s.Reload();

        Assert.Single(log.Lines, l => l.Message.StartsWith("Cloud folder detection (Google Drive): Mirrored folder: found"));
        Assert.Single(log.Lines, l => l.Message.StartsWith("World of Warcraft: "));
    }

    [Fact]
    public void Setup_choice_is_applied_once_and_then_forgotten()
    {
        Directory.CreateDirectory(MirrorDrive);
        var cloud = Path.Combine(MirrorDrive, "InterfaceSync", "_forever_", "WTF-Account", "PLAYER#1");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "macros-cache.txt"), "already in the cloud");
        File.SetLastWriteTimeUtc(Path.Combine(cloud, "macros-cache.txt"), _clock.UtcNow.AddDays(1));   // cloud newer
        var s = Service(c =>
        {
            c.EnabledVersions["_retail_"] = false;
            c.FirstSyncChoices["_forever_"] = FirstSyncChoice.UseThisComputer;
        });
        s.Start();
        var preview = Assert.Single(s.PreviewFirstSync(), p => p.Version == "_forever_");
        Assert.True(preview.NeedsChoice);

        s.Poll();

        Assert.Equal("_forever_", File.ReadAllText(Path.Combine(cloud, "macros-cache.txt")));            // this PC's uploaded
        Assert.False(new SettingsStore(_fs, s.Paths.Data).Load().FirstSyncChoices.ContainsKey("_forever_"));
    }

    [Fact]
    public void Diagnostics_include_paths_versions_and_log_lines()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service();
        s.Start();
        s.Poll();
        var text = s.Diagnostics();
        Assert.Contains(_wow, text);
        Assert.Contains(MirrorDrive, text);
        Assert.Contains("_forever_: on", text);
        Assert.Contains("Last 200 log lines", text);
        Assert.Contains("pushed settings", text);
    }

    [Fact]
    public void A_copy_of_the_cloud_folder_is_pointed_out_once_and_can_be_hidden()
    {
        Directory.CreateDirectory(MirrorDrive);
        var service = Service();
        service.Start();
        service.Poll();
        Assert.Empty(service.Duplicates);

        Directory.CreateDirectory(Path.Combine(MirrorDrive, "InterfaceSync (1)", "_retail_", "WTF-Account"));
        File.WriteAllText(Path.Combine(MirrorDrive, "InterfaceSync (1)", "_retail_", "WTF-Account", "x.lua"), "x");
        _clock.Advance(SyncService.DuplicateCheckEvery);
        service.Poll();
        var copy = Assert.Single(service.Duplicates);
        Assert.Equal("InterfaceSync (1)", copy.Name);
        Assert.True(Directory.Exists(copy.Path));                                       // nothing moved or deleted

        service.HideDuplicate(copy);
        Assert.Empty(service.Duplicates);
        var again = new SyncService(_fs, new FakeRegistry(), _processes, _env, _clock, new MemoryLog(), new AppPaths(_env.LocalAppData), "HomePC");   // restart
        again.Start();
        Assert.Empty(again.Duplicates);
    }

    [Fact]
    public void A_timed_pause_ends_by_itself_and_an_open_ended_one_does_not()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service();
        s.Start();
        s.SetPaused(true, _clock.UtcNow.AddHours(1));
        s.Poll();
        Assert.Equal(OverallState.Paused, s.State);
        Assert.StartsWith("Paused until", s.StateText);

        PollAfter(s, TimeSpan.FromMinutes(61));
        Assert.False(s.Settings.Paused);
        Assert.Null(s.Settings.PausedUntilUtc);
        Assert.NotEqual(OverallState.Paused, s.State);

        s.SetPaused(true);
        PollAfter(s, TimeSpan.FromDays(3));
        Assert.True(s.Settings.Paused);
        Assert.Equal("Paused", s.StateText);
    }

    [Fact]
    public void Syncs_land_in_the_activity_timeline_and_the_computers_list()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service();
        s.Start();
        s.Poll();
        var activity = s.Activity.List();
        Assert.Equal(2, activity.Count(a => a.Kind == ActivityKind.Uploaded));
        var me = Assert.Single(s.Computers());
        Assert.True(me.IsThisComputer);
        Assert.Equal(2, me.Versions.Count);
    }

    [Fact]
    public void Whats_new_shows_once_after_an_update_but_not_on_a_new_install()
    {
        Directory.CreateDirectory(MirrorDrive);
        var fresh = Service(s => s.SetupComplete = false);
        Assert.Null(fresh.TakeWhatsNew());                       // new install: recorded, not shown

        var upgraded = Service();                                // set up by an earlier version, never saw these notes
        Assert.NotNull(upgraded.TakeWhatsNew());
        Assert.Null(upgraded.TakeWhatsNew());                    // only once
        Assert.Equal(ReleaseNotes.BaseVersion(ProductInfo.DisplayVersion), upgraded.Settings.LastSeenVersion);
    }

    [Fact]
    public void Report_a_problem_hides_paths_and_names()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service();
        s.Start();
        s.Poll();
        var text = s.RedactedDiagnostics();
        Assert.DoesNotContain(_root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HomePC", text);
        Assert.Contains("<this computer>", text);
        Assert.Contains("<World of Warcraft folder>", text);
    }

    [Fact]
    public void A_restore_can_be_undone_exactly()
    {
        Directory.CreateDirectory(MirrorDrive);
        var s = Service();
        s.Start();
        s.Poll();
        var w = s.Workers.Single(x => x.Version.FolderName == "_retail_");
        var file = Path.Combine(_wow, "_retail_", "WTF", "Account", "PLAYER#1", "macros-cache.txt");
        var snap = s.SaveSnapshot(w, "Old")!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        File.WriteAllText(file, "new");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "added.txt"), "only after");

        var (outcome, undo) = s.RestoreWithUndo(w, snap);
        Assert.True(outcome is SyncOutcome.Pushed or SyncOutcome.NothingToDo or SyncOutcome.Waiting, outcome.ToString());
        Assert.Equal("_retail_", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "added.txt")));

        Assert.NotNull(undo);
        s.Restore(w, undo!);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "added.txt")));
    }
}
