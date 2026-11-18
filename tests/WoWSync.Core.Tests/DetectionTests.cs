using WoWSync.Core.Abstractions;
using WoWSync.Core.Detection;
using WoWSync.Core.Tests.Fakes;
using Xunit;

namespace WoWSync.Core.Tests;

public class WowDetectionTests
{
    private const string Root = @"C:\Program Files (x86)\World of Warcraft";

    private static InMemoryFileSystem StandardInstall(string root = Root)
    {
        var fs = new InMemoryFileSystem();
        fs.File($@"{root}\_retail_\Wow.exe").Dir($@"{root}\_retail_\WTF\Account\PLAYER#1").File($@"{root}\_retail_\WTF\Config.wtf");
        fs.File($@"{root}\_forever_\Wow.exe").Dir($@"{root}\_forever_\WTF\Account");
        fs.File($@"{root}\_classic_era_\WowClassic.exe").Dir($@"{root}\_classic_era_\WTF");          // never launched
        fs.File($@"{root}\_ptr_\WowT.exe").File($@"{root}\_ptr_\.flavor.info");                        // no WTF yet
        fs.Dir($@"{root}\_junk_");                                                                       // no exe
        fs.File($@"{root}\Data\data.000");
        fs.File($@"{root}\_backup_\notes.txt").Dir($@"{root}\_backup_\WTF");                            // WTF but no exe
        return fs;
    }

    [Fact]
    public void Registry_install_path_pointing_at_retail_resolves_to_install_root()
    {
        var fs = StandardInstall();
        var reg = new FakeRegistry().Set(RegistryHive.LocalMachine, WowDetector.BlizzardKeyWow64, "InstallPath", Root + @"\_retail_\");

        var result = new WowDetector(fs, reg).DetectInstall(null);

        Assert.Equal(DetectionSource.Registry, result.Source);
        Assert.Equal(Root, result.Path);
    }

    [Fact]
    public void Uninstall_entry_is_used_when_blizzard_key_is_missing()
    {
        var fs = StandardInstall(@"E:\Blizzard\World of Warcraft");
        var reg = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, WowDetector.UninstallKeyWow64 + @"\Some Other App", "DisplayName", "Other")
            .Set(RegistryHive.LocalMachine, WowDetector.UninstallKeyWow64 + @"\World of Warcraft", "DisplayName", "World of Warcraft")
            .Set(RegistryHive.LocalMachine, WowDetector.UninstallKeyWow64 + @"\World of Warcraft", "InstallLocation", @"""E:\Blizzard\World of Warcraft""");

        var result = new WowDetector(fs, reg).DetectInstall(null);

        Assert.Equal(DetectionSource.UninstallEntry, result.Source);
        Assert.Equal(@"E:\Blizzard\World of Warcraft", result.Path);
    }

    [Fact]
    public void Stale_registry_value_falls_through_to_common_locations_on_other_drives()
    {
        var fs = StandardInstall(@"D:\World of Warcraft");
        fs.Drives.Add(new DriveEntry(@"C:\", DriveKind.Fixed, "OS", true));
        fs.Drives.Add(new DriveEntry(@"D:\", DriveKind.Fixed, "Games", true));
        var reg = new FakeRegistry().Set(RegistryHive.LocalMachine, WowDetector.BlizzardKeyWow64, "InstallPath", @"C:\Old\World of Warcraft");

        var result = new WowDetector(fs, reg).DetectInstall(null);

        Assert.Equal(DetectionSource.CommonLocation, result.Source);
        Assert.Equal(@"D:\World of Warcraft", result.Path);
        Assert.Contains(result.Attempts, a => a.Contains(@"C:\Old\World of Warcraft"));
    }

    [Fact]
    public void User_override_wins_and_a_vanished_override_falls_back()
    {
        var fs = StandardInstall();
        fs.File(@"F:\WoW\_retail_\Wow.exe").Dir(@"F:\WoW\_retail_\WTF\Account");
        var reg = new FakeRegistry().Set(RegistryHive.LocalMachine, WowDetector.BlizzardKeyWow64, "InstallPath", Root);
        var detector = new WowDetector(fs, reg);

        Assert.Equal((@"F:\WoW", DetectionSource.UserOverride), (detector.DetectInstall(@"F:\WoW").Path, detector.DetectInstall(@"F:\WoW").Source));

        fs.RemoveTree(@"F:\WoW");
        var fallback = detector.DetectInstall(@"F:\WoW");
        Assert.Equal(DetectionSource.Registry, fallback.Source);
        Assert.Contains(fallback.Attempts, a => a.Contains("no longer contains"));
    }

    [Fact]
    public void Not_found_asks_the_user()
    {
        var result = new WowDetector(new InMemoryFileSystem(), new FakeRegistry()).DetectInstall(null);
        Assert.False(result.Found);
        Assert.Equal(DetectionSource.NotFound, result.Source);
    }

    [Fact]
    public void Scans_every_game_version_including_new_ones_without_code_changes()
    {
        var fs = StandardInstall();
        fs.File($@"{Root}\_anniversary_\WowClassic.exe").Dir($@"{Root}\_anniversary_\WTF\Account");

        var versions = new WowDetector(fs, new FakeRegistry()).ScanVersions(Root);

        Assert.Equal(["_anniversary_", "_classic_era_", "_forever_", "_ptr_", "_retail_"], versions.Select(v => v.FolderName));
        Assert.True(versions.Single(v => v.FolderName == "_retail_").CanEnable);
        Assert.True(versions.Single(v => v.FolderName == "_forever_").CanEnable);
        Assert.False(versions.Single(v => v.FolderName == "_classic_era_").CanEnable); // never launched
        Assert.False(versions.Single(v => v.FolderName == "_ptr_").CanEnable);
        Assert.Equal("classic_era", versions.Single(v => v.FolderName == "_classic_era_").Key);
    }

    [Fact]
    public void Linked_version_folders_and_linked_account_folders_are_not_trusted()
    {
        var fs = StandardInstall();
        fs.Link($@"{Root}\_retail_\WTF\Account");
        fs.File($@"{Root}\_beta_\Wow.exe").Dir($@"{Root}\_beta_\WTF");
        fs.Link($@"{Root}\_beta_");

        var versions = new WowDetector(fs, new FakeRegistry()).ScanVersions(Root);

        Assert.DoesNotContain(versions, v => v.FolderName == "_beta_");
        Assert.False(versions.Single(v => v.FolderName == "_retail_").CanEnable);
    }
}

public class DriveDetectionTests
{
    private const string PrefsJson = """
        {"per_account_preferences":[{"key":"1234567890","value":{"mount_point_path":"G","other":1}}]}
        """;

    [Fact]
    public void Drive_settings_mount_letter_gives_streaming_my_drive()
    {
        var fs = new InMemoryFileSystem().Dir(@"G:\My Drive\WoWSync");
        fs.Drives.Add(new DriveEntry(@"G:\", DriveKind.Fixed, "Google Drive", true));
        var reg = new FakeRegistry().Set(RegistryHive.CurrentUser, DriveDetector.DriveFsUserKey, "PerAccountPreferences", PrefsJson);

        var result = new DriveDetector(fs, reg, new FakeEnvironment()).Detect(null);

        Assert.Equal(@"G:\My Drive", result.MyDrivePath);
        Assert.Equal(DriveMode.Streaming, result.Mode);
        Assert.Equal(DetectionSource.DriveFsSettings, result.Source);
    }

    [Fact]
    public void Probe_finds_any_letter_labelled_google_drive()
    {
        var fs = new InMemoryFileSystem().Dir(@"H:\My Drive");
        fs.Drives.Add(new DriveEntry(@"C:\", DriveKind.Fixed, "OS", true));
        fs.Drives.Add(new DriveEntry(@"H:\", DriveKind.Fixed, "Google Drive", true));

        var result = new DriveDetector(fs, new FakeRegistry(), new FakeEnvironment()).Detect(null);

        Assert.Equal(@"H:\My Drive", result.MyDrivePath);
        Assert.Equal(DetectionSource.Probe, result.Source);
    }

    [Fact]
    public void Mirrored_my_drive_in_the_user_profile()
    {
        var fs = new InMemoryFileSystem().Dir(@"C:\Users\Player\My Drive");

        var result = new DriveDetector(fs, new FakeRegistry(), new FakeEnvironment()).Detect(null);

        Assert.Equal(@"C:\Users\Player\My Drive", result.MyDrivePath);
        Assert.Equal(DriveMode.Mirror, result.Mode);
    }

    [Fact]
    public void Drive_moved_from_G_to_mirror_between_runs_is_followed()
    {
        var fs = new InMemoryFileSystem().Dir(@"G:\My Drive");
        fs.Drives.Add(new DriveEntry(@"G:\", DriveKind.Fixed, "Google Drive", true));
        var detector = new DriveDetector(fs, new FakeRegistry(), new FakeEnvironment());
        Assert.Equal(@"G:\My Drive", detector.Detect(null).MyDrivePath);

        // Player switches Drive to mirror mode: G: disappears, %USERPROFILE%\My Drive appears.
        fs.RemoveTree(@"G:\");
        fs.Drives.Clear();
        fs.Dir(@"C:\Users\Player\My Drive");

        var after = detector.Detect(@"G:\My Drive"); // stale stored path is ignored
        Assert.Equal(@"C:\Users\Player\My Drive", after.MyDrivePath);
        Assert.Equal(DriveMode.Mirror, after.Mode);
    }

    [Fact]
    public void Custom_mirror_folder_from_settings_and_unreadable_json_are_handled()
    {
        var fs = new InMemoryFileSystem().Dir(@"D:\Sync\Google\My Drive");
        var reg = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, DriveDetector.DriveFsUserKey, "PerAccountPreferences", "{not json")
            .Set(RegistryHive.CurrentUser, DriveDetector.DriveFsUserKey, "DefaultMountPoint", @"D:\Sync\Google");

        var result = new DriveDetector(fs, reg, new FakeEnvironment()).Detect(null);

        Assert.Equal(@"D:\Sync\Google\My Drive", result.MyDrivePath);
        Assert.Contains(result.Attempts, a => a.Contains("could not be read"));
    }

    [Fact]
    public void Not_found_explains_what_to_check()
    {
        var result = new DriveDetector(new InMemoryFileSystem(), new FakeRegistry(), new FakeEnvironment()).Detect(null);
        Assert.False(result.Found);
        Assert.Contains(result.Attempts, a => a.Contains("Google Drive for desktop"));
    }

    [Fact]
    public void Extracts_mount_points_from_nested_json()
    {
        var json = """{"a":[{"value":{"mount_point_path":"G"}},{"value":{"mount_point_path":"C:\\Users\\X\\My Drive"}}]}""";
        Assert.Equal(["G", @"C:\Users\X\My Drive"], DriveDetector.ExtractMountPoints(json));
    }
}
