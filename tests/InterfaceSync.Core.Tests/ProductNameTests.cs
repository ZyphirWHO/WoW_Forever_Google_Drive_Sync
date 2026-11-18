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

public class TrademarkHygieneTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Product_names_carry_no_game_trademarks()
    {
        foreach (var name in new[] { ProductInfo.Name, ProductInfo.Id, CloudLayout.AppFolderName })
            foreach (var mark in new[] { "WoW", "Warcraft", "Blizzard" })
                Assert.DoesNotContain(mark, name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readme_has_the_credit_line_and_the_not_affiliated_statement()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc.,\nin the U.S. and/or other countries.", readme.Replace("\r\n", "\n"));
        Assert.Contains("not affiliated with", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("World of Warcraft®", readme);
    }
}
