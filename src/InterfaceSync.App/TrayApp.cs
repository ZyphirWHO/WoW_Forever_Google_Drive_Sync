using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;

namespace InterfaceSync.App;

/// <summary>The tray icon and its menu. All sync work runs on a background timer, never on the UI thread.</summary>
internal sealed class TrayApp : ApplicationContext, IStatusHost
{
    private readonly Control _ui = new();
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly FileLog _log;
    private readonly IRegistry _registry = new WindowsRegistry();
    private readonly EventWaitHandle _showEvent;
    private readonly bool _startInTray;
    private System.Threading.Timer? _timer;
    private StatusForm? _status;
    private int _busy;
    private volatile bool _exiting;

    public TrayApp(WindowsUserEnvironment env, bool startInTray)
    {
        _startInTray = startInTray;
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
        Service.NoticeRaised += notice => OnUi(() => ShowNotice(notice));

        _pauseItem = new ToolStripMenuItem("Pause syncing", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open status", null, (_, _) => ShowStatus()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Sync now", null, (_, _) => RunInBackground(Service.SyncNow)));
        menu.Items.Add(_pauseItem);
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
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        _tray.BalloonTipClicked += (_, _) => ShowStatus();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        new Thread(WaitForShowRequests) { IsBackground = true, Name = "show-status listener" }.Start();

        RunInBackground(Service.Start, afterOnUi: AfterStart);
    }

    public SyncService Service { get; }

    // ------------------------------------------------------------------ start-up

    private void AfterStart()
    {
        if (!Service.Settings.SetupComplete)
        {
            if (!FirstRun.RunSetup(Service, _registry))
            {
                _log.Info("First-run setup was cancelled; nothing was synced.");
                Exit();
                return;
            }
            _tray.ShowBalloonTip(8_000, $"{ProductInfo.Name} is running",
                "It syncs on its own when you close the game. Click the W icon by the clock to see its status.", ToolTipIcon.Info);
        }
        else if (!_startInTray)
        {
            ShowStatus();
        }

        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, SyncService.PollInterval);
    }

    private void Poll()
    {
        if (_exiting || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { Service.Poll(); }
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
            _status = new StatusForm(this);
        _status.Show();
        if (_status.WindowState == FormWindowState.Minimized) _status.WindowState = FormWindowState.Normal;
        _status.Activate();
        _status.RefreshView();
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
        MessageBox.Show(
            $"{ProductInfo.Name} hit an unexpected problem: {e.Message}\n\nIt has been written to the log. " +
            "Open the status window and use Copy diagnostics to send the details.",
            ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _timer?.Dispose();
            _tray.Dispose();
            _icons.Dispose();
            _showEvent.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
