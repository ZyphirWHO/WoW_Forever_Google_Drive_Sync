using InterfaceSync.Core.Abstractions;
using InterfaceSync.App;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.App.Tests;

/// <summary>
/// Builds and shows the real windows against a real SyncService over temp folders, on an STA thread,
/// so a window that throws while opening fails CI instead of reaching a player.
/// </summary>
public sealed class WindowSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "interfacesync-ui", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SyncService Service(bool setupComplete, params (string folder, bool launched)[] versions)
    {
        var env = new FakeEnvironment
        {
            LocalAppData = Path.Combine(_root, "LocalAppData"),
            UserProfile = Path.Combine(_root, "Users", "Player"),
            ProgramData = Path.Combine(_root, "ProgramData"),
        };
        var wow = Path.Combine(_root, "World of Warcraft");
        foreach (var (folder, launched) in versions)
        {
            Directory.CreateDirectory(Path.Combine(wow, folder, launched ? Path.Combine("WTF", "Account") : "WTF"));
            File.WriteAllText(Path.Combine(wow, folder, "Wow.exe"), "exe");
            if (launched) File.WriteAllText(Path.Combine(wow, folder, "WTF", "Account", "macros-cache.txt"), folder);
        }
        Directory.CreateDirectory(Path.Combine(env.UserProfile, "My Drive"));

        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(env.LocalAppData);
        var settings = new AppSettings { WowInstallOverride = wow, SetupComplete = setupComplete };
        if (setupComplete) foreach (var (folder, launched) in versions) settings.EnabledVersions[folder] = launched;
        new SettingsStore(fs, paths.Data).Save(settings);

        var service = new SyncService(fs, new FakeRegistry(), new FakeProcessList(), env, new FakeClock(DateTime.UtcNow),
            new MemoryLog(), paths, "TestPC");
        service.Start();
        return service;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI test timed out");
        if (failure != null) throw new Exception("Window failed: " + failure.Message, failure);
    }

    [Fact]
    public void First_run_window_opens_with_launched_versions_ticked()
    {
        var service = Service(setupComplete: false, ("_forever_", true), ("_classic_beta_", false), ("_retail_", true));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());   // the 2.0 dev build crashed here
            form.Show();
            Application.DoEvents();

            Assert.Equal(2, form.VersionCount);                           // _classic_beta_ never launched: not listed
            Assert.Equal(2, form.CheckedCount);
            Assert.True(form.CanStart);

            form.SetChecked(0, false);
            form.SetChecked(1, false);
            Assert.False(form.CanStart);                                  // nothing ticked: can't start
            form.SetChecked(1, true);
            Assert.True(form.CanStart);
            form.Close();
        });
    }

    [Fact]
    public void First_run_shows_each_versions_first_sync_then_finish()
    {
        var service = Service(setupComplete: false, ("_forever_", true), ("_retail_", true));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());
            form.Show();
            Application.DoEvents();
            form.ClickStart();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!form.IsFinished && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }

            Assert.True(form.IsFinished, "Finish button never enabled");
            Assert.Equal(2, form.Results.Count);
            Assert.All(form.Results, r => Assert.Contains("Done.", r));
            Assert.Contains(form.Results, r => r.StartsWith("_forever_: Done. Google Drive had no settings"));
            form.Close();
            Assert.Equal(DialogResult.OK, form.DialogResult);
        });
        Assert.True(service.Settings.SetupComplete);
    }

    [Fact]
    public void First_run_offers_every_cloud_app_and_switching_updates_the_folder()
    {
        var service = Service(setupComplete: false, ("_forever_", true));
        Directory.CreateDirectory(Path.Combine(_root, "Users", "Player", "Dropbox"));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());
            form.Show();
            Application.DoEvents();
            Assert.Equal(["Google Drive", "OneDrive (not found on this PC)", "Dropbox", "iCloud Drive (not found on this PC)"], form.ProviderChoices);

            form.ChooseProvider(InterfaceSync.Core.Detection.CloudProvider.Dropbox);
            Application.DoEvents();
            Assert.EndsWith("Dropbox", form.CloudFolderText);
            Assert.True(form.CanStart);

            form.ChooseProvider(InterfaceSync.Core.Detection.CloudProvider.OneDrive);
            Application.DoEvents();
            Assert.StartsWith("Not found", form.CloudFolderText);
            Assert.False(form.CanStart);

            // iCloud for Windows' folder appears: choosing iCloud Drive finds it.
            Directory.CreateDirectory(Path.Combine(_root, "Users", "Player", "iCloudDrive"));
            form.ChooseProvider(InterfaceSync.Core.Detection.CloudProvider.ICloudDrive);
            Application.DoEvents();
            Assert.EndsWith("iCloudDrive", form.CloudFolderText);
            Assert.True(form.CanStart);
            form.Close();
        });
        Assert.Equal(InterfaceSync.Core.Detection.CloudProvider.ICloudDrive, service.Settings.CloudProvider);
    }

    [Fact]
    public void OneDrive_offers_keep_on_device_unticked_so_it_only_happens_with_the_players_ok()
    {
        var service = Service(setupComplete: false, ("_forever_", true));
        Directory.CreateDirectory(Path.Combine(_root, "Users", "Player", "OneDrive"));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());
            form.Show();
            Application.DoEvents();
            Assert.False(form.KeepOnDeviceOffered);                       // Google Drive selected
            form.ChooseProvider(InterfaceSync.Core.Detection.CloudProvider.OneDrive);
            Application.DoEvents();
            Assert.True(form.KeepOnDeviceOffered);
            Assert.False(form.KeepOnDeviceTicked);
            form.Close();
        });
    }

    [Fact]
    public void Change_cloud_folder_window_opens_and_preselects_another_app_that_is_installed()
    {
        var service = Service(setupComplete: true, ("_forever_", true));
        Directory.CreateDirectory(Path.Combine(_root, "Users", "Player", "Dropbox"));
        OnSta(() =>
        {
            using var form = new ChangeCloudFolder(service);
            form.Show();
            Application.DoEvents();
            Assert.Equal(InterfaceSync.Core.Detection.CloudProvider.Dropbox, form.Chosen);
            Assert.False(form.KeepOnDevice);
            form.Close();
        });
    }

    [Fact]
    public void About_text_has_the_trademark_credit_and_not_affiliated_statement()
    {
        var text = StatusForm.AboutText;
        Assert.Contains("World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc., in the U.S. and/or other countries.", text);
        Assert.Contains("Not affiliated with, endorsed by or sponsored by Blizzard Entertainment", text);
        Assert.Contains("World of Warcraft®", text);
        Assert.Contains("MIT License", text);
    }

    [Fact]
    public void First_run_explains_how_it_works_and_offers_the_choice_when_the_cloud_already_has_settings()
    {
        // Another computer already uploaded _forever_ settings, newer than this PC's.
        var cloud = Path.Combine(_root, "Users", "Player", "My Drive", "InterfaceSync", "_forever_", "WTF-Account");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "macros-cache.txt"), "from the other computer");
        File.SetLastWriteTimeUtc(Path.Combine(cloud, "macros-cache.txt"), DateTime.UtcNow.AddDays(1));
        var service = Service(setupComplete: false, ("_forever_", true));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());
            form.Show();
            Application.DoEvents();
            Assert.Contains("Quit the game on one computer before playing on another", FirstRun.HowItWorks);
            Assert.Equal(["_forever_"], form.ChoiceVersions);

            form.ChooseThisComputer("_forever_");
            form.ClickStart();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!form.IsFinished && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            Assert.True(form.IsFinished);
            form.Close();
        });
        Assert.Equal("_forever_", File.ReadAllText(Path.Combine(cloud, "macros-cache.txt")));   // this PC's uploaded, as chosen
    }

    [Fact]
    public void First_run_window_opens_when_nothing_is_found()
    {
        var service = Service(setupComplete: false);
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());
            form.Show();
            Application.DoEvents();
            Assert.Equal(0, form.VersionCount);
            Assert.False(form.CanStart);
            form.Close();
        });
    }

    private sealed class TestHost(SyncService service) : IStatusHost
    {
        public SyncService Service { get; } = service;
        public void Run(Action work, Action? afterOnUi = null) { work(); afterOnUi?.Invoke(); }
    }

    [Fact]
    public void Status_window_opens_and_refreshes()
    {
        var service = Service(setupComplete: true, ("_forever_", true), ("_retail_", true), ("_ptr_", false));
        service.Poll();
        OnSta(() =>
        {
            using var form = new StatusForm(new TestHost(service));
            form.Show();
            Application.DoEvents();
            form.RefreshView();
            Application.DoEvents();
            // one card per game version: the two synced, then the one that's off
            Assert.Equal(["_forever_", "_retail_", "_ptr_"], form.VersionTitles.Select(t => t.Split(' ')[0]));
            form.RefreshView();                                      // updates in place
            Assert.Equal(3, form.VersionTitles.Count);
            form.AllowClose();
            form.Close();
        });
    }

    [Fact]
    public void Setup_is_a_wizard_that_walks_through_its_steps()
    {
        var service = Service(setupComplete: false, ("_forever_", true));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry(), new WindowsStartAtLogin(new FakeRegistry(), Path.Combine(_root, "InterfaceSync.exe"), Path.Combine(_root, "Temp")));
            form.Show();
            Application.DoEvents();
            Assert.Equal("Welcome", form.CurrentStep);
            form.ClickNext();
            Assert.Equal("Your setup", form.CurrentStep);
            form.ClickNext();
            Assert.Equal("Finish", form.CurrentStep);               // the cloud is empty: no "Your settings" step
            form.ClickNext();                                       // Start syncing
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!form.IsFinished && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            Assert.True(form.IsFinished);
            Assert.Equal("Syncing", form.CurrentStep);
            form.Close();
        });
    }

    [Fact]
    public void Setup_switches_on_start_with_Windows_by_default_and_the_status_window_can_switch_it_off()
    {
        var service = Service(setupComplete: false, ("_forever_", true));
        var registry = new FakeRegistry();
        var exe = Path.Combine(_root, "Programs", "InterfaceSync.exe");
        var startup = new WindowsStartAtLogin(registry, exe, Path.Combine(_root, "Temp"));
        OnSta(() =>
        {
            using (var form = new FirstRun(service, registry, startup))
            {
                form.Show();
                Application.DoEvents();
                Assert.True(form.StartAtLoginChecked);
                form.ClickStart();
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!form.IsFinished && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                form.Close();
            }
            Assert.Equal($"\"{exe}\" --tray", registry.GetString(RegistryHive.CurrentUser, WindowsStartAtLogin.RunKey, "InterfaceSync"));

            using var status = new StatusForm(new TestHost(service)) { StartAtLogin = startup };
            status.Show();
            Application.DoEvents();
            var box = AllControls(status).OfType<CheckBox>().Single(c => c.Text == "Start with Windows");
            Assert.True(box.Checked);
            status.ToggleStartAtLogin();
            Application.DoEvents();
            Assert.False(box.Checked);
            Assert.False(startup.IsEnabled);
            status.AllowClose();
            status.Close();
        });
    }

    [Fact]
    public void Appearance_switches_the_open_window_to_dark_and_back_and_is_saved()
    {
        var service = Service(setupComplete: true, ("_forever_", true), ("_ptr_", false));
        service.Poll();
        OnSta(() =>
        {
            try
            {
                Theme.Set(AppTheme.Light);
                using var form = new StatusForm(new TestHost(service));
                form.Show();
                Application.DoEvents();
                Assert.Equal(Theme.Window, form.BackColor);           // parchment
                var lightBack = form.BackColor;

                form.ChooseAppearance(AppTheme.Dark);
                Application.DoEvents();
                Assert.True(Theme.IsDark);
                Assert.Equal(Theme.Window, form.BackColor);
                Assert.Equal(Theme.Surface, form.Headline.BackColor);      // cards take the surface colour
                Assert.NotEmpty(AllControls(form).OfType<ModernButton>());
                using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height)) form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));

                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (service.Settings.Theme != AppTheme.Dark && DateTime.UtcNow < deadline) Thread.Sleep(20);
                Assert.Equal(AppTheme.Dark, service.Settings.Theme);

                form.ChooseAppearance(AppTheme.Light);
                Application.DoEvents();
                Assert.False(Theme.IsDark);
                Assert.Equal(lightBack, form.BackColor);
                Assert.Equal(Theme.Surface, form.Headline.BackColor);
                Assert.True(Brand.HasCinzel);                             // the heading font loaded from the exe
                Assert.NotNull(form.Banner.Toggle);                       // the sun/moon switch in the banner
                form.Banner.Toggle!.PerformClick();
                Application.DoEvents();
                Assert.True(Theme.IsDark);                                // light → dark
                form.Banner.Toggle.PerformClick();
                Application.DoEvents();
                Assert.False(Theme.IsDark);
                Assert.Single(AllControls(form).OfType<BrandBanner>());
                form.AllowClose();
                form.Close();
            }
            finally { Theme.Set(AppTheme.System); }
        });
    }

    /// <summary>
    /// Saves pictures of the windows in both themes for review when INTERFACESYNC_SCREENSHOTS names a folder
    /// (CI uploads them with the build). Does nothing otherwise.
    /// </summary>
    [Fact]
    public void Screenshots_for_review()
    {
        var folder = Environment.GetEnvironmentVariable("INTERFACESYNC_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        var cloud = Path.Combine(_root, "Users", "Player", "My Drive", "InterfaceSync", "_retail_", "WTF-Account");
        Directory.CreateDirectory(cloud);
        File.WriteAllText(Path.Combine(cloud, "macros-cache.txt"), "from the laptop");
        var setup = Service(setupComplete: false, ("_retail_", true), ("_classic_", false));
        var shots = new[] { AppTheme.Dark, AppTheme.Light };
        OnSta(() =>
        {
            try
            {
                foreach (var theme in shots)
                {
                    Theme.Set(theme);
                    using var form = new FirstRun(setup, new FakeRegistry(), new WindowsStartAtLogin(new FakeRegistry(), @"C:\Users\Player\Documents\Interface Sync\InterfaceSync.exe", @"C:\Temp"));
                    form.Show();
                    Application.DoEvents();
                    for (int step = 1; ; step++)
                    {
                        Save(form, Path.Combine(folder, $"windows-setup-{theme}-{step}-{form.CurrentStep.Replace(' ', '-')}.png"));
                        if (form.CurrentStep == "Finish") break;
                        form.ClickNext();
                        Application.DoEvents();
                    }
                    form.Close();
                }
            }
            finally { Theme.Set(AppTheme.System); }
        });

        // The status window after setup, on a PC that has synced.
        var dupCopy = Path.Combine(_root, "Users", "Player", "My Drive", "InterfaceSync (1)", "_retail_");
        Directory.CreateDirectory(dupCopy);
        File.WriteAllText(Path.Combine(dupCopy, "x.lua"), "x");
        var status = Service(setupComplete: true, ("_retail_", true), ("_classic_", false));
        status.Poll();
        var retailAccount = Path.Combine(_root, "World of Warcraft", "_retail_", "WTF", "Account");
        var all = InterfaceSync.Core.Sync.FolderScanner.Scan(new PhysicalFileSystem(), retailAccount).Manifest.Entries.ToList();
        status.Backups.CreateFromFiles("_retail_", retailAccount, all, "before-pull-changes");
        status.Backups.Create("_retail_", retailAccount, "cloud-copy-conflict");
        OnSta(() =>
        {
            try
            {
                foreach (var theme in shots)
                {
                    Theme.Set(theme);
                    using var form = new StatusForm(new TestHost(status)) { Updater = UpdaterWithNewerRelease(status), InstallUpdate = _ => { } };
                    form.Show();
                    form.RefreshView();
                    Application.DoEvents();
                    Save(form, Path.Combine(folder, $"windows-status-{theme}.png"));
                    var conflict = new InterfaceSync.Core.Sync.ConflictInfo
                    {
                        Reason = "Both this PC and LAPTOP changed _retail_ since the last sync, so it isn't clear which settings are newest.",
                        LocalNewestUtc = DateTime.UtcNow.AddHours(-3), CloudNewestUtc = DateTime.UtcNow.AddMinutes(-12), LocalMachine = "HOME-PC", CloudPushedBy = "LAPTOP",
                    };
                    using (var c = new ConflictDialog("_retail_", conflict)) { c.Show(); Application.DoEvents(); Save(c, Path.Combine(folder, $"windows-conflict-{theme}.png")); c.Close(); }
                    using (var a = new AboutDialog()) { a.Show(); Application.DoEvents(); Save(a, Path.Combine(folder, $"windows-about-{theme}.png")); a.Close(); }
                    using (var m = ThemedDialog.Create("Your current settings are backed up first, and the restored settings are then uploaded to your other computers.",
                               "Put _retail_'s settings back as they were on Monday 23 November 2026 18:16?", DialogKind.Question,
                               ("Cancel", DialogResult.Cancel, ButtonKind.Ghost), ("Restore", DialogResult.OK, ButtonKind.Primary)))
                    { m.Show(); Application.DoEvents(); Save(m, Path.Combine(folder, $"windows-message-{theme}.png")); m.Close(); }
                    using (var restore = new RestoreDialog(new TestHost(status), status.Workers[0]))
                    {
                        restore.Show();
                        Application.DoEvents();
                        Save(restore, Path.Combine(folder, $"windows-restore-{theme}.png"));
                        restore.Close();
                    }
                    form.AllowClose();
                    form.Close();
                }
            }
            finally { Theme.Set(AppTheme.System); }
        });

        static void Save(Form form, string path)
        {
            using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    /// <summary>GitHub answering "latest release: 9.9.9".</summary>
    private sealed class NewerRelease : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    { "tag_name": "v9.9.9", "html_url": "https://github.com/o/r/releases/tag/v9.9.9", "draft": false, "prerelease": false, "body": "",
                      "assets": [ { "name": "InterfaceSync-Setup-9.9.9.exe", "browser_download_url": "https://github.com/o/r/releases/download/v9.9.9/InterfaceSync-Setup-9.9.9.exe", "size": 1, "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] }
                    """),
            });
    }

    private static InterfaceSync.Core.Updates.Updater UpdaterWithNewerRelease(SyncService service)
    {
        var u = new InterfaceSync.Core.Updates.Updater(service, new HttpClient(new NewerRelease()), new FakeClock(DateTime.UtcNow), "3.0.0");
        u.CheckAsync().GetAwaiter().GetResult();
        return u;
    }

    [Fact]
    public void Check_now_brings_the_update_card_into_view_and_an_update_reopens_the_status_window_with_a_note()
    {
        var service = Service(setupComplete: true, ("_forever_", true), ("_retail_", true), ("_ptr_", false));
        service.Poll();
        var updater = new InterfaceSync.Core.Updates.Updater(service, new HttpClient(new NewerRelease()), new FakeClock(DateTime.UtcNow), "3.0.0");
        OnSta(() =>
        {
            using var form = new StatusForm(new TestHost(service)) { Updater = updater, InstallUpdate = _ => { } };
            form.Size = new System.Drawing.Size(860, 420);                // short, so it scrolls
            form.Show();
            form.RefreshView();
            Application.DoEvents();
            form.ScrollToBottom();
            Application.DoEvents();
            Assert.True(form.ScrollY > 0, "the window should be scrolled down to start with");

            form.CheckForUpdates();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (updater.State != InterfaceSync.Core.Updates.UpdateState.Available && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            form.RefreshView();
            Application.DoEvents();
            Assert.True(form.UpdateOffered);
            Assert.Equal(0, form.ScrollY);                                   // the update card is in view

            form.ShowUpdated();
            Assert.Contains(AllControls(form).OfType<Label>(), l => l.Text.StartsWith($"Updated to version {InterfaceSync.Core.ProductInfo.DisplayVersion}"));
            form.AllowClose();
            form.Close();
        });
    }

    [Fact]
    public void Status_window_offers_an_available_update()
    {
        var service = Service(setupComplete: true, ("_forever_", true));
        service.Poll();
        var updater = UpdaterWithNewerRelease(service);
        OnSta(() =>
        {
            IWin32Window? asked = null;
            using var form = new StatusForm(new TestHost(service)) { Updater = updater, InstallUpdate = owner => asked = owner };
            form.Show();
            form.RefreshView();
            Application.DoEvents();
            Assert.True(form.UpdateOffered);
            var button = AllControls(form).OfType<ModernButton>().Single(b => b.Text == "Update now");
            button.PerformClick();
            Assert.Same(form, asked);
            form.AllowClose();
            form.Close();
        });
    }

    [Fact]
    public void Restore_dialog_lists_backups_and_puts_the_settings_back()
    {
        var service = Service(setupComplete: true, ("_forever_", true));
        service.Poll();
        var worker = Assert.Single(service.Workers);
        var account = Path.Combine(_root, "World of Warcraft", "_forever_", "WTF", "Account");
        var manifest = InterfaceSync.Core.Sync.FolderScanner.Scan(new PhysicalFileSystem(), account).Manifest;
        var backup = service.Backups.CreateFromFiles("_forever_", account, manifest.Entries.ToList(), "before-pull-changes");
        File.WriteAllText(Path.Combine(account, "macros-cache.txt"), "broken by an addon");
        OnSta(() =>
        {
            using var dialog = new RestoreDialog(new TestHost(service), worker) { Quiet = true };
            dialog.Show();
            Application.DoEvents();
            Assert.Equal(1, dialog.BackupCount);
            Assert.Equal("Before downloading settings", backup.Description);
            dialog.RestoreNow(backup);
            Application.DoEvents();
            Assert.NotNull(dialog.LastOutcome);
        });
        Assert.Equal("_forever_", File.ReadAllText(Path.Combine(account, "macros-cache.txt")));
    }

    [Fact]
    public void Status_window_points_out_a_copy_of_the_cloud_folder_and_hides_it()
    {
        var copy = Path.Combine(_root, "Users", "Player", "My Drive", "InterfaceSync (1)", "_forever_");
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "x.lua"), "x");
        var service = Service(setupComplete: true, ("_forever_", true));
        service.Poll();
        OnSta(() =>
        {
            using var form = new StatusForm(new TestHost(service));
            form.Show();
            form.RefreshView();
            Application.DoEvents();
            Assert.True(form.DuplicateShown);
            AllControls(form).OfType<ModernButton>().Single(b => b.Text == "Hide").PerformClick();
            Application.DoEvents();
            Assert.False(form.DuplicateShown);
            form.AllowClose();
            form.Close();
        });
        Assert.True(Directory.Exists(copy));                       // left alone
    }

    [Fact]
    public void Conflict_window_shows_both_copies_and_returns_the_choice()
    {
        var info = new InterfaceSync.Core.Sync.ConflictInfo
        {
            Reason = "Both this PC and LAPTOP changed _retail_ since the last sync.",
            LocalNewestUtc = DateTime.UtcNow.AddHours(-2), CloudNewestUtc = DateTime.UtcNow.AddMinutes(-5),
            LocalMachine = "HOME-PC", CloudPushedBy = "LAPTOP",   // the cloud copy is newer, so it is suggested
        };
        OnSta(() =>
        {
            using var d = new ConflictDialog("_retail_", info);
            d.Show();
            Application.DoEvents();
            var keepCloud = AllControls(d).OfType<ModernButton>().Single(b => b.Text == "Keep the cloud settings");
            Assert.Equal(ButtonKind.Primary, keepCloud.Kind);                       // the newer one is the main action
            Assert.Contains(AllControls(d).OfType<Label>(), l => l.Text.Contains("This PC (HOME-PC)"));
            keepCloud.PerformClick();
            Assert.Equal(InterfaceSync.Core.Sync.KeepChoice.Cloud, d.Choice);
        });
    }

    private static IEnumerable<Control> AllControls(Control c) =>
        c.Controls.Cast<Control>().SelectMany(x => AllControls(x).Prepend(x));

    [Fact]
    public void Every_tray_state_has_its_own_embedded_icon_and_the_app_icon_has_all_sizes()
    {
        OnSta(() =>
        {
            using var icons = new TrayIcons();
            var seen = new HashSet<string>();
            foreach (var state in Enum.GetValues<OverallState>())
            {
                using var bmp = icons.For(state).ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                Assert.True(seen.Add(Convert.ToBase64String(ms.ToArray())), $"{state} looks the same as another state");
            }
            Assert.NotNull(TrayIcons.AppIcon);
        });

        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("app.ico")!;
        var header = new byte[6];
        stream.ReadExactly(header);
        Assert.Equal(8, BitConverter.ToUInt16(header, 4));   // 16, 20, 24, 32, 40, 48, 64, 256
    }
}
