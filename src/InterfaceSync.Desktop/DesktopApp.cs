using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.Desktop;

/// <summary>
/// The app on Windows and macOS: the icon by the clock (click: the panel; Windows right-click: the menu), the
/// 15-second poll, the main window, setup, notifications and updates. All sync work runs off the UI thread.
/// </summary>
internal sealed class DesktopApp : Application, IAppHost
{
    private TrayIcon? _tray;
    private TrayPanel? _panel;
    private MainWindow? _main;
    private NativeMenuItem? _pauseItem, _darkItem;
    private IStartAtLogin? _startup;
    private static readonly HttpClient Http = new();
    private string? _announcedUpdate;
    private Timer? _timer;
    private FileLog? _log;
    private int _busy;
    private readonly Notifier _notifier = new();
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _frame;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _whatsNewChecked;
    private EventWaitHandle? _showEvent;

    public bool StartAsRoot { get; set; }
    public bool StartInTray { get; set; }
    public bool JustUpdated { get; set; }
    public SyncService Service { get; private set; } = null!;
    public Updater? Updater { get; private set; }
    public IStartAtLogin? StartAtLogin => _startup;

    public override void Initialize()
    {
        Styles.Add(Look.Create());
        Look.AddResources(Resources);
        // Windows 11's own font for the interface (Segoe UI on Windows 10); the system font on a Mac.
        if (OperatingSystem.IsWindows())
            Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI")) } });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;

        if (StartAsRoot)
        {
            var w = Dialogs.Message($"{ProductInfo.Name} is running as root, so it would write to the wrong place. Quit it and open it normally.");
            w.Closed += (_, _) => desktop.Shutdown();
            w.Show();
            return;
        }

        var env = Os.Environment();
        var fs = new PhysicalFileSystem();
        var clock = new SystemClock();
        var registry = Os.Registry();
        var moved = LocalDataMigration.Run(fs, env.LocalAppData);   // before anything opens the data folder
        var paths = new AppPaths(env.LocalAppData);
        _log = new FileLog(fs, clock, paths.Logs);
        if (moved != null) _log.Info(moved);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _log.Error($"Unexpected error: {e.ExceptionObject}");
        Dispatcher.UIThread.UnhandledException += (_, e) => { _log.Error($"Unexpected error in a window: {e.Exception}"); e.Handled = true; };
        Service = new SyncService(fs, registry, Os.Processes(), env, clock, _log, paths, Environment.MachineName);
        L.Use(Service.Settings.Language);   // before any window or menu text is made
        _startup = new RememberedStartAtLogin(Os.StartAtLogin(env, fs, registry),
            () => Service.Settings.StartAtLogin, on => Service.SavePreference(s => s.StartAtLogin = on), () => Service.Settings.SetupComplete);
        Look.Changed += OnThemeChanged;
        Look.Apply(Service.Settings.Theme);
        Service.Changed += () => Dispatcher.UIThread.Post(RefreshAll);
        Updater = new Updater(Service, Http, clock);
        Updater.Changed += () => Dispatcher.UIThread.Post(OnUpdateChanged);
        Service.NoticeRaised += notice => Dispatcher.UIThread.Post(() => ShowNotice(notice));
        _notifier.Activated += action => Dispatcher.UIThread.Post(() => OnNoticeClicked(action));

        BuildTray();
        ListenForShowRequests();
        Run(Service.Start, AfterStart);
    }

    // ------------------------------------------------------------------ tray

    private void BuildTray()
    {
        _tray = new TrayIcon { Icon = AppIcons.For(OverallState.Waiting), ToolTipText = $"{ProductInfo.Name}: {L.T("starting")}", IsVisible = true };
        // Click: the panel. On Windows a right-click also opens the classic menu; a Mac's menu-bar icon shows the panel.
        _tray.Clicked += (_, _) => TogglePanel();
        if (Os.IsWindows) _tray.Menu = BuildMenu();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        _animation.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % AppIcons.SyncingFrames.Count;
            if (_tray != null) _tray.Icon = AppIcons.SyncingFrames[_frame];
        };
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        NativeMenuItem Item(string text, Action click)
        {
            var i = new NativeMenuItem(text);
            i.Click += (_, _) => click();
            return i;
        }
        _pauseItem = new NativeMenuItem(L.T("Pause syncing")) { Menu = new NativeMenu() };
        _pauseItem.Menu.Opening += (_, _) => FillPauseMenu();
        FillPauseMenu();
        _darkItem = new NativeMenuItem(L.T("Dark mode")) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Look.IsDark };
        _darkItem.Click += (_, _) => ToggleTheme(this);
        menu.Items.Add(Item(L.F("Open {0}", ProductInfo.Name), () => ShowMain()));
        menu.Items.Add(Item(L.T("Sync now"), () => Run(Service.SyncNow)));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("Settings…"), () => ShowMain("Settings")));
        menu.Items.Add(_darkItem);
        menu.Items.Add(Item(L.T("Check for updates"), () => { ShowMain("Settings"); CheckForUpdates(); }));
        menu.Items.Add(Item(L.T("Help and questions"), () => Actions.OpenLink(null, Feedback.HelpPage)));
        menu.Items.Add(Item(L.T("About"), () => Dialogs.About().Show()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("Exit"), Quit));
        return menu;
    }

    private void FillPauseMenu()
    {
        if (_pauseItem?.Menu is not { } m || Service == null) return;
        m.Items.Clear();
        if (Service.Settings.Paused)
        {
            var resume = new NativeMenuItem(Service.Settings.PausedUntilUtc is { } u ? L.F("Resume syncing (paused until {0})", StatusWords.Until(u, DateTime.UtcNow)) : L.T("Resume syncing"));
            resume.Click += (_, _) => Run(() => Service.SetPaused(false));
            m.Items.Add(resume);
            return;
        }
        foreach (var (label, untilUtc) in StatusWords.PauseChoices(DateTime.UtcNow))
        {
            var item = new NativeMenuItem(label);
            item.Click += (_, _) => Run(() => Service.SetPaused(true, untilUtc));
            m.Items.Add(item);
        }
    }

    private void TogglePanel()
    {
        if (Service == null) return;
        _panel ??= new TrayPanel(this);
        if (_panel.IsVisible) { _panel.Hide(); return; }
        _panel.ShowNearClock();
    }

    // ------------------------------------------------------------------ start-up

    private void AfterStart()
    {
        if (StartInTray) _log?.Info("Started at sign-in.");
        try { if (_startup!.Repair() is { } repaired) _log?.Info(repaired); }
        catch (Exception e) { _log?.Warn($"Couldn't update the start-at-login entry: {e.Message}"); }
        _log?.Info($"{_startup!.Label}: {(_startup.IsEnabled ? "on" : _startup.SwitchedOffNote != null ? "switched off in the system's startup list" : "off")}.");

        if (!Service.Settings.SetupComplete)
        {
            var setup = new SetupWindow(this, _startup);
            setup.Closed += (_, _) =>
            {
                if (!setup.Started)
                {
                    _log?.Info("First-run setup was cancelled; nothing was synced.");
                    Quit();
                    return;
                }
                Run(() => Service.TakeWhatsNew());   // a new install: nothing to announce, just remember the version
                StartPolling();
                ShowMain();
                _notifier.Show(L.F("{0} is running", ProductInfo.Name),
                    Os.IsWindows ? L.T("It syncs on its own when you close the game. Click its icon by the clock to see its status.")
                                 : L.T("It syncs on its own when you quit the game. Click its icon in the menu bar to see its status."));
            };
            setup.Show();
            setup.Activate();
            return;
        }

        if (!StartInTray || JustUpdated) ShowMain();
        if (JustUpdated)
        {
            _main?.ShowUpdated();
            if (Service.Settings.Notifications != NotificationLevel.Off)
                _notifier.Show(L.F("{0} was updated", ProductInfo.Name), L.F("You now have version {0}.", ProductInfo.DisplayVersion));
        }
        else if (StartInTray && !Service.Settings.LoginStartNoticeShown)
        {
            // Started at sign-in with no window: say once where it is (Windows tucks new tray icons away).
            Service.SavePreference(s => s.LoginStartNoticeShown = true);
            _notifier.Show(Os.IsWindows ? L.F("{0} started with Windows", ProductInfo.Name) : L.F("{0} is running", ProductInfo.Name),
                Os.IsWindows ? L.T("It runs in the tray by the clock. If you don't see its icon, click the ^ arrow there; you can drag the icon out to keep it in view.")
                             : L.T("It syncs on its own when you quit the game. Click its icon in the menu bar to see its status."));
        }
        StartPolling();
    }

    private void StartPolling() => _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, SyncService.PollInterval);

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { Service.Poll(); _ = Updater?.CheckIfDueAsync(); }
        catch (Exception e) { _log?.Error($"Unexpected error while syncing: {e}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Run(Action work, Action? afterOnUi = null)
    {
        Task.Run(() =>
        {
            SpinWait.SpinUntil(() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0);
            try { work(); }
            catch (Exception e) { _log?.Error($"Unexpected error: {e}"); }
            finally { Interlocked.Exchange(ref _busy, 0); }
            if (afterOnUi != null) Dispatcher.UIThread.Post(afterOnUi);
        });
    }

    /// <summary>Windows: a second launch (Start menu, a notification) asks this copy to show its window.</summary>
    private void ListenForShowRequests()
    {
        if (!Os.IsWindows) return;
        try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Os.ShowEventName); }
        catch (Exception) { return; }
        new Thread(() =>
        {
            while (true)
            {
                try { if (_showEvent.WaitOne()) Dispatcher.UIThread.Post(() => ShowMain()); }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "show-window listener" }.Start();
    }

    // ------------------------------------------------------------------ windows

    public void ShowMain(string? page = null)
    {
        if (Service == null) return;
        _main ??= new MainWindow(this);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        if (page != null) _main.Navigate(page);
        _main.Refresh();

        // The first time the window opens after an update: what's new in this version (once).
        if (!_whatsNewChecked && Service.Settings.SetupComplete)
        {
            _whatsNewChecked = true;
            string? notes = null;
            var main = _main;
            Run(() => notes = Service.TakeWhatsNew(), () => { if (notes != null) Actions.ShowWhatsNew(main, notes); });
        }
    }

    private void RefreshAll()
    {
        if (_tray != null)
        {
            bool syncing = Service.State == OverallState.Syncing;
            if (syncing != _animation.IsEnabled) _animation.IsEnabled = syncing;
            if (!syncing) _tray.Icon = AppIcons.For(Service.State);
            _tray.ToolTipText = StatusWords.Tooltip(StatusWords.StateText(Service, DateTime.UtcNow), StatusWords.LastSynced(Service.Workers), DateTime.UtcNow);
        }
        if (_pauseItem != null) _pauseItem.Header = Service.Settings.Paused ? L.T("Syncing paused") : L.T("Pause syncing");
        if (_panel?.IsVisible == true) _panel.Refresh();
        _main?.Refresh();
    }

    /// <summary>The sun/moon switch: light ⇄ dark.</summary>
    internal static void ToggleTheme(IAppHost host) => ChooseTheme(host, Look.IsDark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>Applies at once and saves the choice for next time.</summary>
    internal static void ChooseTheme(IAppHost host, AppTheme theme)
    {
        Look.Apply(theme);
        host.Run(() => host.Service.SetTheme(theme));
    }

    private void OnThemeChanged()
    {
        if (_darkItem != null) _darkItem.IsChecked = Look.IsDark;
    }

    // ------------------------------------------------------------------ notifications

    /// <summary>
    /// Windows: a notification (with "Choose which to keep" for a conflict). A Mac can't show notifications from an
    /// unsigned app, so a conflict opens the window instead; other notices show in the panel.
    /// </summary>
    private void ShowNotice(Notice notice)
    {
        if (!SyncService.ShouldShow(notice.Kind, Service.Settings.Notifications)) return;
        (string, NoticeAction)? button = notice.Kind switch
        {
            NoticeKind.Conflict => (L.T("Choose which to keep…"), NoticeAction.ChooseWhichToKeep),
            NoticeKind.Downloaded => (L.T("See activity"), NoticeAction.ShowActivity),
            _ => null,
        };
        bool shown = _notifier.Show(notice.Title, notice.Message, NoticeAction.Open, button);
        if (!shown && notice.Kind == NoticeKind.Conflict) ShowMain();
    }

    private void OnNoticeClicked(NoticeAction action)
    {
        switch (action)
        {
            case NoticeAction.ChooseWhichToKeep:
                ShowMain();
                if (Service.Workers.FirstOrDefault(w => w.State.Conflict != null) is { } w) Actions.Resolve(_main, this, w);
                break;
            case NoticeAction.UpdateNow: InstallUpdate(); break;
            case NoticeAction.ShowActivity: ShowMain("Activity"); break;
            default: ShowMain(); break;
        }
    }

    // ------------------------------------------------------------------ updates

    public void CheckForUpdates()
    {
        if (Updater != null) _ = Updater.CheckAsync();
    }

    private void OnUpdateChanged()
    {
        _main?.Refresh();
        if (_panel?.IsVisible == true) _panel.Refresh();
        if (Updater?.State == UpdateState.Available && Updater.Available is { } r && _announcedUpdate != r.Version
            && Service.Settings.Notifications != NotificationLevel.Off)
        {
            _announcedUpdate = r.Version;
            _notifier.Show(r.IsPrerelease ? L.F("Beta {0} is available", r.Version) : L.F("{0} {1} is available", ProductInfo.Name, r.Version),
                L.T("Your settings are kept when you update."), NoticeAction.Open,
                (Os.IsWindows ? L.T("Update now") : L.T("Download update"), NoticeAction.UpdateNow));
        }
    }

    /// <summary>
    /// Windows: downloads the installer (SHA-256 checked), then quits and lets it update this copy; the installer
    /// opens Interface Sync again. A Mac: opens this Mac's disk image in the browser (an unsigned app can't replace itself).
    /// </summary>
    public void InstallUpdate()
    {
        if (Updater?.Available is not { } release) return;
        // Wherever it was clicked (panel, notification, a page), show the update's progress in Settings → Updates.
        ShowMain();
        _main?.Navigate("Settings", "Updates");
        if (!Os.IsWindows)
        {
            var dmg = release.MacImage(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64);
            Actions.OpenLink(_main, dmg?.Url ?? release.Page);
            Dialogs.Message(L.F("Your browser is downloading {0} {1}. When it's done: quit {0} from the menu bar, open the downloaded disk image and drag the app onto Applications (choose Replace). Your settings are kept.", ProductInfo.Name, release.Version)).Show();
            return;
        }
        if (release.WindowsInstaller is not { } asset) { Actions.OpenLink(_main, release.Page); return; }
        Task.Run(async () =>
        {
            try
            {
                var path = await Updater.DownloadAsync(asset, Path.Combine(Service.Paths.Data, "Updates"));
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!Updater.IsThisProduct(info.ProductName))
                    throw new InvalidDataException($"The downloaded installer says it is \"{info.ProductName?.Trim('\0', ' ')}\", not {ProductInfo.Name}.");
                Dispatcher.UIThread.Post(() =>
                {
                    _log?.Info($"Installing {asset.Name}; {ProductInfo.Name} will reopen when it's done.");
                    Program.PendingInstaller = path;
                    Quit();
                });
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                _log?.Warn($"Update failed: {e.Message}");
                Dispatcher.UIThread.Post(() => Dialogs.Message($"{e.Message}\n\n{L.T("Nothing was changed. You can download it from the project's Releases page instead.")}",
                    L.T("The update couldn't be installed"), warning: true).Show());
            }
        });
    }

    // ------------------------------------------------------------------ quit

    public void Quit()
    {
        _timer?.Dispose();
        // Let an in-flight copy finish (at most 30 s) so nothing is left half-done.
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, TimeSpan.FromSeconds(30));
        _log?.Info($"{ProductInfo.Name} stopped.");
        if (_tray != null) _tray.IsVisible = false;
        _panel?.Close();
        _main?.AllowClose();
        _showEvent?.Dispose();
        _desktop?.Shutdown();
    }
}
