using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Updates;

using InterfaceSync.Core.Localization;

namespace InterfaceSync.Mac;

/// <summary>What the windows need from the app: the service and a way to run work off the UI thread.</summary>
internal interface IMacHost
{
    SyncService Service { get; }

    /// <summary>Runs work in the background, one at a time with polling; then <paramref name="afterOnUi"/> on the UI thread.</summary>
    void Run(Action work, Action? afterOnUi = null);
}

/// <summary>The menu-bar app: icon and menu, the 15-second poll, and the setup and status windows.</summary>
internal sealed class MacApp : Application, IMacHost
{
    private TrayIcon? _tray;
    private NativeMenuItem? _headline;
    private NativeMenuItem? _pause;
    private NativeMenuItem? _startupItem;
    private IStartAtLogin? _startup;
    private NativeMenuItem? _darkItem;
    private static readonly HttpClient Http = new();
    private string? _announcedUpdate;
    public Updater Updater { get; private set; } = null!;
    private StatusWindow? _status;
    private Timer? _timer;
    private FileLog? _log;
    private int _busy;

    public bool StartAsRoot { get; set; }
    public SyncService Service { get; private set; } = null!;

    public override void Initialize()
    {
        Styles.Add(MacTheme.Create());
        MacTheme.AddResources(Resources);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        if (StartAsRoot || !OperatingSystem.IsMacOS())
        {
            var w = Dialogs.Message(StartAsRoot
                ? $"{ProductInfo.Name} is running as root, so it would write to the wrong place. Quit it and open it normally."
                : $"This build of {ProductInfo.Name} is for macOS.");
            w.Closed += (_, _) => desktop.Shutdown();
            w.Show();
            return;
        }

        var env = new MacUserEnvironment();
        var fs = new PhysicalFileSystem();
        var clock = new SystemClock();
        var moved = LocalDataMigration.Run(fs, env.LocalAppData);
        var paths = new AppPaths(env.LocalAppData);
        _log = new FileLog(fs, clock, paths.Logs);
        if (moved != null) _log.Info(moved);
        Service = new SyncService(fs, new NoRegistry(), new MacProcessList(), env, clock, _log, paths, Environment.MachineName);
        _startup = new RememberedStartAtLogin(new MacStartAtLogin(fs, env.UserProfile, Environment.ProcessPath ?? "", ProductInfo.BundleId),
            () => Service.Settings.StartAtLogin, on => Service.SavePreference(s => s.StartAtLogin = on), () => Service.Settings.SetupComplete);
        try { if (_startup.Repair() is { } repaired) _log.Info(repaired); }
        catch (Exception e) { _log.Warn($"Couldn't update Open at Login: {e.Message}"); }
        L.Use(Service.Settings.Language);   // before any window or menu text is made
        MacTheme.Changed += OnThemeChanged;
        MacTheme.Apply(Service.Settings.Theme);
        Service.Changed += () => Dispatcher.UIThread.Post(RefreshTray);
        Updater = new Updater(Service, Http, clock);
        Updater.Changed += () => Dispatcher.UIThread.Post(OnUpdateChanged);
        Service.NoticeRaised += notice => Dispatcher.UIThread.Post(() => ShowNotice(notice));

        BuildTray(desktop);
        Run(Service.Start, () => AfterStart(desktop));
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        _headline = new NativeMenuItem($"{ProductInfo.Name}: {L.T("starting")}") { IsEnabled = false };
        var open = new NativeMenuItem(L.T("Open status..."));
        open.Click += (_, _) => ShowStatus();
        var sync = new NativeMenuItem(L.T("Sync now"));
        sync.Click += (_, _) => Run(Service.SyncNow);
        _pause = new NativeMenuItem(L.T("Pause syncing")) { Menu = new NativeMenu() };
        _pause.Menu.Opening += (_, _) => FillPauseMenu();
        FillPauseMenu();
        var settings = new NativeMenuItem(L.T("Settings...")) { Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.OemComma, Avalonia.Input.KeyModifiers.Meta) };
        settings.Click += (_, _) => { ShowStatus(); _status?.OpenSettings(); };
        var help = new NativeMenuItem(L.T("Help")) { Menu = new NativeMenu() };
        var helpPage = new NativeMenuItem(L.T("Help and Questions"));
        helpPage.Click += (_, _) => MacActions.OpenLink(null, Feedback.HelpPage);
        var whatsNew = new NativeMenuItem(L.T("What's New"));
        whatsNew.Click += (_, _) => MacActions.ShowWhatsNew(null);
        var report = new NativeMenuItem(L.T("Report a Problem..."));
        report.Click += (_, _) => MacActions.ReportProblem(null, this);
        help.Menu.Items.Add(helpPage);
        help.Menu.Items.Add(whatsNew);
        help.Menu.Items.Add(report);
        _startupItem = new NativeMenuItem(_startup!.Label) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = _startup.IsEnabled };
        _startupItem.Click += (_, _) => ToggleStartAtLogin();
        menu.Opening += (_, _) => { if (_startupItem != null) _startupItem.IsChecked = _startup.IsEnabled; };
        _darkItem = new NativeMenuItem(L.T("Dark Mode")) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = MacTheme.IsDark };
        _darkItem.Click += (_, _) => ToggleTheme(this);
        var updates = new NativeMenuItem(L.T("Check for Updates..."));
        updates.Click += (_, _) => { ShowStatus(); _status?.CheckForUpdates(); };
        var about = new NativeMenuItem(L.F("About {0}", ProductInfo.Name));
        about.Click += (_, _) => Dialogs.About().Show();
        var quit = new NativeMenuItem(L.F("Quit {0}", ProductInfo.Name));
        quit.Click += (_, _) => Quit(desktop);
        menu.Items.Add(_headline);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(sync);
        menu.Items.Add(_pause);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(_startupItem);
        menu.Items.Add(_darkItem);
        menu.Items.Add(updates);
        menu.Items.Add(help);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(about);
        menu.Items.Add(quit);

        _tray = new TrayIcon { Icon = MacIcons.For(OverallState.Waiting), ToolTipText = ProductInfo.Name, Menu = menu, IsVisible = true };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        _animation.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % MacIcons.SyncingFrames.Count;
            if (_tray != null) _tray.Icon = MacIcons.SyncingFrames[_frame];
        };
    }

    /// <summary>While syncing, the badge's dots light up in turn.</summary>
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _frame;

    private void FillPauseMenu()
    {
        if (_pause?.Menu is not { } m) return;
        m.Items.Clear();
        if (Service?.Settings.Paused == true)
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

    private void AfterStart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!Service.Settings.SetupComplete)
        {
            var setup = new SetupWindow(this, _startup);
            setup.Closed += (_, _) =>
            {
                if (!setup.Started)
                {
                    _log?.Info("First-run setup was cancelled; nothing was synced.");
                    Quit(desktop);
                    return;
                }
                Run(() => Service.TakeWhatsNew());   // a new install: nothing to announce, just remember the version
                StartPolling();
            };
            setup.Show();
            setup.Activate();
            return;
        }
        StartPolling();
    }

    /// <summary>The sun/moon switch: light ⇄ dark.</summary>
    internal static void ToggleTheme(IMacHost host) => ChooseTheme(host, MacTheme.IsDark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>Applies at once and saves the choice for next time.</summary>
    internal static void ChooseTheme(IMacHost host, AppTheme theme)
    {
        MacTheme.Apply(theme);
        host.Run(() => host.Service.SetTheme(theme));
    }

    private void OnThemeChanged()
    {
        if (_darkItem != null) _darkItem.IsChecked = MacTheme.IsDark;
        _status?.Refresh();
    }

    private void OnUpdateChanged()
    {
        _status?.Refresh();
        if (Updater.State == UpdateState.Available && Updater.Available is { } r && _announcedUpdate != r.Version
            && Service.Settings.Notifications != NotificationLevel.Off)
        {
            _announcedUpdate = r.Version;
            if (_headline != null) _headline.Header = L.F("{0} {1} is available", ProductInfo.Name, r.Version);
        }
    }

    private void ToggleStartAtLogin()
    {
        if (_startup == null || _log == null) return;
        try
        {
            if (_startup.IsEnabled) { _startup.Disable(); _log.Info($"{_startup.Label}: off."); }
            else if (_startup.CannotEnableReason is { } why) Dialogs.Message(why).Show();
            else { _startup.Enable(); _log.Info($"{_startup.Label}: on."); }
        }
        catch (Exception e)
        {
            _log.Warn($"Couldn't change {_startup.Label}: {e.Message}");
            Dialogs.Message($"Couldn't change {_startup.Label}: {e.Message}").Show();
        }
        if (_startupItem != null) _startupItem.IsChecked = _startup.IsEnabled;
    }

    private void StartPolling() => _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, SyncService.PollInterval);

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { Service.Poll(); _ = Updater.CheckIfDueAsync(); }
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

    private void RefreshTray()
    {
        if (_tray == null) return;
        bool syncing = Service.State == OverallState.Syncing;
        if (syncing != _animation.IsEnabled) _animation.IsEnabled = syncing;
        if (!syncing) _tray.Icon = MacIcons.For(Service.State);
        _tray.ToolTipText = StatusWords.Tooltip(StatusWords.StateText(Service, DateTime.UtcNow), StatusWords.LastSynced(Service.Workers), DateTime.UtcNow);
        if (_headline != null) _headline.Header = $"{ProductInfo.Name}: {StatusWords.StateText(Service, DateTime.UtcNow)}";
        if (_pause != null) _pause.Header = Service.Settings.Paused ? L.T("Syncing paused") : L.T("Pause syncing");
        FillPauseMenu();
        _status?.Refresh();
    }

    /// <summary>
    /// macOS notifications need a signed, notarised app; until then a notice shows in the menu (first line)
    /// and as the menu-bar icon's warning badge, and a conflict opens the status window.
    /// </summary>
    private void ShowNotice(Notice notice)
    {
        if (!SyncService.ShouldShow(notice.Kind, Service.Settings.Notifications)) return;
        if (_headline != null) _headline.Header = $"{notice.Title}";
        if (notice.Kind == NoticeKind.Conflict) ShowStatus();
    }

    private void ShowStatus()
    {
        if (_status == null) _status = new StatusWindow(this, _startup, Updater);
        _status.Show();
        _status.Activate();
        _status.Refresh();

        // The first time the window opens after an update: what's new in this version (once).
        if (!_whatsNewChecked && Service.Settings.SetupComplete)
        {
            _whatsNewChecked = true;
            string? notes = null;
            Run(() => notes = Service.TakeWhatsNew(), () => { if (notes != null) MacActions.ShowWhatsNew(_status, notes); });
        }
    }

    private bool _whatsNewChecked;

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _timer?.Dispose();
        // Let an in-flight copy finish (at most 30 s) so nothing is left half-done.
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, TimeSpan.FromSeconds(30));
        _log?.Info($"{ProductInfo.Name} stopped.");
        if (_tray != null) _tray.IsVisible = false;
        _status?.AllowClose();
        desktop.Shutdown();
    }
}
