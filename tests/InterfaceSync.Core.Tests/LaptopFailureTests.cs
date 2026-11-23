using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>Reproduces the laptop's first run of the renamed build (2026-11-18 14:25).</summary>
public sealed class LaptopFailureTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    [Fact]
    public void Read_only_folders_are_deleted()
    {
        var dir = Path.Combine(_world.Root, "ro", "SavedVariables");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Addon.lua"), "x");
        File.SetAttributes(Path.Combine(dir, "Addon.lua"), FileAttributes.ReadOnly);
        File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.ReadOnly);

        new PhysicalFileSystem().DeleteDirectory(Path.Combine(_world.Root, "ro"));

        Assert.False(Directory.Exists(Path.Combine(_world.Root, "ro")));
    }

    [Fact]
    public void Pull_completes_even_when_the_old_folder_cannot_be_deleted_and_the_leftover_never_blocks_later_pulls()
    {
        var a = _world.Pc("HomePC", "_forever_");
        a.WriteSetting(@"PLAYER#1\SavedVariables\Addon.lua", "cloud copy");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());

        var fs = new ScriptedFileSystem();
        var laptop = _world.Pc("TLIT-LT-001", "_forever_", fs);
        laptop.WriteSetting(@"PLAYER#1\SavedVariables\Addon.lua", "laptop copy");
        fs.FailDeleteDirectory = p => p.Contains(".interfacesync-staging");   // the download's side folder can't be removed

        Assert.Equal(SyncOutcome.Pulled, laptop.Sync.Tick());                       // was: "sync failed", retried forever
        Assert.Equal("cloud copy", laptop.ReadSetting(@"PLAYER#1\SavedVariables\Addon.lua"));
        Assert.True(laptop.Sync.State.Joined);
        Assert.Contains(laptop.Log.Lines, l => l.Message.Contains("couldn't remove the leftover folder"));
        Assert.Equal(SyncOutcome.NothingToDo, laptop.PollAfter(TimeSpan.FromMinutes(5)));

        // Another pull while the stuck leftover is still there uses a numbered name instead.
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\SavedVariables\Addon.lua", "cloud v2")));
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromMinutes(5)));
        Assert.Equal("cloud v2", laptop.ReadSetting(@"PLAYER#1\SavedVariables\Addon.lua"));

        // Once Windows lets go, the leftovers are cleaned up on the next pull.
        fs.FailDeleteDirectory = null;
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\SavedVariables\Addon.lua", "cloud v3")));
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromMinutes(5)));
        var wtf = Path.Combine(laptop.VersionPath, "WTF");
        Assert.Equal(["Account"], Directory.GetDirectories(wtf).Select(Path.GetFileName));
    }

    [Fact]
    public void Local_data_that_cannot_be_moved_is_copied_instead_of_starting_fresh()
    {
        var fs = new InMemoryFileSystem()
            .File(@"C:\Users\P\AppData\Local\WoWSync\settings.json", "{\"setupComplete\":true}")
            .File(@"C:\Users\P\AppData\Local\WoWSync\State\forever.json", "{\"joined\":true}")
            .File(@"C:\Users\P\AppData\Local\WoWSync\Backups\_forever_\20261118-1-first-join\a.lua", "backup");

        var message = LocalDataMigration.Run(fs, @"C:\Users\P\AppData\Local");

        Assert.StartsWith("Copied", message);
        Assert.Equal("{\"joined\":true}", fs.ReadAllText(@"C:\Users\P\AppData\Local\InterfaceSync\State\forever.json"));
        Assert.True(fs.FileExists(@"C:\Users\P\AppData\Local\InterfaceSync\Backups\_forever_\20261118-1-first-join\a.lua"));
        Assert.True(fs.FileExists(@"C:\Users\P\AppData\Local\WoWSync\settings.json"));        // old folder untouched
    }
}

/// <summary>The laptop log of 2026-11-18 14:25-14:34: a failing first sync retried every 15 s.</summary>
public sealed class RetryNoiseTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    [Fact]
    public void A_retried_first_sync_keeps_one_backup_of_the_original_settings_and_logs_each_message_once()
    {
        var a = _world.Pc("HomePC", "_forever_");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "cloud copy");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());

        var fs = new ScriptedFileSystem();
        var laptop = _world.Pc("TLIT-LT-001", "_forever_", fs);
        laptop.WriteSetting(@"PLAYER#1\macros-cache.txt", "the laptop's own settings");
        fs.FailCopy = (_, dest) => dest.Contains(".interfacesync-staging");      // keeps failing, like the laptop did

        for (int i = 0; i < 40; i++) Assert.Equal(SyncOutcome.Failed, laptop.PollAfter(TimeSpan.FromSeconds(15)));

        var backup = Assert.Single(laptop.Backups.List("_forever_"));               // was: 40, pruned to the newest 5
        Assert.Equal("the laptop's own settings", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "macros-cache.txt")));
        Assert.Single(laptop.Log.Lines, l => l.Message.Contains("first sync on this PC"));
        Assert.Single(laptop.Log.Lines, l => l.Message.Contains("backed up this PC's settings"));
        Assert.Single(laptop.Log.Lines, l => l.Message.Contains("sync failed"));

        fs.FailCopy = null;
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromSeconds(15)));
        Assert.Equal("cloud copy", laptop.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Single(laptop.Backups.List("_forever_"));
    }

    [Fact]
    public void An_unchanged_folder_reuses_the_newest_backup_and_a_changed_one_gets_a_new_backup()
    {
        var pc = _world.Pc("HomePC", "_forever_");
        pc.WriteSetting(@"PLAYER#1\macros-cache.txt", "v1");
        var first = pc.Backups.Create("_forever_", pc.AccountPath, "before-pull")!;
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = pc.Backups.Create("_forever_", pc.AccountPath, "before-pull")!;
        Assert.True(again.Reused);
        Assert.Equal(first.Path, again.Path);

        pc.WriteSetting(@"PLAYER#1\macros-cache.txt", "v2");
        var changed = pc.Backups.Create("_forever_", pc.AccountPath, "before-pull")!;
        Assert.False(changed.Reused);
        Assert.Equal(2, pc.Backups.List("_forever_").Count);
    }
}
