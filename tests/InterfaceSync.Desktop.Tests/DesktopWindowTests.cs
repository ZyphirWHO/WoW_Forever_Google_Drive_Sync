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
using InterfaceSync.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(InterfaceSync.Desktop.Tests.TestAppBuilder))]

namespace InterfaceSync.Desktop.Tests;

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(Look.Create());
        Look.AddResources(Resources);
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });   // real text rendering, so the embedded font is exercised
}

/// <summary>Runs the Mac windows headless (on any OS) against a real SyncService over a Mac-shaped temp layout.</summary>
public sealed class DesktopWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "is-mac-ui", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class Host(SyncService service, InterfaceSync.Core.Updates.Updater? updater = null) : IAppHost
    {
        public SyncService Service { get; } = service;
        public InterfaceSync.Core.Updates.Updater? Updater => updater;
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
        Assert.Equal(["Google Drive", $"OneDrive (not found on this {Os.Machine})", $"Dropbox (not found on this {Os.Machine})", $"iCloud Drive (not found on this {Os.Machine})"], window.ProviderChoices);
        Assert.True(window.CanStart);

        window.ClickStart();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsFinished);
        var result = Assert.Single(window.Results);
        Assert.StartsWith($"_retail_: Done. Google Drive had no settings for this version yet, so this {Os.Machine}'s were uploaded.", result);
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
        var host = new Host(service);
        var window = new MainWindow(host);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            DesktopApp.ChooseTheme(host, AppTheme.Dark);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
            Assert.Equal(AppTheme.Dark, service.Settings.Theme);
            // The window background is the warm charcoal, not Fluent's near-black.
            Assert.True(Application.Current.TryGetResource("SystemRegionColor", Avalonia.Styling.ThemeVariant.Dark, out var region));
            Assert.Equal(Avalonia.Media.Color.Parse(InterfaceSync.Core.ThemePalette.Dark.Window), region);

            // Headings use the embedded Cinzel font, not a fallback.
            var heading = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(Brand.Heading, weight: Avalonia.Media.FontWeight.Bold), out var face);
            Assert.True(heading);
            Assert.Equal("Cinzel", face!.FamilyName);

            DesktopApp.ChooseTheme(host, AppTheme.Light);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Application.Current.ActualThemeVariant);
            Assert.Equal(AppTheme.Light, service.Settings.Theme);
        }
        finally { Look.Apply(AppTheme.System); window.AllowClose(); }
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

    private InterfaceSync.Core.Updates.Updater NewerUpdater(SyncService service)
    {
        var u = new InterfaceSync.Core.Updates.Updater(service, new HttpClient(new NewerRelease()), new FakeClock(DateTime.UtcNow), "3.0.0");
        u.CheckAsync().GetAwaiter().GetResult();
        return u;
    }

    private string Account => Path.Combine(_root, "Applications", "World of Warcraft", "_retail_", "WTF", "Account", "PLAYER#1");

    private void WriteSetting(string relative, string text)
    {
        var path = Path.Combine(Account, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [AvaloniaFact]
    public void Main_window_opens_on_home_and_moves_between_every_page()
    {
        var (service, _) = MakeService(setupComplete: true);
        service.Poll();
        var window = new MainWindow(new Host(service));
        window.Show();
        window.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Home", window.CurrentPage);
        var home = (HomePage)window.PageFor("Home");
        Assert.Equal("Everything is in sync", home.Headline);
        Assert.StartsWith("Last synced", home.Detail);
        Assert.Contains("_retail_: In sync", home.TileTexts);
        foreach (var page in MainWindow.PageKeys)
        {
            window.Navigate(page);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(page, window.CurrentPage);
        }
        window.AllowClose();
    }

    [AvaloniaFact]
    public void Messages_that_need_the_player_appear_above_every_page()
    {
        var (service, myDrive) = MakeService(setupComplete: true);
        Directory.CreateDirectory(Path.Combine(myDrive, "InterfaceSync (1)", "_retail_"));
        File.WriteAllText(Path.Combine(myDrive, "InterfaceSync (1)", "_retail_", "x.lua"), "x");
        service.Start();
        service.Poll();
        var window = new MainWindow(new Host(service, NewerUpdater(service)));
        window.Show();
        window.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.BarTitles, t => t == "Interface Sync 9.9.9 is available");
        Assert.Contains(window.BarTitles, t => t.Contains("made a copy"));
        window.Navigate("Settings");
        Assert.Contains(window.BarTitles, t => t == "Interface Sync 9.9.9 is available");
        window.ShowUpdated();
        Assert.Contains(window.BarTitles, t => t.StartsWith("Updated to version"));
        window.AllowClose();
    }

    [AvaloniaFact]
    public void Backups_page_saves_a_snapshot_restores_it_with_undo_and_deletes_it_with_undo()
    {
        var (service, _) = MakeService(setupComplete: true);
        service.Poll();
        var window = new MainWindow(new Host(service));
        window.Show();
        window.Navigate("Backups", "_retail_");
        Dispatcher.UIThread.RunJobs();
        var page = (BackupsPage)window.PageFor("Backups");

        var snap = page.SaveSnapshot("Raid night");
        Assert.NotNull(snap);
        Assert.Equal("Saved the snapshot \"Raid night\"", window.ToastText);
        Assert.Contains(page.Shown, b => b.Pinned);

        var macros = Path.Combine(Account, "macros-cache.txt");
        File.WriteAllText(macros, "changed later");
        page.Restore(snap!);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("mac settings", File.ReadAllText(macros));
        Assert.StartsWith("_retail_ is back as it was on", window.ToastText);
        window.ClickUndo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("changed later", File.ReadAllText(macros));            // undone exactly
        Assert.Equal("Restore undone.", window.ToastText);

        var zip = Path.Combine(_root, "copy.zip");
        BackupsPage.SaveZip(snap!, zip);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            Assert.Contains(archive.Entries, e => e.FullName.EndsWith("macros-cache.txt"));
        Assert.EndsWith("-Raid-night.zip", BackupsPage.ZipName(snap!));

        page.DeleteForTest(snap!);
        Assert.DoesNotContain(page.Shown, b => b.Pinned);                  // hidden at once
        Assert.True(Directory.Exists(snap!.Path));                          // not yet deleted
        window.ClickUndo();
        Assert.Contains(page.Shown, b => b.Pinned);
        page.DeleteForTest(page.Shown.First(b => b.Pinned));
        window.ExpireToast();                                               // the note goes away: now it's deleted
        Assert.False(Directory.Exists(snap.Path));
        window.AllowClose();
    }

    [AvaloniaFact]
    public void Choose_what_syncs_opens_in_the_side_panel_and_saves_what_was_unticked()
    {
        var (service, _) = MakeService(setupComplete: true);
        WriteSetting(@"Realm\Thrall\SavedVariables\Bartender4.lua", "x");
        WriteSetting(@"SavedVariables\WeakAuras.lua", "x");
        service.Poll();
        var window = new MainWindow(new Host(service));
        window.Show();
        Actions.ChooseWhatSyncs(window, service.Workers[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.DrawerOpen);
        Assert.Equal("Choose what syncs: _retail_", window.DrawerTitle);
        var view = Assert.IsType<SyncChoicesView>(window.DrawerContent);
        Assert.Equal(["Thrall - Realm"], view.CharacterNames);
        Assert.Equal(["Bartender4", "WeakAuras"], view.AddonNames);
        view.Filter("weak");
        view.SetCharacter(0, false);
        view.SetAddon(1, false);
        Assert.Equal(["Realm/Thrall"], view.Rules.Characters);
        Assert.Equal(["WeakAuras"], view.Rules.Addons);
        window.CloseDrawer();
        window.AllowClose();
    }

    [AvaloniaFact]
    public void Activity_computers_and_settings_pages_show_what_happened()
    {
        var (service, _) = MakeService(setupComplete: true);
        service.Poll();
        var window = new MainWindow(new Host(service, NewerUpdater(service)));
        window.Show();
        window.Navigate("Activity");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, ((ActivityPage)window.PageFor("Activity")).EntryCount);
        var entry = service.Activity.List()[0];
        Assert.StartsWith("Uploaded", entry.Headline);
        Assert.Contains("macros-cache.txt", ActivityPage.DetailsText(entry));

        window.Navigate("Computers");
        Assert.Contains("MacBook (this computer)", ((ComputersPage)window.PageFor("Computers")).Names);

        window.Navigate("Settings");
        var titles = ((SettingsPage)window.PageFor("Settings")).RowTitles;
        foreach (var t in new[] { "Appearance", "Language", "Notifications", "Pause syncing", "Check for updates", "Report a problem", "Run setup again" })
            Assert.Contains(t, titles);
        Assert.Contains(titles, t => t.StartsWith("_retail_"));
        window.AllowClose();
    }

    [AvaloniaFact]
    public void Tray_panel_shows_the_state_and_recent_activity()
    {
        var (service, _) = MakeService(setupComplete: true);
        service.Poll();
        var panel = new TrayPanel(new Host(service));
        panel.Show();
        panel.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Everything is in sync", panel.Headline);
        Assert.Contains(panel.RecentTexts, t => t.StartsWith("Uploaded"));
        Assert.Equal(3, AppIcons.SyncingFrames.Count);
        panel.Close();
    }

    [AvaloniaFact]
    public void About_has_the_trademark_credit_and_every_menu_bar_state_has_an_icon()
    {
        Assert.Contains("World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc.", Dialogs.AboutText);
        Assert.Contains("Not affiliated with, endorsed by or sponsored by Blizzard Entertainment", Dialogs.AboutText);
        foreach (var state in Enum.GetValues<OverallState>()) Assert.NotNull(AppIcons.For(state));
        Dialogs.About().Show();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Pictures of the windows in both themes (and German) for review, when INTERFACESYNC_SCREENSHOTS names a folder
    /// (CI prints them with the build). Does nothing otherwise.
    /// </summary>
    [AvaloniaFact]
    public void Screenshots_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("INTERFACESYNC_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        void Save(Avalonia.Controls.Window w, string name) { Dispatcher.UIThread.RunJobs(); w.CaptureRenderedFrame()?.Save(Path.Combine(folder, name + ".png")); }

        var (setupService, setupDrive) = MakeService(setupComplete: false);
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            Look.Apply(theme);
            var setup = new SetupWindow(new Host(setupService));
            setup.Show();
            for (int i = 1; ; i++)
            {
                Save(setup, $"setup-{theme}-{i}-{setup.CurrentStep.Replace(' ', '-')}");
                if (setup.CurrentStep == "Finish") break;
                setup.ClickNext();
            }
            setup.Close();
        }
        _ = setupDrive;

        var (service, myDrive) = MakeService(setupComplete: true);
        Directory.CreateDirectory(Path.Combine(myDrive, "InterfaceSync (1)", "_retail_"));
        File.WriteAllText(Path.Combine(myDrive, "InterfaceSync (1)", "_retail_", "x.lua"), "x");
        WriteSetting(@"SavedVariables\Details.lua", "d");
        WriteSetting(@"SavedVariables\WeakAuras.lua", "w");
        WriteSetting(@"Silvermoon\Thrall\SavedVariables\Bartender4.lua", "b");
        WriteSetting(@"Silvermoon\Jaina\macros-cache.txt", "j");
        service.Start();
        service.Poll();
        service.Activity.Add(InterfaceSync.Core.Sync.ActivityEntry.ForFiles(DateTime.UtcNow.AddMinutes(-3), "_retail_", InterfaceSync.Core.Sync.ActivityKind.Downloaded,
            [@"PLAYER#1\SavedVariables\Details.lua", @"PLAYER#1\SavedVariables\WeakAuras.lua", @"PLAYER#1\bindings-cache.wtf"], "LAPTOP"));
        service.SaveSnapshot(service.Workers[0], "Before the raid UI rework");
        var updater = NewerUpdater(service);
        try
        {
            foreach (var (theme, lang) in new[] { (AppTheme.Dark, "en"), (AppTheme.Light, "en"), (AppTheme.Dark, "de") })
            {
                Look.Apply(theme);
                InterfaceSync.Core.Localization.L.Use(lang);
                var tag = lang == "en" ? theme.ToString() : $"{theme}-{lang}";
                var window = new MainWindow(new Host(service, updater)) { Width = 1100, Height = 760 };
                window.AnimationsOff();
                window.Show();
                foreach (var page in MainWindow.PageKeys)
                {
                    window.Navigate(page, page == "Backups" ? "_retail_" : null);
                    window.Refresh();
                    Save(window, $"main-{tag}-{page}");
                }
                window.Navigate("Home");
                Actions.ChooseWhatSyncs(window, service.Workers[0]);
                Save(window, $"main-{tag}-choose-what-syncs");
                window.CloseDrawer();
                window.ShowToast("_retail_ is back as it was on Mon 23 Nov, 18:16.", undo: () => { });
                Save(window, $"main-{tag}-undo-note");
                window.AllowClose();

                var panel = new TrayPanel(new Host(service, updater));
                panel.Show();
                panel.Refresh();
                Save(panel, $"panel-{tag}");
                panel.Close();
            }
        }
        finally
        {
            InterfaceSync.Core.Localization.L.Use("en");
            Look.Apply(AppTheme.System);
        }
    }
}
