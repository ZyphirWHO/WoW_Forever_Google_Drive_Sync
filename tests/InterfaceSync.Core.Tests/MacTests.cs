using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>macOS layouts, checked on every OS through the fakes.</summary>
public class MacWowDetectionTests
{
    private const string Root = "/Applications/World of Warcraft";
    private const string Scratch = "/Users/player/Library/Application Support/InterfaceSync/Temp";

    private static InMemoryFileSystem MacInstall(string root = Root)
    {
        var fs = new InMemoryFileSystem();
        fs.Dir($"{root}/_retail_/World of Warcraft.app/Contents/MacOS").Dir($"{root}/_retail_/WTF/Account/PLAYER#1");
        fs.Dir($"{root}/_classic_era_/World of Warcraft Classic.app").Dir($"{root}/_classic_era_/WTF");
        fs.Dir($"{root}/_retail_/Interface/AddOns");
        return fs;
    }

    [Fact]
    public void Product_db_in_users_shared_finds_the_mac_install_with_forward_slashes_kept()
    {
        var db = ProductDbFixtures.Database(ProductDbFixtures.Product("wow", "/Applications/World of Warcraft", "_retail_", "11.2.7.64000", family: "wow"));
        var fs = MacInstall().Bytes("/Users/Shared/Battle.net/Agent/product.db", db);
        var detector = new WowDetector(fs, new FakeRegistry(), FakeEnvironment.Mac(), Scratch);

        var result = detector.DetectInstalls(null);

        Assert.Equal((DetectionSource.BattleNetProductDb, Root), (result.Source, result.PrimaryRoot));
        var versions = detector.ScanVersions(result);
        var retail = versions.Single(v => v.FolderName == "_retail_");
        Assert.Equal(["World of Warcraft.app"], retail.Executables);
        Assert.True(retail.CanEnable);
        Assert.Equal("/Applications/World of Warcraft/_retail_/WTF/Account", retail.AccountPath);
        Assert.False(versions.Single(v => v.FolderName == "_classic_era_").CanEnable);
    }

    [Fact]
    public void Without_battle_net_data_the_usual_mac_folders_are_checked_and_the_registry_is_never_asked()
    {
        var fs = MacInstall("/Users/player/Applications/World of Warcraft");
        var reg = new FakeRegistry().Set(RegistryHive.LocalMachine, WowDetector.BlizzardKeyWow64, "InstallPath", @"C:\Program Files (x86)\World of Warcraft");
        var result = new WowDetector(fs, reg, FakeEnvironment.Mac(), Scratch).DetectInstalls(null);
        Assert.Equal((DetectionSource.CommonLocation, "/Users/player/Applications/World of Warcraft"), (result.Source, result.PrimaryRoot));
        Assert.DoesNotContain(result.Attempts, a => a.Contains("registry"));
    }

    [Fact]
    public void The_game_is_seen_running_by_its_app_bundle_path_and_versions_are_told_apart()
    {
        var retail = new GameVersion("_retail_", $"{Root}/_retail_", ["World of Warcraft.app"], true);
        var classic = new GameVersion("_classic_era_", $"{Root}/_classic_era_", ["World of Warcraft Classic.app"], true);
        // macOS cuts process names to 16 characters.
        var snapshot = new List<ProcessEntry> { new(501, "World of Warcraf", $"{Root}/_retail_/World of Warcraft.app/Contents/MacOS/World of Warcraft") };

        Assert.True(GameWatcher.IsRunning(retail, snapshot));
        Assert.False(GameWatcher.IsRunning(classic, snapshot));

        var processes = new FakeProcessList();
        processes.Processes.AddRange(snapshot);
        Assert.Single(new GameWatcher(processes).Snapshot());                     // "World of" prefix is listed
        Assert.True(GameWatcher.IsRunning(classic, [new(502, "World of Warcraf", null)])); // path unreadable: assume running
    }
}

public class MacCloudDetectionTests
{
    private static FakeEnvironment Env => FakeEnvironment.Mac();
    private const string CloudStorage = "/Users/player/Library/CloudStorage";

    [Fact]
    public void Google_drive_is_found_from_its_database_in_application_support()
    {
        var db = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "drivefs-roots-mac.db"));
        var myDrive = $"{CloudStorage}/GoogleDrive-someone@example.com/My Drive";
        var fs = new InMemoryFileSystem()
            .Bytes("/Users/player/Library/Application Support/Google/DriveFS/root_preference_sqlite.db", db)
            .Dir(myDrive);

        var r = new DriveDetector(fs, new FakeRegistry(), Env).Detect(null);

        Assert.Equal((myDrive, DetectionSource.DriveFsSettings), (r.MyDrivePath, r.Source));
        Assert.Contains(r.Attempts, a => a.Contains("ignored paths without a drive letter") && a.Contains("Library/CloudStorage"));
        Assert.DoesNotContain(r.Attempts, a => a.Contains("registry"));
    }

    [Fact]
    public void Google_drive_file_provider_folder_is_probed_when_the_database_is_missing()
    {
        var myDrive = $"{CloudStorage}/GoogleDrive-someone@example.com/My Drive";
        var r = new DriveDetector(new InMemoryFileSystem().Dir(myDrive), new FakeRegistry(), Env).Detect(null);
        Assert.Equal((myDrive, DetectionSource.Probe), (r.MyDrivePath, r.Source));
    }

    [Fact]
    public void OneDrive_personal_comes_before_work_and_school()
    {
        var fs = new InMemoryFileSystem().Dir($"{CloudStorage}/OneDrive-Contoso").Dir($"{CloudStorage}/OneDrive-Personal");
        var r = new OneDriveDetector(fs, new FakeRegistry(), Env).Detect(null);
        Assert.Equal($"{CloudStorage}/OneDrive-Personal", r.MyDrivePath);
        Assert.DoesNotContain(r.Attempts, a => a.Contains("registry") || a.Contains("%OneDrive"));
    }

    [Fact]
    public void Dropbox_info_json_lives_in_the_home_folder_on_mac()
    {
        var fs = new InMemoryFileSystem()
            .File("/Users/player/.dropbox/info.json", """{"personal": {"path": "/Users/player/Library/CloudStorage/Dropbox"}}""")
            .Dir($"{CloudStorage}/Dropbox");
        var r = new DropboxDetector(fs, Env).Detect(null);
        Assert.Equal(($"{CloudStorage}/Dropbox", DetectionSource.DriveFsSettings), (r.MyDrivePath, r.Source));

        var probed = new DropboxDetector(new InMemoryFileSystem().Dir($"{CloudStorage}/Dropbox"), Env).Detect(null);
        Assert.Equal(($"{CloudStorage}/Dropbox", DetectionSource.Probe), (probed.MyDrivePath, probed.Source));
    }

    [Theory]
    [InlineData(CloudProvider.GoogleDrive, "Available offline")]
    [InlineData(CloudProvider.OneDrive, "Always Keep on This Device")]
    [InlineData(CloudProvider.Dropbox, "Make available offline")]
    public void Mac_hints_use_finder_wording_and_no_provider_pins_folders_itself(CloudProvider kind, string action)
    {
        var p = CloudProviderCatalog.Create(kind, new InMemoryFileSystem(), new FakeRegistry(), Env, "/tmp");
        Assert.StartsWith("In Finder, Control-click", p.KeepOnDeviceHelp);
        Assert.Contains(action, p.KeepOnDeviceHelp);
        Assert.False(p.CanPinFolder);
    }

    [Fact]
    public void Cloud_layout_uses_forward_slashes_under_a_mac_folder()
    {
        var layout = new CloudLayout($"{CloudStorage}/Dropbox", "_retail_", CloudProvider.Dropbox);
        Assert.Equal($"{CloudStorage}/Dropbox/InterfaceSync/_retail_/WTF-Account", layout.Account);
        Assert.Equal("/Applications/World of Warcraft/_retail_/WTF/Account/PLAYER#1/macros-cache.txt",
            PathUtil.FromRelative("/Applications/World of Warcraft/_retail_/WTF/Account", @"PLAYER#1\macros-cache.txt"));
    }
}

/// <summary>Checks the native struct layout. Runs only on macOS (the macOS CI job); elsewhere it returns at once.</summary>
public class MacNativeTests
{
    [Fact]
    public void Stat_layout_matches_this_mac()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var file = Path.GetTempFileName();
        File.WriteAllBytes(file, new byte[1234]);
        try
        {
            var st = InterfaceSync.Core.Platform.MacNative.Stat(file);
            Assert.NotNull(st);
            Assert.Equal(1234, InterfaceSync.Core.Platform.MacNative.Size(st!));               // st_size offset right
            Assert.Equal(0x8000, InterfaceSync.Core.Platform.MacNative.Mode(st!) & 0xF000);    // S_IFREG
            Assert.False(InterfaceSync.Core.Platform.MacNative.IsDataless(file));
            Assert.False(new InterfaceSync.Core.Platform.PhysicalFileSystem().GetFileInfo(file).IsOnlineOnly);
            Assert.NotNull(InterfaceSync.Core.Platform.MacNative.ExecutablePath(Environment.ProcessId));
            Assert.False(new InterfaceSync.Core.Platform.MacUserEnvironment().IsElevated);
        }
        finally { File.Delete(file); }
    }
}
