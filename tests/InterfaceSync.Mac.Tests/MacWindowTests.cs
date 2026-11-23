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
    public override void Initialize()
    {
        Styles.Add(MacTheme.Create());
        MacTheme.AddResources(Resources);
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });   // real text rendering, so the embedded font is exercised
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
        Assert.Equal(["Google Drive", "OneDrive (not found on this Mac)", "Dropbox (not found on this Mac)", "iCloud Drive (not found on this Mac)"], window.ProviderChoices);
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
    public void Appearance_switches_to_the_soft_dark_palette_and_back_and_is_saved()
    {
        var (service, _) = MakeService(setupComplete: true);
        var window = new StatusWindow(new Host(service));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.ChooseAppearance(AppTheme.Dark);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
            Assert.Equal(AppTheme.Dark, service.Settings.Theme);
            // The window background is the warm charcoal, not Fluent's near-black.
            Assert.True(Application.Current.TryGetResource("SystemRegionColor", Avalonia.Styling.ThemeVariant.Dark, out var region));
            Assert.Equal(Avalonia.Media.Color.Parse(InterfaceSync.Core.ThemePalette.Dark.Window), region);

            // Headings use the embedded Cinzel font, not a fallback.
            var heading = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(MacBrand.Heading, weight: Avalonia.Media.FontWeight.Bold), out var face);
            Assert.True(heading);
            Assert.Equal("Cinzel", face!.FamilyName);

            window.ChooseAppearance(AppTheme.Light);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Application.Current.ActualThemeVariant);
            Assert.Equal(AppTheme.Light, service.Settings.Theme);
        }
        finally { MacTheme.Apply(AppTheme.System); window.AllowClose(); }
    }

    [AvaloniaFact]
    public void Setup_switches_on_open_at_login_by_default()
    {
        var (service, _) = MakeService(setupComplete: false);
        var home = Path.Combine(_root, "Users", "player");
        var app = "/Applications/Interface Sync.app/Contents/MacOS/InterfaceSync";
        var startup = new MacStartAtLogin(new PhysicalFileSystem(), home, app, "io.github.zyphirwho.interfacesync");
        var window = new SetupWindow(new Host(service), startup);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.StartAtLoginChecked);
        window.ClickStart();
        Dispatcher.UIThread.RunJobs();

        var plist = Path.Combine(home, "Library", "LaunchAgents", "io.github.zyphirwho.interfacesync.plist");
        Assert.True(File.Exists(plist));
        Assert.Contains($"<string>{app}</string>", File.ReadAllText(plist));
        Assert.True(startup.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Setup_is_a_wizard_that_walks_through_its_steps()
    {
        var (service, _) = MakeService(setupComplete: false);
        var window = new SetupWindow(new Host(service));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Welcome", window.CurrentStep);
        window.ClickNext();
        Assert.Equal("Your setup", window.CurrentStep);
        window.ClickNext();
        Assert.Equal("Finish", window.CurrentStep);               // empty cloud: no "Your settings" step
        window.ClickNext();                                       // Start syncing
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsFinished);
        Assert.Equal("Syncing", window.CurrentStep);
        window.Close();
    }

    private sealed class NewerRelease : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    { "tag_name": "v9.9.9", "html_url": "https://github.com/o/r/releases/tag/v9.9.9", "draft": false, "prerelease": false, "body": "",
                      "assets": [ { "name": "InterfaceSync-9.9.9-macos-arm64.dmg", "browser_download_url": "https://github.com/o/r/releases/download/v9.9.9/InterfaceSync-9.9.9-macos-arm64.dmg", "size": 1 } ] }
                    """),
            });
    }

    [AvaloniaFact]
    public void Status_window_offers_an_available_update_and_has_the_theme_switch()
    {
        var (service, _) = MakeService(setupComplete: true);
        var updater = new InterfaceSync.Core.Updates.Updater(service, new HttpClient(new NewerRelease()), new FakeClock(DateTime.UtcNow), "3.0.0");
        updater.CheckAsync().GetAwaiter().GetResult();
        var window = new StatusWindow(new Host(service), null, updater);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.UpdateOffered);
        Assert.Equal("9.9.9", updater.Available!.Version);
        Assert.NotNull(updater.Available.MacImage(appleSilicon: true));
        window.AllowClose();
    }

    [AvaloniaFact]
    public void Restore_window_lists_backups_and_puts_the_settings_back()
    {
        var (service, _) = MakeService(setupComplete: true);
        service.Poll();
        var worker = Assert.Single(service.Workers);
        var account = Path.Combine(_root, "Applications", "World of Warcraft", "_retail_", "WTF", "Account");
        var entries = InterfaceSync.Core.Sync.FolderScanner.Scan(new PhysicalFileSystem(), account).Manifest.Entries.ToList();
        service.Backups.CreateFromFiles("_retail_", account, entries, "before-pull-changes");
        File.WriteAllText(Path.Combine(account, "PLAYER#1", "macros-cache.txt"), "broken");
        var window = new RestoreWindow(new Host(service), worker);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, window.BackupCount);
        window.RestoreAt(0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("mac settings", File.ReadAllText(Path.Combine(account, "PLAYER#1", "macros-cache.txt")));
    }

    [AvaloniaFact]
    public void Status_window_points_out_a_copy_of_the_cloud_folder_and_the_conflict_window_returns_the_choice()
    {
        var (service, myDrive) = MakeService(setupComplete: true);
        Directory.CreateDirectory(Path.Combine(myDrive, "InterfaceSync (1)", "_retail_"));
        service.Start();
        var window = new StatusWindow(new Host(service));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.DuplicateShown);
        window.AllowClose();

        InterfaceSync.Core.Sync.KeepChoice? chosen = null;
        var info = new InterfaceSync.Core.Sync.ConflictInfo { Reason = "Both changed.", LocalNewestUtc = DateTime.UtcNow.AddHours(-1), CloudNewestUtc = DateTime.UtcNow, CloudPushedBy = "HOME-PC" };
        var dialog = Dialogs.ChooseKeep("_retail_", info, k => chosen = k);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.Close();                                                  // "decide later"
        Assert.Null(chosen);
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
