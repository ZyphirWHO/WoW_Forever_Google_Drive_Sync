using WoWSync.Core.Abstractions;
using WoWSync.Core.Detection;
using WoWSync.Core.Tests.Fakes;
using Xunit;

namespace WoWSync.Core.Tests;

public class WowDetectionTests
{
    private const string Root = @"C:\Program Files (x86)\World of Warcraft";
    private const string ProductDbPath = @"C:\ProgramData\Battle.net\Agent\product.db";
    private const string Scratch = @"C:\Users\Player\AppData\Local\WoWSync\Temp";

    private static WowDetector Detector(InMemoryFileSystem fs, FakeRegistry? reg = null) =>
        new(fs, reg ?? new FakeRegistry(), new FakeEnvironment(), Scratch);

    /// <summary>The laptop's layout: _forever_ and _classic_beta_ in the default folder.</summary>
    private static InMemoryFileSystem LaptopInstall(string root = Root)
    {
        var fs = new InMemoryFileSystem();
        fs.File($@"{root}\_forever_\Wow.exe").Dir($@"{root}\_forever_\WTF\Account\PLAYER#1").File($@"{root}\_forever_\WTF\Config.wtf");
        fs.File($@"{root}\_classic_beta_\WowB.exe").Dir($@"{root}\_classic_beta_\WTF");
        fs.Dir($@"C:\Program Files (x86)\Warcraft III\Warcraft III.exe");
        return fs;
    }

    [Fact]
    public void Laptop_product_db_finds_the_install_and_tags_each_version()
    {
        var fs = LaptopInstall().Bytes(ProductDbPath, ProductDbFixtures.Laptop());
        fs.File(@"C:\ProgramData\Battle.net\Agent\.product.db", "ignored");
        var detector = Detector(fs);

        var result = detector.DetectInstalls(null);

        Assert.Equal(DetectionSource.BattleNetProductDb, result.Source);
        var install = Assert.Single(result.Installs);
        Assert.Equal(Root, install.Root);
        Assert.Equal(["wowf", "wow_classic_beta"], install.Products.Select(p => p.Uid));
        Assert.Contains(result.Attempts, a => a.Contains("'battle.net' skipped"));
        Assert.Contains(result.Attempts, a => a.Contains("'bdko' skipped"));

        var versions = detector.ScanVersions(result);
        var forever = versions.Single(v => v.FolderName == "_forever_");
        Assert.Equal(("wowf", "1.60.1.70959"), (forever.ProductUid, forever.ClientVersion));
        Assert.True(forever.CanEnable);
        var beta = versions.Single(v => v.FolderName == "_classic_beta_");
        Assert.Equal(("wow_classic_beta", "1.60.1.70532"), (beta.ProductUid, beta.ClientVersion));
        Assert.False(beta.CanEnable);                                  // never launched

        Assert.False(fs.FileExists(Scratch + @"\product.db.copy"));    // the copy is cleaned up
    }

    [Fact]
    public void Parses_the_laptop_fixture_exactly()
    {
        var products = ProductDb.Parse(ProductDbFixtures.Laptop());

        Assert.Equal(["battle.net", "wowf", "bdko", "wow_classic_beta"], products.Select(p => p.Uid));
        var wowf = products.Single(p => p.Uid == "wowf");
        Assert.Equal(@"C:\Program Files (x86)\World of Warcraft", wowf.InstallPath);
        Assert.Equal("_forever_", wowf.GameFolder);
        Assert.Equal("1.60.1.70959", wowf.Version);
        Assert.Equal("_classic_beta_", products.Single(p => p.Uid == "wow_classic_beta").GameFolder);
    }

    [Fact]
    public void Paths_are_paired_with_their_own_products_folders_and_grouped_by_install()
    {
        // Two installs, products interleaved, settings fields in unusual order.
        var db = ProductDbFixtures.Database(
            ProductDbFixtures.Product("wow", "D:/Games/World of Warcraft/", "_retail_", "11.2.7.64000", folderFirst: true),
            ProductDbFixtures.Product("wowf", "C:/Program Files (x86)/World of Warcraft", "_forever_", "1.60.1.70959"),
            ProductDbFixtures.Product("wow_classic_era", "d:/games/world of warcraft", "_classic_era_", "1.15.8.1"));
        var fs = LaptopInstall()
            .File(@"D:\Games\World of Warcraft\_retail_\Wow.exe").Dir(@"D:\Games\World of Warcraft\_retail_\WTF\Account")
            .File(@"D:\Games\World of Warcraft\_classic_era_\WowClassic.exe").Dir(@"D:\Games\World of Warcraft\_classic_era_\WTF\Account")
            .Bytes(ProductDbPath, db);
        var detector = Detector(fs);

        var result = detector.DetectInstalls(null);

        Assert.Equal(2, result.Installs.Count);
        var d = result.Installs.Single(i => i.Root.StartsWith("D:"));
        Assert.Equal(@"D:\Games\World of Warcraft", d.Root);
        Assert.Equal(["_retail_", "_classic_era_"], d.Products.Select(p => p.GameFolder));
        Assert.Equal(["_forever_"], result.Installs.Single(i => i.Root.StartsWith("C:")).Products.Select(p => p.GameFolder));

        var versions = detector.ScanVersions(result);
        Assert.Equal("11.2.7.64000", versions.Single(v => v.FolderName == "_retail_").ClientVersion);
        Assert.Equal("wowf", versions.Single(v => v.FolderName == "_forever_").ProductUid);
    }

    [Fact]
    public void Folder_name_in_an_unexpected_field_is_still_found_within_the_same_product()
    {
        var db = ProductDbFixtures.Database(
            ProductDbFixtures.Product("wowf", "C:/Program Files (x86)/World of Warcraft", "_forever_", null, folderField: 14));
        var p = Assert.Single(ProductDb.Parse(db));
        Assert.Equal("_forever_", p.GameFolder);
    }

    [Fact]
    public void Warcraft_III_and_other_products_are_never_taken_for_wow()
    {
        var db = ProductDbFixtures.Database(
            ProductDbFixtures.Product("w3", "C:/Program Files (x86)/Warcraft III", "_retail_", "1.36"),
            ProductDbFixtures.Product("battle.net", "C:/Program Files (x86)/Battle.net", null, null));
        var fs = LaptopInstall().Bytes(ProductDbPath, db);
        fs.File(@"C:\Program Files (x86)\Warcraft III\_retail_\Warcraft III.exe").Dir(@"C:\Program Files (x86)\Warcraft III\_retail_\WTF");

        var result = Detector(fs).DetectInstalls(null);

        Assert.Equal(DetectionSource.CommonLocation, result.Source);           // fell back, ignoring W3
        Assert.Equal(Root, result.PrimaryRoot);
        Assert.Contains(result.Attempts, a => a.Contains("no World of Warcraft products"));
    }

    [Fact]
    public void Unreadable_or_locked_product_db_falls_back_to_the_folder_scan()
    {
        var corrupt = LaptopInstall().Bytes(ProductDbPath, [0x0A, 0xFF, 0xFF, 0xFF]);
        var r1 = Detector(corrupt).DetectInstalls(null);
        Assert.Equal((DetectionSource.CommonLocation, Root), (r1.Source, r1.PrimaryRoot));
        Assert.Contains(r1.Attempts, a => a.Contains("could not be read"));

        var locked = LaptopInstall().Bytes(ProductDbPath, ProductDbFixtures.Laptop());
        locked.Locked.Add(ProductDbPath);
        var r2 = Detector(locked).DetectInstalls(null);
        Assert.Equal((DetectionSource.CommonLocation, Root), (r2.Source, r2.PrimaryRoot));
    }

    [Fact]
    public void Product_db_pointing_at_a_removed_folder_falls_back()
    {
        var fs = new InMemoryFileSystem().Bytes(ProductDbPath, ProductDbFixtures.Laptop());
        fs.File(@"E:\World of Warcraft\_retail_\Wow.exe").Dir(@"E:\World of Warcraft\_retail_\WTF\Account");
        fs.Drives.Add(new DriveEntry(@"E:\", DriveKind.Fixed, "Games", true));

        var result = Detector(fs).DetectInstalls(null);

        Assert.Equal((DetectionSource.CommonLocation, @"E:\World of Warcraft"), (result.Source, result.PrimaryRoot));
        Assert.Contains(result.Attempts, a => a.Contains("but no game version was found there"));
    }

    [Fact]
    public void Home_pc_without_uninstall_entries_or_registry_still_detects_from_product_db()
    {
        var fs = LaptopInstall().Bytes(ProductDbPath, ProductDbFixtures.Laptop());
        var result = Detector(fs, new FakeRegistry()).DetectInstalls(null);
        Assert.Equal((DetectionSource.BattleNetProductDb, Root), (result.Source, result.PrimaryRoot));
    }

    [Fact]
    public void Blizzard_registry_key_is_only_a_hint_when_product_db_is_missing()
    {
        var fs = LaptopInstall(@"F:\Blizz\World of Warcraft");
        var reg = new FakeRegistry().Set(RegistryHive.LocalMachine, WowDetector.BlizzardKeyWow64, "InstallPath", @"F:\Blizz\World of Warcraft\_forever_\");

        var result = Detector(fs, reg).DetectInstalls(null);

        Assert.Equal((DetectionSource.RegistryHint, @"F:\Blizz\World of Warcraft"), (result.Source, result.PrimaryRoot));
    }

    [Fact]
    public void User_override_wins_and_a_vanished_override_falls_back()
    {
        var fs = LaptopInstall().Bytes(ProductDbPath, ProductDbFixtures.Laptop());
        fs.File(@"F:\WoW\_retail_\Wow.exe").Dir(@"F:\WoW\_retail_\WTF\Account");
        var detector = Detector(fs);

        var chosen = detector.DetectInstalls(@"F:\WoW");
        Assert.Equal((DetectionSource.UserOverride, @"F:\WoW"), (chosen.Source, chosen.PrimaryRoot));

        fs.RemoveTree(@"F:\WoW");
        var fallback = detector.DetectInstalls(@"F:\WoW");
        Assert.Equal(DetectionSource.BattleNetProductDb, fallback.Source);
        Assert.Contains(fallback.Attempts, a => a.Contains("no longer contains"));
    }

    [Fact]
    public void Not_found_asks_the_user()
    {
        var result = Detector(new InMemoryFileSystem()).DetectInstalls(null);
        Assert.False(result.Found);
        Assert.Equal(DetectionSource.NotFound, result.Source);
    }

    [Fact]
    public void Scans_every_game_version_including_new_ones_without_code_changes()
    {
        var fs = new InMemoryFileSystem();
        fs.File($@"{Root}\_retail_\Wow.exe").Dir($@"{Root}\_retail_\WTF\Account\PLAYER#1");
        fs.File($@"{Root}\_forever_\Wow.exe").Dir($@"{Root}\_forever_\WTF\Account");
        fs.File($@"{Root}\_classic_era_\WowClassic.exe").Dir($@"{Root}\_classic_era_\WTF");
        fs.File($@"{Root}\_ptr_\WowT.exe").File($@"{Root}\_ptr_\.flavor.info");
        fs.File($@"{Root}\_anniversary_\WowClassic.exe").Dir($@"{Root}\_anniversary_\WTF\Account");
        fs.Dir($@"{Root}\_junk_");
        fs.File($@"{Root}\Data\data.000");
        fs.File($@"{Root}\_backup_\notes.txt").Dir($@"{Root}\_backup_\WTF");

        var versions = Detector(fs).ScanVersions(Root);

        Assert.Equal(["_anniversary_", "_classic_era_", "_forever_", "_ptr_", "_retail_"], versions.Select(v => v.FolderName));
        Assert.True(versions.Single(v => v.FolderName == "_retail_").CanEnable);
        Assert.False(versions.Single(v => v.FolderName == "_classic_era_").CanEnable);
        Assert.False(versions.Single(v => v.FolderName == "_ptr_").CanEnable);
        Assert.Equal("classic_era", versions.Single(v => v.FolderName == "_classic_era_").Key);
    }

    [Fact]
    public void Linked_version_folders_and_linked_account_folders_are_not_trusted()
    {
        var fs = LaptopInstall();
        fs.Link($@"{Root}\_forever_\WTF\Account");
        fs.File($@"{Root}\_beta_\Wow.exe").Dir($@"{Root}\_beta_\WTF");
        fs.Link($@"{Root}\_beta_");

        var versions = Detector(fs).ScanVersions(Root);

        Assert.DoesNotContain(versions, v => v.FolderName == "_beta_");
        Assert.False(versions.Single(v => v.FolderName == "_forever_").CanEnable);
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
