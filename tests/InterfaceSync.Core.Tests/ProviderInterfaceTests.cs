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

public class ProviderInterfaceTests
{
    private static ICloudProvider Make(CloudProvider k, InMemoryFileSystem? fs = null) =>
        CloudProviderCatalog.Create(k, fs ?? new InMemoryFileSystem(), new FakeRegistry(), new FakeEnvironment(), @"C:\Temp");

    [Theory]
    [InlineData(CloudProvider.GoogleDrive, "Google Drive", "Available offline")]
    [InlineData(CloudProvider.OneDrive, "OneDrive", "Always keep on this device")]
    [InlineData(CloudProvider.Dropbox, "Dropbox", "Make available offline")]
    public void Each_provider_names_itself_and_explains_keeping_files_on_the_device(CloudProvider kind, string name, string help)
    {
        var p = Make(kind);
        Assert.Equal((kind, name), (p.Kind, p.DisplayName));
        Assert.Contains(help, p.KeepOnDeviceHelp);
        Assert.Contains("InterfaceSync", p.KeepOnDeviceHelp);
    }

    [Theory]
    [InlineData(CloudProvider.GoogleDrive, "macros-cache (1).txt", true)]
    [InlineData(CloudProvider.GoogleDrive, "macros-cache-HOMEPC.txt", false)]
    [InlineData(CloudProvider.OneDrive, "macros-cache-HOMEPC.txt", true)]
    [InlineData(CloudProvider.OneDrive, "macros-cache (1).txt", false)]
    [InlineData(CloudProvider.Dropbox, "macros-cache (HomePC's conflicted copy 2026-11-18).txt", true)]
    [InlineData(CloudProvider.Dropbox, "macros-cache (1).txt", false)]
    public void Each_provider_recognises_its_own_conflict_copies(CloudProvider kind, string name, bool expected)
    {
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "macros-cache.txt" };
        Assert.Equal(expected, Make(kind).IsConflictCopy(name, siblings));
        Assert.True(CloudProviderCatalog.IsAnyConflictCopy(name, siblings) || !expected); // the scanner checks all three
    }

    [Theory]
    [InlineData(FileEntryInfo.RecallOnDataAccess, true)]
    [InlineData(FileEntryInfo.RecallOnOpen, true)]
    [InlineData(FileAttributes.Offline, true)]
    [InlineData(FileEntryInfo.Pinned, false)]
    [InlineData(FileAttributes.Archive, false)]
    public void Online_only_placeholders_are_recognised_by_every_provider(FileAttributes attribute, bool online)
    {
        var file = new FileEntryInfo(@"C:\x.lua", 10, DateTime.UtcNow, attribute);
        foreach (var k in CloudProviders.All) Assert.Equal(online, Make(k).IsOnlineOnly(file));
    }

    [Fact]
    public void OneDrive_pins_the_folder_and_everything_in_it_and_others_do_not_offer_it()
    {
        var fs = new InMemoryFileSystem().File(@"C:\OneDrive\InterfaceSync\_forever_\WTF-Account\a.lua");
        fs.SetAttributes(@"C:\OneDrive\InterfaceSync\_forever_\WTF-Account\a.lua", FileAttributes.Archive | FileEntryInfo.Unpinned);
        var one = Make(CloudProvider.OneDrive, fs);
        Assert.True(one.CanPinFolder);
        one.PinFolder(fs, @"C:\OneDrive\InterfaceSync");

        foreach (var p in new[] { @"C:\OneDrive\InterfaceSync", @"C:\OneDrive\InterfaceSync\_forever_\WTF-Account", @"C:\OneDrive\InterfaceSync\_forever_\WTF-Account\a.lua" })
        {
            Assert.True((fs.GetAttributes(p) & FileEntryInfo.Pinned) != 0, p);
            Assert.True((fs.GetAttributes(p) & FileEntryInfo.Unpinned) == 0, p);
        }
        Assert.True((fs.GetAttributes(@"C:\OneDrive\InterfaceSync\_forever_\WTF-Account\a.lua") & FileAttributes.Archive) != 0); // others kept

        Assert.False(Make(CloudProvider.GoogleDrive).CanPinFolder);
        Assert.False(Make(CloudProvider.Dropbox).CanPinFolder);
        Assert.Throws<NotSupportedException>(() => Make(CloudProvider.Dropbox).PinFolder(fs, @"C:\x"));
    }
}

/// <summary>Two PCs share a Google Drive folder and a OneDrive folder; one moves the data across.</summary>
public sealed class ProviderMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "is-provmove", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc));
    private readonly PhysicalFileSystem _fs = new();
    private string Google => Path.Combine(_root, "cloud", "google", "My Drive");
    private string OneDrive => Path.Combine(_root, "cloud", "onedrive", "OneDrive");

    public ProviderMoveTests()
    {
        Directory.CreateDirectory(Google);
        Directory.CreateDirectory(OneDrive);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed record Pc(SyncService Service, string Wow, FakeProcessList Processes)
    {
        public string Setting => Path.Combine(Wow, "_forever_", "WTF", "Account", "PLAYER#1", "macros-cache.txt");
    }

    private Pc MakePc(string name, string contents)
    {
        var home = Path.Combine(_root, name);
        var env = new FakeEnvironment
        {
            LocalAppData = Path.Combine(home, "LocalAppData"), UserProfile = Path.Combine(home, "User"),
            ProgramData = Path.Combine(home, "ProgramData"), RoamingAppData = Path.Combine(home, "Roaming"),
        };
        var wow = Path.Combine(home, "World of Warcraft");
        Directory.CreateDirectory(Path.Combine(wow, "_forever_", "WTF", "Account", "PLAYER#1"));
        File.WriteAllText(Path.Combine(wow, "_forever_", "Wow.exe"), "exe");
        var setting = Path.Combine(wow, "_forever_", "WTF", "Account", "PLAYER#1", "macros-cache.txt");
        File.WriteAllText(setting, contents);
        File.SetLastWriteTimeUtc(setting, _clock.UtcNow);
        var paths = new AppPaths(env.LocalAppData);
        var settings = new AppSettings { WowInstallOverride = wow, SetupComplete = true, CloudProvider = CloudProvider.GoogleDrive, DriveOverride = Google };
        settings.EnabledVersions["_forever_"] = true;
        new SettingsStore(_fs, paths.Data).Save(settings);
        var processes = new FakeProcessList();
        var s = new SyncService(_fs, new FakeRegistry(), processes, env, _clock, new MemoryLog(), paths, name);
        s.Start();
        return new Pc(s, wow, processes);
    }

    private void Edit(Pc pc, string contents)
    {
        _clock.Advance(TimeSpan.FromMinutes(10));
        File.WriteAllText(pc.Setting, contents);
        File.SetLastWriteTimeUtc(pc.Setting, _clock.UtcNow);
    }

    private string CloudFile(string root) => Path.Combine(root, "InterfaceSync", "_forever_", "WTF-Account", "PLAYER#1", "macros-cache.txt");

    [Fact]
    public void Moving_to_another_app_copies_the_data_backs_up_the_old_copy_and_pauses_the_other_pc()
    {
        var home = MakePc("HomePC", "home settings");
        home.Service.Poll();                                    // first sync: uploaded to Google
        var laptop = MakePc("WorkLaptop", "laptop settings");
        laptop.Service.Poll();                                  // joins: takes the home settings

        Edit(home, "home, latest");                             // a pending change is pushed before moving
        var result = home.Service.ChangeProvider(CloudProvider.OneDrive, OneDrive, keepOnDevice: false);

        Assert.True(result.Changed, result.Message);
        Assert.Equal("home, latest", File.ReadAllText(CloudFile(OneDrive)));
        Assert.Equal("home, latest", File.ReadAllText(CloudFile(Google)));       // old copy left in place
        Assert.True(File.Exists(Path.Combine(Google, "InterfaceSync", MovedMarker.FileName)));
        Assert.Contains(home.Service.Backups.List("_forever_"), b => b.Reason == "cloud-before-provider-move");
        Assert.Equal(CloudProvider.OneDrive, home.Service.Drive!.Provider);
        home.Service.SyncNow();
        Assert.Equal(OverallState.InSync, home.Service.State);                   // same commit: no re-download

        // The laptop, still on Google Drive, stops instead of syncing there.
        Edit(laptop, "laptop edit after the move");
        laptop.Service.Poll();
        Assert.Equal(OverallState.NeedsAttention, laptop.Service.State);
        Assert.Contains("moved to OneDrive", laptop.Service.StateText);
        Assert.Equal("home, latest", File.ReadAllText(CloudFile(Google)));       // nothing pushed to the old folder

        // Switching the laptop too: OneDrive already has the data, so it joins it.
        var joined = laptop.Service.ChangeProvider(CloudProvider.OneDrive, OneDrive, keepOnDevice: false);
        Assert.True(joined.Changed);
        Assert.Contains("already has", joined.Message);
        laptop.Service.Poll();
        Assert.Equal(CloudProvider.OneDrive, laptop.Service.Drive!.Provider);
        // The laptop changed its settings after the move and the cloud copy changed too: it asks, never picks silently.
        var worker = laptop.Service.Workers.Single();
        Assert.NotNull(worker.State.Conflict);
        Assert.Equal(SyncOutcome.Pulled, laptop.Service.Resolve(worker, KeepChoice.Cloud));
        Assert.Equal("home, latest", File.ReadAllText(laptop.Setting));
    }

    [Fact]
    public void Moving_back_replaces_the_stale_copy_after_backing_it_up()
    {
        var home = MakePc("HomePC", "v1");
        home.Service.Poll();
        Assert.True(home.Service.ChangeProvider(CloudProvider.OneDrive, OneDrive, false).Changed);
        Edit(home, "v2 on OneDrive");
        home.Service.SyncNow();

        var back = home.Service.ChangeProvider(CloudProvider.GoogleDrive, Google, false);

        Assert.True(back.Changed, back.Message);
        Assert.Equal("v2 on OneDrive", File.ReadAllText(CloudFile(Google)));
        Assert.False(File.Exists(Path.Combine(Google, "InterfaceSync", MovedMarker.FileName)));
        Assert.True(File.Exists(Path.Combine(OneDrive, "InterfaceSync", MovedMarker.FileName)));
        Assert.Contains(home.Service.Backups.List("_forever_"), b => b.Reason == "cloud-before-provider-move-old-copy");
    }

    [Fact]
    public void Moving_is_refused_while_the_game_runs_or_when_the_app_is_missing()
    {
        var home = MakePc("HomePC", "v1");
        home.Service.Poll();
        home.Processes.Processes.Add(new ProcessEntry(1, "Wow", Path.Combine(home.Wow, "_forever_", "Wow.exe")));
        var running = home.Service.ChangeProvider(CloudProvider.OneDrive, OneDrive, false);
        Assert.False(running.Changed);
        Assert.Contains("Close _forever_", running.Message);

        home.Processes.Processes.Clear();
        var missing = home.Service.ChangeProvider(CloudProvider.Dropbox, Path.Combine(_root, "no-such-folder"), false);
        Assert.False(missing.Changed);
        Assert.Contains("not found", missing.Message);
        Assert.Equal(CloudProvider.GoogleDrive, home.Service.Drive!.Provider);
    }
}
