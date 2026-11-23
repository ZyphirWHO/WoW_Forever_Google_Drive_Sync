using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core;
using System.Diagnostics;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.App;

/// <summary>What the status window needs from the tray app (lets tests build the window without a tray).</summary>
internal interface IStatusHost
{
    SyncService Service { get; }
    void Run(Action work, Action? afterOnUi = null);
}

/// <summary>
/// The status dashboard: a headline card (state, last sync, Sync now / Pause), a card per game version with its
/// status and any action it needs, settings (cloud folder, start with Windows, appearance), recent activity, and
/// the rare tools (backups, log, diagnostics, about).
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly IStatusHost _app;
    private SyncService Service => _app.Service;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly FlowLayoutPanel _stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(24, 16), Margin = Padding.Empty };

    // headline
    private readonly Card _hero = new() { Height = 104, Padding = new Padding(20, 14, 20, 14) };
    private readonly Glyph _heroIcon = new(Draw.Check, 26f) { Margin = new Padding(0, 6, 14, 0) };
    private readonly Label _overall = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 2) };
    private readonly Label _overallDetail = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
    private readonly ModernButton _syncNow = new("Sync now", ButtonKind.Primary) { Icon = Draw.Sync };
    private readonly ModernButton _pause = new("Pause");

    // versions
    private readonly FlowLayoutPanel _versionRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    private readonly Dictionary<string, VersionRow> _rows = new();

    // settings
    private readonly Label _cloudPath = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
    private readonly ToggleSwitch _startAtLogin = new("Start with Windows") { Visible = false };
    private readonly BrandBanner _banner = new("Your interface settings, on every computer");

    // updates
    private readonly Card _updateBar = new() { Height = 76, Padding = new Padding(18, 12, 16, 12), Visible = false };
    private readonly Label _updateTitle = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _updateDetail = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
    private readonly ModernButton _updateNow = new("Update now", ButtonKind.Primary) { Icon = Draw.Download };
    private readonly ModernButton _whatsNew = new("What's new", ButtonKind.Ghost);
    private readonly ToggleSwitch _autoUpdate = new("Check for updates automatically");
    private readonly Label _updateStatus = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
    private readonly IStartAtLogin? _startup;
    private Control _startIcon = null!;

    // activity
    private readonly Card _activity = new() { Height = 300, Padding = new Padding(16, 10, 16, 14) };
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.None };
    private readonly Label _lastLine = new() { AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ModernButton _toggleActivity = new("Show", ButtonKind.Ghost) { Height = 28 };
    private bool _activityOpen;

    private bool _allowClose;

    /// <summary>The GitHub update checker; the update parts are hidden when not set (tests).</summary>
    internal Updater? Updater { get; init; }

    /// <summary>Downloads and installs the available update (the tray app quits and the installer reopens it).</summary>
    internal Action<IWin32Window?>? InstallUpdate { get; init; }

    /// <summary>Start with Windows; the switch is hidden when not set (tests).</summary>
    internal IStartAtLogin? StartAtLogin
    {
        get => _startup;
        init { _startup = value; _startAtLogin.Visible = _startIcon.Visible = value != null; if (value != null) { _startAtLogin.Text = value.Label; _startAtLogin.Checked = value.IsEnabled; } }
    }

    public StatusForm(IStatusHost app)
    {
        _app = app;
        Text = $"{ProductInfo.Name} {ProductInfo.DisplayVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 760);
        MinimumSize = new Size(680, 520);
        Font = SystemFonts.MessageBoxFont ?? Font;
        Padding = Padding.Empty;
        Icon = TrayIcons.AppIcon;

        _overall.Font = new Font(Font.FontFamily, 15f, FontStyle.Bold);
        Theme.SetRole(_overallDetail, TextRole.Muted);
        _syncNow.Click += (_, _) => _app.Run(Service.SyncNow);
        _pause.Click += (_, _) => _app.Run(() => Service.SetPaused(!Service.Settings.Paused));
        BuildHero();

        BuildUpdateBar();
        _stack.Controls.Add(_updateBar);
        _stack.Controls.Add(_hero);
        _stack.Controls.Add(new SectionHeader("Game versions"));
        _stack.Controls.Add(_versionRows);
        _stack.Controls.Add(new SectionHeader("Settings"));
        _stack.Controls.Add(BuildSettings());
        _stack.Controls.Add(new SectionHeader("Activity"));
        _stack.Controls.Add(BuildActivity());
        _stack.Controls.Add(BuildTools());
        _content.Controls.Add(_stack);
        _content.Resize += (_, _) => FitWidths();

        Controls.Add(_content);
        _banner.EnableThemeToggle(() => TrayApp.ToggleTheme(Service));
        Controls.Add(_banner);   // docks above the content
        Theme.Apply(this);
        Theme.Changed += OnThemeChanged;
        FitWidths();
    }

    private int RowWidth => Math.Max(560, _content.ClientSize.Width - 48);

    /// <summary>Cards fill the window's width (it can be resized).</summary>
    private void FitWidths()
    {
        foreach (Control c in _stack.Controls) c.Width = RowWidth;
        foreach (var row in _rows.Values) row.Width = RowWidth;
        _stack.Controls[^1].Width = RowWidth;
    }

    private void BuildHero()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        grid.Controls.Add(_heroIcon, 0, 0);
        grid.SetRowSpan(_heroIcon, 2);
        _overall.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _overallDetail.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        grid.Controls.Add(_overall, 1, 0);
        grid.Controls.Add(_overallDetail, 1, 1);
        _pause.Anchor = _syncNow.Anchor = AnchorStyles.Right;
        _syncNow.Margin = new Padding(8, 0, 0, 0);
        _pause.Margin = new Padding(8, 0, 0, 0);
        grid.Controls.Add(_pause, 2, 0);
        grid.Controls.Add(_syncNow, 3, 0);
        grid.SetRowSpan(_pause, 2);
        grid.SetRowSpan(_syncNow, 2);
        _hero.Controls.Add(grid);
    }

    private Card BuildSettings()
    {
        var card = new Card { Height = 196, Padding = new Padding(16, 8, 16, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));

        Control Icon(string g) { var l = new Glyph(g, 14f) { Anchor = AnchorStyles.Left }; Theme.SetRole(l, TextRole.Accent); return l; }
        Label Bold(string t) => new() { Text = t, AutoSize = true, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 1) };

        var cloud = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        cloud.Controls.Add(Bold("Cloud folder"));
        Theme.SetRole(_cloudPath, TextRole.Muted);
        cloud.Controls.Add(_cloudPath);
        var change = new ModernButton("Change...") { Height = 30, Anchor = AnchorStyles.Right };
        change.Click += (_, _) => ChangeCloudFolder();
        grid.Controls.Add(Icon(Draw.Cloud), 0, 0);
        grid.Controls.Add(cloud, 1, 0);
        grid.Controls.Add(change, 2, 0);

        _startAtLogin.Anchor = AnchorStyles.Left;
        _startAtLogin.AutoCheck = false;
        _startAtLogin.Click += (_, _) => ToggleStartAtLogin();
        _startIcon = Icon(Draw.Power);
        _startIcon.Visible = false;
        grid.Controls.Add(_startIcon, 0, 1);
        grid.Controls.Add(_startAtLogin, 1, 1);

        // updates: automatic checks on/off, what the last check found, and Check now
        var updates = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        _autoUpdate.AutoCheck = false;
        _autoUpdate.Click += (_, _) =>
        {
            var on = !Service.Settings.CheckForUpdates;
            Service.SavePreference(s => s.CheckForUpdates = on);
            if (on && Updater != null) _ = Updater.CheckAsync();
            RefreshView();
        };
        Theme.SetRole(_updateStatus, TextRole.Muted);
        _updateStatus.Margin = new Padding(50, 0, 0, 0);
        updates.Controls.Add(_autoUpdate);
        updates.Controls.Add(_updateStatus);
        var checkNow = new ModernButton("Check now") { Height = 30, Anchor = AnchorStyles.Right };
        checkNow.Click += (_, _) => { if (Updater != null) _ = Updater.CheckAsync(); };
        _updateIcon = Icon(Draw.Download);
        grid.Controls.Add(_updateIcon, 0, 2);
        grid.Controls.Add(updates, 1, 2);
        grid.Controls.Add(checkNow, 2, 2);
        _updateRow = [_updateIcon, updates, checkNow];
        card.Controls.Add(grid);
        return card;
    }

    private Card BuildActivity()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Theme.SetRole(_lastLine, TextRole.Muted);
        header.Controls.Add(_lastLine, 0, 0);
        _toggleActivity.Anchor = AnchorStyles.Right;
        _toggleActivity.Margin = Padding.Empty;
        header.Controls.Add(_toggleActivity, 1, 0);
        _toggleActivity.Click += (_, _) => SetActivityOpen(!_activityOpen);
        _logBox.Visible = false;
        _activity.Controls.Add(_logBox);
        _activity.Controls.Add(header);   // docked after the log box = above it
        SetActivityOpen(false);
        return _activity;
    }

    private void SetActivityOpen(bool open)
    {
        _activityOpen = open;
        _logBox.Visible = open;
        _activity.Height = open ? 300 : 58;
        _toggleActivity.Text = open ? "Hide" : "Show all";
        _lastLine.Visible = true;
    }

    private Control BuildTools()
    {
        var tools = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 4, 0, 16) };
        ModernButton Tool(string text, string glyph, Action click)
        {
            var b = new ModernButton(text, ButtonKind.Ghost) { Icon = glyph, Height = 32 };
            b.Click += (_, _) => click();
            tools.Controls.Add(b);
            return b;
        }
        Tool("Open backups", Draw.History, () => Open(Service.Paths.Backups));
        Tool("Open log folder", Draw.Folder, () => Open(Service.Paths.Logs));
        Tool("Copy diagnostics", Draw.Info, () =>
        {
            Clipboard.SetText(Service.Diagnostics());
            MessageBox.Show(this, "Diagnostics copied. Paste them into your message or GitHub issue.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        Tool("About", Draw.Info, () => ShowAbout(this));
        return tools;
    }

    /// <summary>The Start with Windows switch (also used by tests).</summary>
    internal void ToggleStartAtLogin()
    {
        if (_startup == null) return;
        TrayApp.ToggleStartAtLogin(_startup, Service.Log, this);
        _startAtLogin.Checked = _startup.IsEnabled;
    }

    private Control _updateIcon = null!;
    private Control[] _updateRow = [];

    private void BuildUpdateBar()
    {
        _updateTitle.Font = new Font(Font, FontStyle.Bold);
        Theme.SetRole(_updateDetail, TextRole.Muted);
        _updateBar.Highlight = Brand.C(ThemePalette.Banner.Gold);
        _updateNow.Click += (_, _) => InstallUpdate?.Invoke(this);
        _whatsNew.Click += (_, _) => { if (Updater?.Available is { } r) TrayApp.OpenPage(r.Page); };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var icon = new Glyph(Draw.Download, 18f) { Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
        Theme.SetRole(icon, TextRole.Accent);
        grid.Controls.Add(icon, 0, 0);
        grid.SetRowSpan(icon, 2);
        _updateTitle.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _updateDetail.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        grid.Controls.Add(_updateTitle, 1, 0);
        grid.Controls.Add(_updateDetail, 1, 1);
        _whatsNew.Anchor = _updateNow.Anchor = AnchorStyles.Right;
        _whatsNew.Margin = new Padding(8, 0, 0, 0);
        _updateNow.Margin = new Padding(8, 0, 0, 0);
        grid.Controls.Add(_whatsNew, 2, 0);
        grid.Controls.Add(_updateNow, 3, 0);
        grid.SetRowSpan(_whatsNew, 2);
        grid.SetRowSpan(_updateNow, 2);
        _updateBar.Controls.Add(grid);
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        RefreshView();
    }

    /// <summary>For tests: what the sun/moon switch does.</summary>
    internal void ChooseAppearance(AppTheme theme) => TrayApp.ChooseTheme(Service, theme);
    internal BrandBanner Banner => _banner;
    internal bool UpdateOffered => _updateBar.Visible;

    /// <summary>For tests: the version cards' titles, and the headline card.</summary>
    internal IReadOnlyList<string> VersionTitles => _versionRows.Controls.OfType<VersionRow>().Select(r => r.Title).ToList();
    internal Card Headline => _hero;

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps InterfaceSync running in the tray; Exit is in the tray menu.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private static Color ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Good => Theme.Good,
        StatusTone.Busy => Theme.Info,
        StatusTone.Attention => Theme.Error,
        StatusTone.Paused => Theme.Muted,
        _ => Theme.Warning,
    };

    private static string ToneGlyph(StatusTone tone) => tone switch
    {
        StatusTone.Good => Draw.Check,
        StatusTone.Busy => Draw.Sync,
        StatusTone.Attention => Draw.Warning,
        StatusTone.Paused => Draw.Pause,
        _ => Draw.Clock,
    };

    public void RefreshView()
    {
        if (IsDisposed || !Visible) return;
        var now = DateTime.UtcNow;
        var tone = StatusWords.Tone(Service.State);

        _overall.Text = StatusWords.Headline(Service.State);
        var last = StatusWords.LastSynced(Service.Workers);
        _overallDetail.Text = Service.State == OverallState.InSync
            ? $"Last synced {StatusWords.Ago(last, now)}"
            : $"{Service.StateText}{(last != null ? $" · last synced {StatusWords.Ago(last, now)}" : "")}";
        _heroIcon.Text = ToneGlyph(tone);
        _heroIcon.ForeColor = ToneColor(tone);
        _hero.Highlight = tone == StatusTone.Attention ? Theme.Error : null;
        _hero.Invalidate();
        _pause.Text = Service.Settings.Paused ? "Resume" : "Pause";

        // updates
        foreach (var c in _updateRow) c.Visible = Updater != null;
        if (Updater != null)
        {
            _autoUpdate.Checked = Service.Settings.CheckForUpdates;
            _updateStatus.Text = Updater.Describe() + (Service.Settings.LastUpdateCheckUtc is { } checkedAt && Updater.State != UpdateState.Checking
                ? $" Last checked {StatusWords.Ago(checkedAt, now)}." : "");
            var release = Updater.Available;
            bool show = release != null && Updater.State is UpdateState.Available or UpdateState.Downloading;
            if (show != _updateBar.Visible) { _updateBar.Visible = show; FitWidths(); }
            if (show)
            {
                _updateTitle.Text = $"{ProductInfo.Name} {release!.Version} is available";
                _updateDetail.Text = Updater.State == UpdateState.Downloading
                    ? "Downloading and checking the installer…"
                    : $"You have {Updater.CurrentVersion}. Updating takes a few seconds and keeps all your settings.";
                _updateNow.Enabled = Updater.State != UpdateState.Downloading;
                _updateNow.Text = release.WindowsInstaller != null ? "Update now" : "Download";
            }
        }
        _pause.Icon = Service.Settings.Paused ? Draw.Sync : Draw.Pause;

        // one card per game version, updated in place
        var wanted = new List<(string key, VersionSync? worker, GameVersion version)>();
        foreach (var w in Service.Workers) wanted.Add((w.Version.FolderName, w, w.Version));
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v))) wanted.Add((v.FolderName, null, v));
        if (!wanted.Select(x => x.key).SequenceEqual(_versionRows.Controls.OfType<VersionRow>().Select(r => r.Key)))
        {
            _versionRows.SuspendLayout();
            _versionRows.Controls.Clear();
            _rows.Clear();
            foreach (var (key, _, _) in wanted)
            {
                var row = new VersionRow(key, Resolve) { Width = RowWidth };
                _rows[key] = row;
                _versionRows.Controls.Add(row);
            }
            Theme.Apply(this);
            _versionRows.ResumeLayout();
        }
        foreach (var (key, worker, version) in wanted) _rows[key].Show(worker, version, now);

        var drive = Service.Drive?.MyDrivePath;
        _cloudPath.Text = drive != null ? $"{Service.ProviderName}: {PathUtil.Join(drive, CloudLayout.AppFolderName)}" : $"{Service.ProviderName}: not found";
        if (_startup != null) _startAtLogin.Checked = _startup.IsEnabled;

        // Newest first, so the latest action is visible without scrolling.
        var lines = Service.RecentLog(200).Reverse().Select(l => l.ToString()).ToList();
        _logBox.Text = string.Join(Environment.NewLine, lines);
        _lastLine.Text = lines.Count > 0 ? lines[0] : "Nothing yet.";
        FitWidths();
    }

    private void Resolve(VersionSync w)
    {
        if (w.State.Conflict is not { } c) return;

        var keepLocal = new TaskDialogButton("Keep this PC's settings");
        var keepCloud = new TaskDialogButton("Keep the cloud settings");
        var page = new TaskDialogPage
        {
            Caption = ProductInfo.Name,
            Heading = $"{w.Version.FolderName}: settings changed in two places",
            Text = $"{c.Reason}\n\nThis PC{(c.LocalMachine != null ? $" ({c.LocalMachine})" : "")}: last changed {Format(c.LocalNewestUtc)}\n" +
                   $"Cloud{(c.CloudPushedBy != null ? $" (from {c.CloudPushedBy})" : "")}: last changed {Format(c.CloudNewestUtc)}\n\n" +
                   "The copy you don't keep is saved as a backup." +
                   (c.CloudBackupPath != null ? $"\nCloud copy saved to: {c.CloudBackupPath}" : ""),
            Icon = TaskDialogIcon.Warning,
            Buttons = { keepLocal, keepCloud, TaskDialogButton.Cancel },
            DefaultButton = c.Suggested == KeepChoice.Cloud ? keepCloud : keepLocal,
        };
        var result = TaskDialog.ShowDialog(this, page);
        if (result == keepLocal) _app.Run(() => Service.Resolve(w, KeepChoice.Local));
        else if (result == keepCloud) _app.Run(() => Service.Resolve(w, KeepChoice.Cloud));
    }

    internal static string AboutText =>
        $"{ProductInfo.Tagline}\n\n{ProductInfo.NotAffiliated}\n\n{ProductInfo.TrademarkNotice}\n\n" +
        $"Version {ProductInfo.DisplayVersion}. {ProductInfo.Licence}.\n\n{ProductInfo.FontCredit}";

    internal static void ShowAbout(IWin32Window? owner)
    {
        var page = new TaskDialogPage
        {
            Caption = $"About {ProductInfo.Name}",
            Heading = $"{ProductInfo.Name} {ProductInfo.DisplayVersion}",
            Text = AboutText,
            Icon = new TaskDialogIcon(TrayIcons.AppIcon),
            Buttons = { TaskDialogButton.OK },
        };
        if (owner != null) TaskDialog.ShowDialog(owner, page);
        else TaskDialog.ShowDialog(page);
    }

    private void ChangeCloudFolder()
    {
        using var dialog = new ChangeCloudFolder(Service);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var target = dialog.Chosen;
        var keep = dialog.KeepOnDevice;
        ProviderChangeResult? result = null;
        _app.Run(
            () => result = Service.ChangeProvider(target, null, keep),
            () => MessageBox.Show(this, result?.Message ?? "Nothing changed.", ProductInfo.Name, MessageBoxButtons.OK,
                result?.Changed == true ? MessageBoxIcon.Information : MessageBoxIcon.Warning));
    }

    private static string Format(DateTime? utc) => utc?.ToLocalTime().ToString("g") ?? "never";

    private void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show(this, $"Couldn't open {folder}: {e.Message}", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>One game version: name and version, status pill, upload/download times, and its action if any.</summary>
    private sealed class VersionRow : Card
    {
        private readonly Label _title = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
        private readonly Label _details = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
        private readonly StatusPill _pill = new() { Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 0, 0) };
        private readonly ModernButton _action = new("Choose which to keep...", ButtonKind.Primary) { Anchor = AnchorStyles.Right, Visible = false, Height = 30, Margin = new Padding(10, 0, 0, 0) };
        private VersionSync? _worker;

        public VersionRow(string key, Action<VersionSync> resolve)
        {
            Key = key;
            Height = 70;
            Padding = new Padding(18, 10, 16, 10);
            Margin = new Padding(0, 0, 0, 8);
            _title.Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold);
            Theme.SetRole(_details, TextRole.Muted);
            _action.Click += (_, _) => { if (_worker != null) resolve(_worker); };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _title.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _details.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            grid.Controls.Add(_title, 0, 0);
            grid.Controls.Add(_details, 0, 1);
            grid.Controls.Add(_pill, 1, 0);
            grid.SetRowSpan(_pill, 2);
            grid.Controls.Add(_action, 2, 0);
            grid.SetRowSpan(_action, 2);
            Controls.Add(grid);
        }

        public string Key { get; }
        public string Title => _title.Text;

        public void Show(VersionSync? worker, GameVersion version, DateTime now)
        {
            _worker = worker;
            _title.Text = version.FolderName + (version.ClientVersion != null ? $"   {version.ClientVersion}" : "");
            if (worker == null)
            {
                _details.Text = version.CanEnable ? "Not synced on this PC. Switch it on in setup to sync it." : "Not played on this PC yet";
                _pill.Set("Off", Theme.Muted);
                _action.Visible = false;
                Highlight = null;
            }
            else
            {
                var tone = StatusWords.Tone(worker.Status);
                var status = string.IsNullOrEmpty(worker.StatusText) ? worker.Status.ToString() : worker.StatusText;
                _pill.Set(status.Length > 48 ? status[..45] + "..." : status, ToneColor(tone));
                var pending = worker.State.PendingPush ? " · upload pending" : "";
                _details.Text = (worker.State.LastError is { Length: > 0 } err && tone == StatusTone.Attention ? err : StatusWords.Details(worker, now)) + pending;
                _action.Visible = worker.State.Conflict != null;
                Highlight = tone == StatusTone.Attention ? Theme.Error : null;
            }
            Invalidate();
        }
    }
}
