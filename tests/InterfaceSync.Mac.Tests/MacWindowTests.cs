using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Tests.Fakes;
using InterfaceSync.Mac;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(InterfaceSync.Mac.Tests.TestAppBuilder))]

namespace InterfaceSync.Mac.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Runs the Mac windows headless (on any OS) against a real SyncService over a Mac-shaped temp layout.</summary>
public sealed class MacWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "is-mac-ui", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Host(SyncService service) : IMacHost
    {
        public SyncService Service { get; } = service;
        public void Run(Action work, Action? afterOnUi = null) { work(); if (afterOnUi != null) Dispatcher.UIThread.Post(afterOnUi); }
    }

    private (SyncService service, string myDrive) MakeService(bool setupComplete)
    {
        var home = Path.Combine(_root, "Users", "player");
        var env = new FakeEnvironment
        {
            Os = HostOs.MacOS, UserProfile = home,
            LocalAppData = Path.Combine(home, "Library", "Application Support"),
            RoamingAppData = Path.Combine(home, "Library", "Application Support"),
            ProgramData = Path.Combine(_root, "Users", "Shared"),
        };
        var wow = Path.Combine(_root, "Applications", "World of Warcraft");
        Directory.CreateDirectory(Path.Combine(wow, "_retail_", "World of Warcraft.app", "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(wow, "_retail_", "WTF", "Account", "PLAYER#1"));
        File.WriteAllText(Path.Combine(wow, "_retail_", "WTF", "Account", "PLAYER#1", "macros-cache.txt"), "mac settings");
        var myDrive = Path.Combine(home, "Library", "CloudStorage", "GoogleDrive-someone@example.com", "My Drive");
        Directory.CreateDirectory(myDrive);

        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(env.LocalAppData);
        var settings = new AppSettings { WowInstallOverride = wow, SetupComplete = setupComplete };
        if (setupComplete) settings.EnabledVersions["_retail_"] = true;
        new SettingsStore(fs, paths.Data).Save(settings);
        var service = new SyncService(fs, new NoRegistry(), new FakeProcessList(), env, new FakeClock(DateTime.UtcNow), new MemoryLog(), paths, "MacBook");
        service.Start();
        return (service, myDrive);
    }

    [AvaloniaFact]
    public void Setup_finds_the_mac_install_and_cloud_folder_then_runs_the_first_sync_to_finish()
    {
        var (service, myDrive) = MakeService(setupComplete: false);
        var window = new SetupWindow(new Host(service));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, window.VersionCount);
        Assert.Equal(["Google Drive", "OneDrive (not found on this Mac)", "Dropbox (not found on this Mac)"], window.ProviderChoices);
        Assert.True(window.CanStart);

        window.ClickStart();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFinished);
        var result = Assert.Single(window.Results);
        Assert.StartsWith("_retail_: Done. Google Drive had no settings for this version yet, so this Mac's were uploaded.", result);
        Assert.Equal("mac settings", File.ReadAllText(Path.Combine(myDrive, "InterfaceSync", "_retail_", "WTF-Account", "PLAYER#1", "macros-cache.txt")));
        window.Close();
        Assert.True(window.Started);
        Assert.True(service.Settings.SetupComplete);
    }

    [AvaloniaFact]
    public void Setup_offers_the_choice_when_the_cloud_already_has_settings_and_uses_it()
    {
        var (service, myDrive) = MakeService(setupComplete: false);
        var cloud = Path.Combine(myDrive, "InterfaceSync", "_retail_", "WTF-Account", "PLAYER#1");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "macros-cache.txt"), "from the PC");
        File.SetLastWriteTimeUtc(Path.Combine(cloud, "macros-cache.txt"), DateTime.UtcNow.AddDays(-1));   // older than this Mac's
        service.Reload();
        var window = new SetupWindow(new Host(service));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["_retail_"], window.ChoiceVersions);
        Assert.Contains("Quit the game on one computer", SetupWindow.HowItWorks);
        window.ClickStart();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFinished);
        Assert.Equal("mac settings", File.ReadAllText(Path.Combine(cloud, "macros-cache.txt")));    // newer one preselected: this Mac's
        window.Close();
    }

    [AvaloniaFact]
    public void Status_window_lists_each_version()
    {
        var (service, _) = MakeService(setupComplete: true);
        service.Poll();
        var window = new StatusWindow(new Host(service));
        window.Show();
        window.Refresh();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(window.Rows, r => r.StartsWith("_retail_: In sync"));
        Assert.Equal("In sync", window.Overall);
        window.AllowClose();
    }

    [AvaloniaFact]
    public void About_has_the_trademark_credit_and_every_menu_bar_state_has_an_icon()
    {
        Assert.Contains("World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc.", Dialogs.AboutText);
        Assert.Contains("Not affiliated with, endorsed by or sponsored by Blizzard Entertainment", Dialogs.AboutText);
        foreach (var state in Enum.GetValues<OverallState>()) Assert.NotNull(MacIcons.For(state));
        Dialogs.About().Show();
        Dispatcher.UIThread.RunJobs();
    }
}
