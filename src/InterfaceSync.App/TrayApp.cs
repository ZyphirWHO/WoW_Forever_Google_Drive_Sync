using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Updates;
using Microsoft.Win32;
using System.Diagnostics;

namespace InterfaceSync.App;

/// <summary>The tray icon and its menu. All sync work runs on a background timer, never on the UI thread.</summary>
internal sealed class TrayApp : ApplicationContext, IStatusHost
{
    private readonly Control _ui = new();
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ContextMenuStrip _menu;
    private readonly IStartAtLogin _startup;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _darkItem;
    private static readonly HttpClient Http = new();
    private string? _announcedUpdate;
    private readonly FileLog _log;
    private readonly IRegistry _registry = new WindowsRegistry();
    private readonly EventWaitHandle _showEvent;
    private readonly bool _startInTray;
    private System.Threading.Timer? _timer;
    private StatusForm? _status;
    private int _busy;
    private volatile bool _exiting;

    private readonly bool _justUpdated;

    public TrayApp(WindowsUserEnvironment env, bool startInTray, bool justUpdated = false)
    {
        _startInTray = startInTray;
        _justUpdated = justUpdated;
        _ = _ui.Handle; // force the window handle now: background events are marshalled through it

        var fs = new PhysicalFileSystem();
        var clock = new SystemClock();
        var moved = LocalDataMigration.Run(fs, env.LocalAppData); // before anything opens the data folder
        var paths = new AppPaths(env.LocalAppData);
        _log = new FileLog(fs, clock, paths.Logs);
        if (moved != null) _log.Info(moved);
        Application.ThreadException += (_, e) => OnUnexpected(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _log.Error($"Unexpected error: {e.ExceptionObject}");
        Service = new SyncService(fs, _registry, new WindowsProcessList(), env, clock, _log, paths, Environment.MachineName);
        Service.Changed += () => OnUi(RefreshTray);
        Updater = new Updater(Service, Http, clock);
        Updater.Changed += () => OnUi(OnUpdateChanged);
        Service.NoticeRaised += notice => OnUi(() => ShowNotice(notice));

        _pauseItem = new ToolStripMenuItem("Pause syncing", null, (_, _) => TogglePause());
        _startup = new WindowsStartAtLogin(_registry, Environment.ProcessPath ?? Application.ExecutablePath, Path.GetTempPath());
        _startupItem = new ToolStripMenuItem(_startup.Label, null, (_, _) => ToggleStartAtLogin(_startup, _log, null));
        var menu = _menu = new ContextMenuStrip();
        menu.Opening += (_, _) => _startupItem.Checked = _startup.IsEnabled;
        _darkItem = new ToolStripMenuItem("Dark mode", null, (_, _) => ToggleTheme(Service));
        menu.Items.Add(new ToolStripMenuItem("Open status", null, (_, _) => ShowStatus()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Sync now", null, (_, _) => RunInBackground(Service.SyncNow)));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(_darkItem);
        menu.Items.Add(new ToolStripMenuItem("Check for updates", null, (_, _) => { ShowStatus(); _status?.CheckForUpdates(); }));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => StatusForm.ShowAbout(null)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        _tray = new NotifyIcon
        {
            Icon = _icons.For(OverallState.Waiting),
            Text = $"{ProductInfo.Name}: starting",
            ContextMenuStrip = menu,
            Visible = true,
        };
        Theme.Changed += ApplyThemeToMenu;
        Theme.Set(Service.Settings.Theme);
        SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        _tray.BalloonTipClicked += (_, _) => ShowStatus();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        new Thread(WaitForShowRequests) { IsBackground = true, Name = "show-status listener" }.Start();

        RunInBackground(Service.Start, afterOnUi: AfterStart);
    }

    public SyncService Service { get; }
    public Updater Updater { get; }

    // ------------------------------------------------------------------ start-up

    private void AfterStart()
    {
        try { if (_startup.Repair() is { } repaired) _log.Info(repaired); }
        catch (Exception e) { _log.Warn($"Couldn't update the start-with-Windows entry: {e.Message}"); }

        if (!Service.Settings.SetupComplete)
        {
            if (!FirstRun.RunSetup(Service, _registry, _startup))
            {
                _log.Info("First-run setup was cancelled; nothing was synced.");
                Exit();
                return;
            }
            _tray.ShowBalloonTip(8_000, $"{ProductInfo.Name} is running",
                "It syncs on its own when you close the game. Click the W icon by the clock to see its status.", ToolTipIcon.Info);
        }
        else if (!_startInTray || _justUpdated)
        {
            ShowStatus();   // after an in-app update, reopen where the player left off: the status window
        }

        if (_justUpdated)
        {
            _status?.ShowUpdated();
            _tray.ShowBalloonTip(6_000, $"{ProductInfo.Name} was updated", $"You now have version {ProductInfo.DisplayVersion}.", ToolTipIcon.Info);
        }
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, SyncService.PollInterval);
    }

    private void Poll()
    {
        if (_exiting || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { Service.Poll(); _ = Updater.CheckIfDueAsync(); }
        catch (Exception e) { _log.Error($"Unexpected error while syncing: {e}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Runs work off the UI thread, one at a time with polling.</summary>
    private void RunInBackground(Action work, Action? afterOnUi = null)
    {
        Task.Run(() =>
        {
            SpinWait.SpinUntil(() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0);
            try { work(); }
            catch (Exception e) { _log.Error($"Unexpected error: {e}"); }
            finally { Interlocked.Exchange(ref _busy, 0); }
            if (afterOnUi != null) OnUi(afterOnUi);
        });
    }

    // ------------------------------------------------------------------ UI

    private void RefreshTray()
    {
        _tray.Icon = _icons.For(Service.State);
        var text = $"{ProductInfo.Name}: {Service.StateText}";
        _tray.Text = text.Length > 127 ? text[..124] + "..." : text;
        _pauseItem.Checked = Service.Settings.Paused;
        _status?.RefreshView();
    }

    private void ShowNotice(Notice notice)
    {
        var icon = notice.Kind == NoticeKind.Conflict ? ToolTipIcon.Warning : ToolTipIcon.Error;
        _tray.ShowBalloonTip(10_000, notice.Title, notice.Message, icon);
    }

    private void ShowStatus()
    {
        if (_status == null || _status.IsDisposed)
            _status = new StatusForm(this) { StartAtLogin = _startup, Updater = Updater, InstallUpdate = InstallUpdate };
        _status.Show();
        if (_status.WindowState == FormWindowState.Minimized) _status.WindowState = FormWindowState.Normal;
        _status.Activate();
        _status.RefreshView();
    }

    /// <summary>The sun/moon switch: light ⇄ dark.</summary>
    internal static void ToggleTheme(SyncService service) => ChooseTheme(service, Theme.IsDark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>Applies at once and saves the choice for next time.</summary>
    internal static void ChooseTheme(SyncService service, AppTheme theme)
    {
        Theme.Set(theme);
        Task.Run(() => service.SetTheme(theme));
    }

    private void ChooseTheme(AppTheme theme) => ChooseTheme(Service, theme);

    private void ApplyThemeToMenu()
    {
        _darkItem.Checked = Theme.IsDark;
        Theme.Apply(_menu);
    }

    /// <summary>Follows Windows' light/dark switch while "Match Windows" is chosen.</summary>
    private void OnWindowsPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && Theme.Mode == AppTheme.System)
            OnUi(() => { if (Theme.IsDark != Theme.WindowsAppsUseDark()) Theme.Refresh(); });
    }

    /// <summary>Switches start-with-Windows on or off, explaining when it can't be switched on from here.</summary>
    internal static void ToggleStartAtLogin(IStartAtLogin startup, ILog log, IWin32Window? owner)
    {
        try
        {
            if (startup.IsEnabled)
            {
                startup.Disable();
                log.Info($"{startup.Label}: off.");
            }
            else if (startup.CannotEnableReason is { } why)
            {
                ThemedDialog.Info(owner, why, $"{startup.Label} can't be switched on from here");
            }
            else
            {
                startup.Enable();
                log.Info($"{startup.Label}: on.");
            }
        }
        catch (Exception e)
        {
            log.Warn($"Couldn't change {startup.Label}: {e.Message}");
            ThemedDialog.Warn(owner, e.Message, $"Couldn't change {startup.Label}");
        }
    }

    // ------------------------------------------------------------------ updates

    private void OnUpdateChanged()
    {
        _status?.RefreshView();
        if (Updater.State == UpdateState.Available && Updater.Available is { } r && _announcedUpdate != r.Version)
        {
            _announcedUpdate = r.Version;
            _tray.ShowBalloonTip(10_000, r.IsPrerelease ? $"Test build {r.Version} is available" : $"{ProductInfo.Name} {r.Version} is available",
                "Open Interface Sync and click Update now. Your settings are kept.", ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// Downloads the installer (SHA-256 checked), then quits and lets it update this copy; the installer opens
    /// Interface Sync again when it's done. Anything mid-sync finishes first (Exit waits for it).
    /// </summary>
    private void InstallUpdate(IWin32Window? owner)
    {
        if (Updater.Available?.WindowsInstaller is not { } asset)
        {
            if (Updater.Available is { } release) OpenPage(release.Page);
            return;
        }
        Task.Run(async () =>
        {
            try
            {
                var path = await Updater.DownloadAsync(asset, Path.Combine(Service.Paths.Data, "Updates"));
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!Updater.IsThisProduct(info.ProductName))
                    throw new InvalidDataException($"The downloaded installer says it is \"{info.ProductName?.Trim('\0', ' ')}\", not {ProductInfo.Name}.");
                OnUi(() =>
                {
                    _log.Info($"Installing {asset.Name}; {ProductInfo.Name} will reopen when it's done.");
                    Program.PendingInstaller = path;
                    Exit();
                });
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                _log.Warn($"Update failed: {e.Message}");
                OnUi(() => ThemedDialog.Warn(owner, $"{e.Message}\n\nNothing was changed. You can download it from the project's Releases page instead.", "The update couldn't be installed"));
            }
        });
    }

    internal static void OpenPage(Uri page)
    {
        try { Process.Start(new ProcessStartInfo(page.ToString()) { UseShellExecute = true }); } catch (Exception) { }
    }

    private void TogglePause() => RunInBackground(() => Service.SetPaused(!Service.Settings.Paused));

    public void Run(Action work, Action? afterOnUi = null) => RunInBackground(work, afterOnUi);

    internal void OnUi(Action action)
    {
        if (_exiting || _ui.IsDisposed) return;
        if (_ui.InvokeRequired) _ui.BeginInvoke(action);
        else action();
    }

    private void WaitForShowRequests()
    {
        while (!_exiting)
        {
            try
            {
                if (_showEvent.WaitOne(TimeSpan.FromSeconds(1)) && !_exiting) OnUi(ShowStatus);
            }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>A UI error must never leave a .NET crash dialog: log it, tell the player plainly, keep running.</summary>
    private void OnUnexpected(Exception e)
    {
        _log.Error($"Unexpected error in the window: {e}");
        ThemedDialog.Show(null,
            $"{e.Message}\n\nIt has been written to the log. Open the status window and use Copy diagnostics to send the details.",
            $"{ProductInfo.Name} hit an unexpected problem", DialogKind.Error);
    }

    // ------------------------------------------------------------------ exit

    internal void Exit()
    {
        if (_exiting) return;
        _timer?.Dispose();
        // Let an in-flight copy finish (at most 30 s) so nothing is left half-done.
        SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, TimeSpan.FromSeconds(30));
        _exiting = true;
        _log.Info($"{ProductInfo.Name} stopped.");
        _tray.Visible = false;
        _status?.AllowClose();
        _status?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
            Theme.Changed -= ApplyThemeToMenu;
            _timer?.Dispose();
            _tray.Dispose();
            _icons.Dispose();
            _showEvent.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
