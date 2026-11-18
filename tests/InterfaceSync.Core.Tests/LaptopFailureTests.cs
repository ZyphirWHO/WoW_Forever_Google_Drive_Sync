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
        fs.FailDeleteDirectory = p => p.Contains(".interfacesync-old");

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
