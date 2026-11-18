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
    public void First_run_offers_all_three_cloud_apps_and_switching_updates_the_folder()
    {
        var service = Service(setupComplete: false, ("_forever_", true));
        Directory.CreateDirectory(Path.Combine(_root, "Users", "Player", "Dropbox"));
        OnSta(() =>
        {
            using var form = new FirstRun(service, new FakeRegistry());
            form.Show();
            Application.DoEvents();
            Assert.Equal(["Google Drive", "OneDrive (not found on this PC)", "Dropbox"], form.ProviderChoices);

            form.ChooseProvider(InterfaceSync.Core.Detection.CloudProvider.Dropbox);
            Application.DoEvents();
            Assert.EndsWith("Dropbox", form.CloudFolderText);
            Assert.True(form.CanStart);

            form.ChooseProvider(InterfaceSync.Core.Detection.CloudProvider.OneDrive);
            Application.DoEvents();
            Assert.StartsWith("Not found", form.CloudFolderText);
            Assert.False(form.CanStart);
            form.Close();
        });
        Assert.Equal(InterfaceSync.Core.Detection.CloudProvider.OneDrive, service.Settings.CloudProvider);
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
            form.AllowClose();
            form.Close();
        });
    }

    [Fact]
    public void Tray_icons_can_be_drawn()
    {
        OnSta(() =>
        {
            using var icons = new TrayIcons();
            foreach (var state in Enum.GetValues<OverallState>())
                Assert.NotNull(icons.For(state));
        });
    }
}
