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
    private readonly Dictionary<AppTheme, NativeMenuItem> _themeItems = [];
    private StatusWindow? _status;
    private Timer? _timer;
    private FileLog? _log;
    private int _busy;

    public bool StartAsRoot { get; set; }
    public SyncService Service { get; private set; } = null!;

    public override void Initialize() => Styles.Add(MacTheme.Create());

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
        MacTheme.Changed += OnThemeChanged;
        MacTheme.Apply(Service.Settings.Theme);
        Service.Changed += () => Dispatcher.UIThread.Post(RefreshTray);
        Service.NoticeRaised += notice => Dispatcher.UIThread.Post(() => ShowNotice(notice));

        BuildTray(desktop);
        Run(Service.Start, () => AfterStart(desktop));
    }

    private void BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        _headline = new NativeMenuItem($"{ProductInfo.Name}: starting") { IsEnabled = false };
        var open = new NativeMenuItem("Open status...");
        open.Click += (_, _) => ShowStatus();
        var sync = new NativeMenuItem("Sync now");
        sync.Click += (_, _) => Run(Service.SyncNow);
        _pause = new NativeMenuItem("Pause syncing") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _pause.Click += (_, _) => Run(() => Service.SetPaused(!Service.Settings.Paused));
        var appearance = new NativeMenuItem("Appearance") { Menu = new NativeMenu() };
        foreach (var (theme, label) in ThemeChoices)
        {
            var item = new NativeMenuItem(label) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = theme == Service.Settings.Theme };
            item.Click += (_, _) => ChooseTheme(theme);
            _themeItems[theme] = item;
            appearance.Menu.Items.Add(item);
        }
        var about = new NativeMenuItem($"About {ProductInfo.Name}");
        about.Click += (_, _) => Dialogs.About().Show();
        var quit = new NativeMenuItem($"Quit {ProductInfo.Name}");
        quit.Click += (_, _) => Quit(desktop);
        menu.Items.Add(_headline);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(open);
        menu.Items.Add(sync);
        menu.Items.Add(_pause);
        menu.Items.Add(appearance);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(about);
        menu.Items.Add(quit);

        _tray = new TrayIcon { Icon = MacIcons.For(OverallState.Waiting), ToolTipText = ProductInfo.Name, Menu = menu, IsVisible = true };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void AfterStart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!Service.Settings.SetupComplete)
        {
            var setup = new SetupWindow(this);
            setup.Closed += (_, _) =>
            {
                if (!setup.Started)
                {
                    _log?.Info("First-run setup was cancelled; nothing was synced.");
                    Quit(desktop);
                    return;
                }
                StartPolling();
            };
            setup.Show();
            setup.Activate();
            return;
        }
        StartPolling();
    }

    internal static readonly (AppTheme Theme, string Label)[] ThemeChoices =
        [(AppTheme.System, "Match macOS"), (AppTheme.Light, "Light"), (AppTheme.Dark, "Dark")];

    /// <summary>Applies at once and saves the choice for next time.</summary>
    internal static void ChooseTheme(IMacHost host, AppTheme theme)
    {
        MacTheme.Apply(theme);
        host.Run(() => host.Service.SetTheme(theme));
    }

    private void ChooseTheme(AppTheme theme) => ChooseTheme(this, theme);

    private void OnThemeChanged()
    {
        foreach (var (t, item) in _themeItems) item.IsChecked = t == MacTheme.Mode;
        _status?.Refresh();
    }

    private void StartPolling() => _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, SyncService.PollInterval);

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { Service.Poll(); }
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
        _tray.Icon = MacIcons.For(Service.State);
        _tray.ToolTipText = $"{ProductInfo.Name}: {Service.StateText}";
        if (_headline != null) _headline.Header = $"{ProductInfo.Name}: {Service.StateText}";
        if (_pause != null) _pause.IsChecked = Service.Settings.Paused;
        _status?.Refresh();
    }

    /// <summary>
    /// macOS notifications need a signed, notarised app; until then a notice shows in the menu (first line)
    /// and as the menu-bar icon's warning badge, and a conflict opens the status window.
    /// </summary>
    private void ShowNotice(Notice notice)
    {
        if (_headline != null) _headline.Header = $"{notice.Title}";
        if (notice.Kind == NoticeKind.Conflict) ShowStatus();
    }

    private void ShowStatus()
    {
        if (_status == null) _status = new StatusWindow(this);
        _status.Show();
        _status.Activate();
        _status.Refresh();
    }

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
