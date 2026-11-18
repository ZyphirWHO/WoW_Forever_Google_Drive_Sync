using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

public class ProductNameTests
{
    [Fact]
    public void Name_and_id_come_from_the_build_and_drive_every_derived_name()
    {
        Assert.Equal("Interface Sync", ProductInfo.Name);
        Assert.Equal("InterfaceSync", ProductInfo.Id);
        Assert.Equal("InterfaceSync", CloudLayout.AppFolderName);
        Assert.Equal("WTF-Account.interfacesync.json", CloudLayout.CommitFileName);
        Assert.Equal(".interfacesync-tmp", FolderScanner.TempSuffix);
        Assert.EndsWith(Path.DirectorySeparatorChar + "InterfaceSync", new AppPaths(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)).Data);
        Assert.DoesNotContain("WoW", ProductInfo.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Warcraft", ProductInfo.Name, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LocalDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "is-localmig", Guid.NewGuid().ToString("N"));
    public LocalDataMigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Earlier_builds_data_folder_is_moved_once()
    {
        var old = Path.Combine(_root, "WoWSync");
        Directory.CreateDirectory(Path.Combine(old, "State"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(old, "State", "forever.json"), "{}");

        var message = LocalDataMigration.Run(new PhysicalFileSystem(), _root);

        Assert.Contains("Moved", message);
        Assert.True(File.Exists(Path.Combine(_root, "InterfaceSync", "State", "forever.json")));
        Assert.False(Directory.Exists(old));
        Assert.Null(LocalDataMigration.Run(new PhysicalFileSystem(), _root));       // once only
    }

    [Fact]
    public void A_folder_without_2_0_settings_is_left_alone()
    {
        var old = Path.Combine(_root, "WoWSync");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "agent-state.txt"), "v1.1");
        Assert.Null(LocalDataMigration.Run(new PhysicalFileSystem(), _root));
        Assert.True(File.Exists(Path.Combine(old, "agent-state.txt")));
    }
}

public sealed class LegacyCommitFileTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    [Fact]
    public void Commit_file_written_by_earlier_builds_is_read_and_replaced_on_the_next_push()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        var root = Path.GetDirectoryName(_world.CommitFilePath())!;
        File.Move(_world.CommitFilePath(), Path.Combine(root, "WTF-Account.wowsync.json"));   // as an earlier build left it

        var b = _world.Pc("WorkLaptop");
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        Assert.Equal(a.Sync.State.LastCommitId, b.Sync.State.LastCommitId);              // same commit, not "legacy" data

        Assert.Equal(SyncOutcome.Pushed, b.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));
        Assert.True(File.Exists(_world.CommitFilePath()));
        Assert.False(File.Exists(Path.Combine(root, "WTF-Account.wowsync.json")));
    }
}

public sealed class CloudFolderMigrationTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("WoWSync")]
    [InlineData("WoW Sync")]
    public void Old_cloud_folder_is_renamed_once_with_commit_files_backed_up_and_syncing_carries_on(string oldName)
    {
        // A PC that synced with an earlier build into <cloud>\WoWSync.
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        var newRoot = Path.Combine(_world.MyDrive, "InterfaceSync");
        var oldRoot = Path.Combine(_world.MyDrive, oldName);
        Directory.Move(newRoot, oldRoot);
        File.Move(Path.Combine(oldRoot, "_retail_", "WTF-Account.interfacesync.json"), Path.Combine(oldRoot, "_retail_", "WTF-Account.wowsync.json"));

        var log = new InterfaceSync.Core.Logging.MemoryLog();
        CloudFolderMigration.Run(new PhysicalFileSystem(), _world.MyDrive, a.Backups, log);

        Assert.True(Directory.Exists(newRoot));
        Assert.False(Directory.Exists(oldRoot));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Renamed the cloud folder"));
        var backup = Assert.Single(a.Backups.List("_retail_"), b => b.Reason == "cloud-folder-rename");
        Assert.True(File.Exists(Path.Combine(backup.Path, "WTF-Account.wowsync.json")));

        // Same commit, so the PC carries on without a re-download.
        a.Restart();
        Assert.Equal(SyncOutcome.NothingToDo, a.Sync.SyncNow());
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));
        Assert.Equal("M2", File.ReadAllText(Path.Combine(newRoot, "_retail_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void When_both_old_and_new_folders_exist_nothing_is_moved()
    {
        Directory.CreateDirectory(Path.Combine(_world.MyDrive, "InterfaceSync"));
        Directory.CreateDirectory(Path.Combine(_world.MyDrive, "WoWSync", "_retail_"));
        var log = new InterfaceSync.Core.Logging.MemoryLog();
        var a = _world.Pc("HomePC");
        CloudFolderMigration.Run(new PhysicalFileSystem(), _world.MyDrive, a.Backups, log);
        Assert.True(Directory.Exists(Path.Combine(_world.MyDrive, "WoWSync", "_retail_")));
        Assert.Contains(log.Lines, l => l.Level == InterfaceSync.Core.Logging.LogLevel.Warning && l.Message.Contains("left untouched"));
    }
}
