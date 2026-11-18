using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

public class OneDriveDetectionTests
{
    [Fact]
    public void Environment_variable_set_by_onedrive_wins()
    {
        var env = new FakeEnvironment();
        env.Variables["OneDriveConsumer"] = @"D:\Sync\OneDrive";
        var fs = new InMemoryFileSystem().Dir(@"D:\Sync\OneDrive").Dir(@"C:\Users\Player\OneDrive");

        var r = new OneDriveDetector(fs, new FakeRegistry(), env).Detect(null);

        Assert.Equal((@"D:\Sync\OneDrive", DetectionSource.DriveFsSettings, CloudProvider.OneDrive), (r.MyDrivePath, r.Source, r.Provider));
    }

    [Fact]
    public void Registry_accounts_personal_before_business()
    {
        var reg = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, OneDriveDetector.AccountsKey + @"\Business1", "UserFolder", @"C:\Users\Player\OneDrive - Contoso")
            .Set(RegistryHive.CurrentUser, OneDriveDetector.AccountsKey + @"\Personal", "UserFolder", @"C:\Users\Player\OneDrive");
        var fs = new InMemoryFileSystem().Dir(@"C:\Users\Player\OneDrive - Contoso").Dir(@"C:\Users\Player\OneDrive");

        var r = new OneDriveDetector(fs, reg, new FakeEnvironment()).Detect(null);

        Assert.Equal(@"C:\Users\Player\OneDrive", r.MyDrivePath);
        Assert.Contains(r.Attempts, a => a.Contains("environment variable %OneDriveConsumer% not set"));
    }

    [Fact]
    public void Work_folder_is_found_by_probing_and_not_found_is_explained()
    {
        var fs = new InMemoryFileSystem().Dir(@"C:\Users\Player\OneDrive - Contoso Ltd");
        var r = new OneDriveDetector(fs, new FakeRegistry(), new FakeEnvironment()).Detect(null);
        Assert.Equal((@"C:\Users\Player\OneDrive - Contoso Ltd", DetectionSource.Probe), (r.MyDrivePath, r.Source));

        var none = new OneDriveDetector(new InMemoryFileSystem(), new FakeRegistry(), new FakeEnvironment()).Detect(null);
        Assert.False(none.Found);
        Assert.Equal(CloudProvider.OneDrive, none.Provider);
        Assert.Contains(none.Attempts, a => a.Contains("Is OneDrive installed and signed in"));
    }
}

public class DropboxDetectionTests
{
    private const string Info = @"C:\Users\Player\AppData\Roaming\Dropbox\info.json";

    [Fact]
    public void Info_json_personal_path_is_used()
    {
        var fs = new InMemoryFileSystem()
            .File(Info, """{"business": {"path": "C:\\Users\\Player\\Dropbox (Contoso)", "host": 1}, "personal": {"path": "E:\\Dropbox", "host": 2}}""")
            .Dir(@"E:\Dropbox").Dir(@"C:\Users\Player\Dropbox (Contoso)");

        var r = new DropboxDetector(fs, new FakeEnvironment()).Detect(null);

        Assert.Equal((@"E:\Dropbox", DetectionSource.DriveFsSettings, CloudProvider.Dropbox), (r.MyDrivePath, r.Source, r.Provider));
    }

    [Fact]
    public void Unreadable_info_json_falls_back_to_the_usual_folder()
    {
        var fs = new InMemoryFileSystem().File(Info, "{oops").Dir(@"C:\Users\Player\Dropbox");
        var r = new DropboxDetector(fs, new FakeEnvironment()).Detect(null);
        Assert.Equal((@"C:\Users\Player\Dropbox", DetectionSource.Probe), (r.MyDrivePath, r.Source));
        Assert.Contains(r.Attempts, a => a.Contains("could not be read"));
        Assert.Contains(r.Attempts, a => a.Contains(@"Local\Dropbox\info.json") && a.Contains("not found"));
    }

    [Fact]
    public void Override_wins_and_a_vanished_override_is_reported()
    {
        var fs = new InMemoryFileSystem().Dir(@"F:\Box").Dir(@"C:\Users\Player\Dropbox");
        var d = new DropboxDetector(fs, new FakeEnvironment());
        Assert.Equal(DetectionSource.UserOverride, d.Detect(@"F:\Box").Source);
        var r = d.Detect(@"F:\Gone");
        Assert.Equal(DetectionSource.Probe, r.Source);
        Assert.Contains(r.Attempts, a => a.Contains("no longer exists"));
    }
}

public class ConflictCopyNameTests
{
    private static readonly HashSet<string> Siblings = new(StringComparer.OrdinalIgnoreCase)
    {
        "bindings-cache.wtf", "Details.lua", "WeakAuras.lua", "macros-cache.txt",
    };

    [Theory]
    [InlineData("macros-cache (1).txt", true)]                                              // Google Drive
    [InlineData("macros-cache (WorkLaptop's conflicted copy 2026-11-18).txt", true)]        // Dropbox
    [InlineData("macros-cache (Case Conflict).txt", true)]                                  // Dropbox
    [InlineData("bindings-cache-TLIT-LT-001.wtf", true)]                                    // OneDrive, dashed PC name
    [InlineData("Details-HOMEPC.lua", true)]                                                // OneDrive
    [InlineData("Details-HOMEPC-2.lua", true)]                                              // OneDrive, numbered
    [InlineData("Details-Forever.lua", false)]                                              // ordinary addon file
    [InlineData("Details_Streamer.lua", false)]
    [InlineData("Bagnon-UNKNOWN.lua", false)]                                               // no original beside it
    [InlineData("WeakAuras.lua", false)]
    public void Recognises_conflict_copies_from_every_cloud_app(string name, bool expected) =>
        Assert.Equal(expected, FolderScanner.IsConflictCopyName(name, Siblings));
}

public sealed class CloudProviderSwitchTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    [Fact]
    public void Switching_to_an_empty_folder_of_another_app_is_a_first_sync_there_not_in_sync()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());

        // Same PC, now pointed at a OneDrive folder that has no InterfaceSync data yet.
        var oneDrive = Path.Combine(_world.Root, "OneDrive");
        Directory.CreateDirectory(oneDrive);
        var fs = new PhysicalFileSystem();
        var switched = new VersionSync(
            new InterfaceSync.Core.Detection.GameVersion("_retail_", a.VersionPath, ["Wow.exe"], true),
            new CloudLayout(oneDrive, "_retail_", CloudProvider.OneDrive), fs, _world.Clock,
            a.Backups, new StateStore(fs, a.DataFolder), new MemoryLog(), "HomePC", () => false);

        Assert.Equal(SyncOutcome.Pushed, switched.SyncNow());
        Assert.Equal("M1", File.ReadAllText(Path.Combine(oneDrive, "InterfaceSync", "_retail_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void Cloud_folder_that_moved_with_the_same_data_carries_on_without_a_pull()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());

        var moved = Path.Combine(_world.Root, "Moved", "My Drive");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(_world.MyDrive, moved);
        var fs = new PhysicalFileSystem();
        var log = new MemoryLog();
        var after = new VersionSync(
            new InterfaceSync.Core.Detection.GameVersion("_retail_", a.VersionPath, ["Wow.exe"], true),
            new CloudLayout(moved, "_retail_"), fs, _world.Clock, a.Backups, new StateStore(fs, a.DataFolder), log, "HomePC", () => false);

        Assert.Equal(SyncOutcome.NothingToDo, after.SyncNow());
        Assert.Contains(log.Lines, l => l.Message.Contains("holds the same settings"));
        Assert.Empty(a.Backups.List("_retail_"));
    }
}
