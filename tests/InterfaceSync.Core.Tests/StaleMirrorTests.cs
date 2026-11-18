using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>
/// The home PC's first run (MIKE-M3, 2026-11-18 16:28): its Google Drive copy was hours out of date, it renamed a
/// cloud folder the laptop had already renamed (Drive then scattered files into the root of My Drive), and it pulled
/// the stale copy over newer settings. These tests pin the rules that prevent each step.
/// </summary>
public sealed class StaleMirrorTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    private string Legacy(string name = "WoWSync") => Path.Combine(_world.MyDrive, name);

    private void WriteCloudFile(string root, string relative, string contents, DateTime stamp)
    {
        var path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, stamp);
    }

    private static string[] Snapshot(string folder) =>
        Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();

    // ---------------------------------------------------------------- 1. never rename, move or delete cloud folders

    [Theory]
    [InlineData("WoWSync")]
    [InlineData("WoW Sync")]
    public void Old_cloud_folder_is_copied_not_renamed_and_left_with_a_readme(string oldName)
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        // Make it look as an earlier build left it: <cloud>\WoWSync with the old commit file name, plus v1.1 AddOns.
        var newRoot = Path.Combine(_world.MyDrive, "InterfaceSync");
        var oldRoot = Legacy(oldName);
        Directory.Move(newRoot, oldRoot);
        File.Move(Path.Combine(oldRoot, "_retail_", "WTF-Account.interfacesync.json"), Path.Combine(oldRoot, "_retail_", "WTF-Account.wowsync.json"));
        WriteCloudFile(oldRoot, @"_retail_\AddOns\Details\Details.toc", "v1.1 addon", _world.Clock.UtcNow);
        var before = Snapshot(oldRoot);

        var log = new MemoryLog();
        CloudFolderMigration.Run(new PhysicalFileSystem(), _world.MyDrive, a.Backups, log);

        // Old folder: everything still there, only a README added.
        Assert.Equal(before.Append(Path.Combine(oldRoot, CloudFolderMigration.ReadmeName)).OrderBy(p => p), Snapshot(oldRoot));
        // New folder: the settings and the commit file under its current name; never AddOns.
        Assert.Equal("M1", File.ReadAllText(Path.Combine(newRoot, "_retail_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
        Assert.True(File.Exists(Path.Combine(newRoot, "_retail_", "WTF-Account.interfacesync.json")));
        Assert.False(Directory.Exists(Path.Combine(newRoot, "_retail_", "AddOns")));
        Assert.Contains(log.Lines, l => l.Message.Contains("left as it was"));

        // Same commit id: the PC carries on without downloading anything.
        a.Restart();
        Assert.Equal(SyncOutcome.NothingToDo, a.Sync.SyncNow());
    }

    [Fact]
    public void When_both_folder_names_exist_the_old_one_is_never_touched()
    {
        WriteCloudFile(Legacy(), @"_retail_\WTF-Account\PLAYER#1\macros-cache.txt", "old", _world.Clock.UtcNow);
        Directory.CreateDirectory(Path.Combine(_world.MyDrive, "InterfaceSync"));
        var before = Snapshot(Legacy());

        CloudFolderMigration.Run(new PhysicalFileSystem(), _world.MyDrive, _world.Pc("HomePC").Backups, new MemoryLog());

        Assert.Equal(before, Snapshot(Legacy()));                          // no README, no changes at all
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_world.MyDrive, "InterfaceSync")));
    }

    // ---------------------------------------------------------------- 2. never trust an out-of-date cloud copy

    [Fact]
    public void Stale_mirror_of_an_already_renamed_folder_asks_instead_of_pulling_and_writes_nothing()
    {
        // The home PC: its Drive copy still shows the old WoWSync folder with v1.1 data from days ago,
        // while this PC's own settings were played today.
        var home = _world.Pc("MIKE-M3", "_forever_");
        WriteCloudFile(Legacy(), @"_forever_\WTF-Account\PLAYER#1\macros-cache.txt", "stale v1.1 copy", _world.Clock.UtcNow.AddDays(-3));
        home.WriteSetting(@"PLAYER#1\macros-cache.txt", "played today");
        var oldBefore = Snapshot(Legacy());

        CloudFolderMigration.Run(new PhysicalFileSystem(), _world.MyDrive, home.Backups, new MemoryLog());
        var outcome = home.Sync.Tick();

        Assert.Equal(SyncOutcome.Conflict, outcome);                                        // asks, with both dates
        var c = home.Sync.State.Conflict!;
        Assert.Contains("newer than the cloud's", c.Reason);
        Assert.Equal("MIKE-M3", c.LocalMachine);
        Assert.Equal("played today", home.ReadSetting(@"PLAYER#1\macros-cache.txt"));      // nothing pulled over it
        Assert.Equal(oldBefore.Append(Path.Combine(Legacy(), CloudFolderMigration.ReadmeName)).OrderBy(p => p), Snapshot(Legacy()));
        Assert.False(File.Exists(Path.Combine(_world.MyDrive, "InterfaceSync", "_forever_", "WTF-Account.interfacesync.json")));
    }

    [Fact]
    public void Cloud_copy_without_a_commit_file_while_another_pc_has_synced_elsewhere_is_stale_and_stops()
    {
        var laptop = _world.Pc("TLIT-LT-001", "_retail_");
        laptop.WriteSetting(@"PLAYER#1\macros-cache.txt", "laptop");
        Assert.Equal(SyncOutcome.Pushed, laptop.Sync.Tick());                               // a commit from another PC exists

        var home = _world.Pc("MIKE-M3", "_forever_");
        WriteCloudFile(Path.Combine(_world.MyDrive, "InterfaceSync"), @"_forever_\WTF-Account\PLAYER#1\macros-cache.txt", "no commit here", _world.Clock.UtcNow.AddDays(1));
        home.WriteSetting(@"PLAYER#1\macros-cache.txt", "home's own");

        Assert.Equal(SyncOutcome.Refused, home.Sync.Tick());
        Assert.Contains("looks out of date", home.Sync.StatusText);
        Assert.Contains("TLIT-LT-001", home.Sync.StatusText);
        Assert.Equal("home's own", home.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.False(File.Exists(Path.Combine(_world.MyDrive, "InterfaceSync", "_forever_", "WTF-Account.interfacesync.json")));
        Assert.Single(home.Log.Lines, l => l.Message.Contains("looks out of date"));        // said once, not every poll
        Assert.Equal(SyncOutcome.Refused, home.PollAfter(TimeSpan.FromSeconds(15)));
        Assert.Single(home.Log.Lines, l => l.Message.Contains("looks out of date"));
    }

    [Fact]
    public void A_commit_under_the_old_folder_name_also_marks_a_copy_without_one_as_stale()
    {
        WriteCloudFile(Legacy(), @"_retail_\WTF-Account.wowsync.json",
            """{"format":"wowsync-commit/1","state":"Committed","commitId":"x","pushedBy":"TLIT-LT-001","pushedAtUtc":"2026-11-18T19:00:00Z","files":[]}""",
            _world.Clock.UtcNow);
        var home = _world.Pc("MIKE-M3", "_forever_");
        WriteCloudFile(Path.Combine(_world.MyDrive, "InterfaceSync"), @"_forever_\WTF-Account\a.lua", "x", _world.Clock.UtcNow.AddDays(1));
        home.WriteSetting(@"PLAYER#1\macros-cache.txt", "home's own");

        Assert.Equal(SyncOutcome.Refused, home.Sync.Tick());
        Assert.Contains("looks out of date", home.Sync.StatusText);
    }

    [Fact]
    public void First_sync_with_this_pc_newer_asks_and_keeping_this_pc_uploads_it()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "cloud copy");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        _world.Clock.Advance(TimeSpan.FromHours(1));
        var b = _world.Pc("WorkLaptop");
        b.WriteSetting(@"PLAYER#1\macros-cache.txt", "newer on this PC");

        Assert.Equal(SyncOutcome.Conflict, b.Sync.Tick());
        var c = b.Sync.State.Conflict!;
        Assert.Equal(KeepChoice.Local, c.Suggested);
        Assert.Equal("HomePC", c.CloudPushedBy);
        Assert.Contains("from HomePC", c.Reason);

        Assert.Equal(SyncOutcome.Pushed, b.Sync.ResolveConflict(KeepChoice.Local));
        Assert.Equal("newer on this PC", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void A_pc_that_synced_with_a_dead_folder_asks_when_it_finds_the_real_one()
    {
        // The laptop's copy is in the real Drive folder.
        var laptop = _world.Pc("TLIT-LT-001", "_forever_");
        laptop.WriteSetting(@"PLAYER#1\macros-cache.txt", "laptop 14:57");
        Assert.Equal(SyncOutcome.Pushed, laptop.Sync.Tick());

        // The home PC synced into an old "My Drive" folder Drive no longer syncs, with newer settings.
        _world.Clock.Advance(TimeSpan.FromHours(2));
        var dead = Path.Combine(_world.Root, "old", "My Drive");
        Directory.CreateDirectory(dead);
        var fs = new PhysicalFileSystem();
        var home = _world.Pc("MIKE-M3", "_forever_");
        home.WriteSetting(@"PLAYER#1\macros-cache.txt", "home 16:36");
        var deadSync = new VersionSync(new InterfaceSync.Core.Detection.GameVersion("_forever_", home.VersionPath, ["Wow.exe"], true),
            new CloudLayout(dead, "_forever_"), fs, _world.Clock, home.Backups, new StateStore(fs, home.DataFolder), home.Log, "MIKE-M3", () => false);
        Assert.Equal(SyncOutcome.Pushed, deadSync.SyncNow());

        // Next start finds the real folder (the laptop's commit): first sync there, and this PC is newer: ask.
        home.Restart();
        Assert.Equal(SyncOutcome.Conflict, home.Sync.Tick());
        Assert.Equal("home 16:36", home.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("laptop 14:57", File.ReadAllText(Path.Combine(_world.CloudAccount("_forever_"), "PLAYER#1", "macros-cache.txt")));
        Assert.Equal("TLIT-LT-001", home.Sync.State.Conflict!.CloudPushedBy);
    }

    // ---------------------------------------------------------------- 3. write only inside <AppRoot>\<version>\

    [Theory]
    [InlineData(@"C:\Users\P\My Drive", "_retail_", true)]
    [InlineData("/Users/p/Library/CloudStorage/Dropbox", "_retail_", true)]
    [InlineData(@"C:\Users\P\My Drive", "..", false)]
    [InlineData(@"C:\Users\P\My Drive", ".", false)]
    [InlineData(@"C:\Users\P\My Drive", "", false)]
    [InlineData(@"C:\Users\P\My Drive", @"..\..", false)]
    [InlineData(@"C:\Users\P\My Drive", "a/b", false)]
    [InlineData(@"C:\Users\P\My Drive", "InterfaceSync", false)]
    [InlineData("My Drive", "_retail_", false)]
    [InlineData("", "_retail_", false)]
    public void Write_target_guard(string root, string version, bool safe) =>
        Assert.Equal(safe, new CloudLayout(root, version).IsSafeWriteTarget(out _));

    [Fact]
    public void A_push_that_would_land_in_the_cloud_root_is_refused_and_writes_nothing()
    {
        var fs = new PhysicalFileSystem();
        var pc = _world.Pc("HomePC");
        pc.WriteSetting(@"PLAYER#1\macros-cache.txt", "x");
        var before = Snapshot(_world.MyDrive);
        var sync = new VersionSync(
            new WoWSync_GameVersion().Make(pc.VersionPath),
            new CloudLayout(_world.MyDrive, ".."), fs, _world.Clock, pc.Backups, new StateStore(fs, pc.DataFolder), pc.Log, "HomePC", () => false);

        Assert.Equal(SyncOutcome.Refused, sync.SyncNow());
        Assert.Contains("Refused to upload", sync.StatusText);
        Assert.Equal(before, Snapshot(_world.MyDrive));
    }

    private sealed class WoWSync_GameVersion
    {
        public InterfaceSync.Core.Detection.GameVersion Make(string path) => new("_retail_", path, ["Wow.exe"], true);
    }

    // ---------------------------------------------------------------- 4. no push announced while paused

    [Fact]
    public void Closing_the_game_while_paused_announces_no_push()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        a.Sync.Paused = true;

        a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2"));

        Assert.DoesNotContain(a.Log.Lines, l => l.Message.Contains("Pushing in"));
        Assert.Contains(a.Log.Lines, l => l.Message.Contains("Syncing is paused, so nothing is uploaded until you resume"));
        Assert.Equal("M1", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.True(a.Sync.State.PendingPush);
        a.Sync.Paused = false;
        Assert.Equal(SyncOutcome.Pushed, a.PollAfter(TimeSpan.FromSeconds(15)));
    }
}
