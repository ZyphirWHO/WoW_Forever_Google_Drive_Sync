using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core;
using System.Diagnostics;
using ActivityKind = InterfaceSync.Core.Sync.ActivityKind;
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
/// status and any action it needs, recent activity in plain words, the computers that sync, and the tools
/// (settings, help, what's new, report a problem, about). Everything else is in the settings window.
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
    private readonly ModernButton _pause = new("Pause") { AccessibleDescription = "Pause syncing for a while" };

    // versions
    private readonly FlowLayoutPanel _versionRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    private readonly Dictionary<string, VersionRow> _rows = new();

    private readonly BrandBanner _banner = new("Your interface settings, on every computer");

    // copies of the cloud folder made by the cloud app
    private readonly Card _dupBar = new() { Padding = new Padding(18, 12, 16, 12), Visible = false };
    private readonly Label _dupTitle = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4) };
    private readonly Label _dupDetail = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };

    // updates
    private readonly Card _updateBar = new() { Height = 76, Padding = new Padding(18, 12, 16, 12), Visible = false };
    private readonly Label _updateTitle = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _updateDetail = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
    private readonly ModernButton _updateNow = new("Update now", ButtonKind.Primary) { Icon = Draw.Download };
    private readonly ModernButton _whatsNew = new("What's new", ButtonKind.Ghost);
    private readonly IStartAtLogin? _startup;

    // activity and computers
    private readonly Card _activity = new() { Padding = new Padding(16, 8, 16, 8) };
    private readonly FlowLayoutPanel _activityRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Location = new Point(16, 8) };
    private readonly Card _computers = new() { Padding = new Padding(16, 8, 16, 8) };
    private readonly FlowLayoutPanel _computerRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Location = new Point(16, 8) };
    private string _activityKey = "", _computersKey = "";
    private DateTime _computersReadUtc = DateTime.MinValue;
    private IReadOnlyList<DeviceInfo> _computerList = [];
    public const int ActivityShown = 6;

    private bool _allowClose;

    /// <summary>The GitHub update checker; the update parts are hidden when not set (tests).</summary>
    internal Updater? Updater { get; init; }

    /// <summary>Downloads and installs the available update (the tray app quits and the installer reopens it).</summary>
    internal Action<IWin32Window?>? InstallUpdate { get; init; }

    /// <summary>Start with Windows, for the settings window (its switch is hidden when not set: tests).</summary>
    internal IStartAtLogin? StartAtLogin { get => _startup; init => _startup = value; }

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
        _pause.Click += (_, _) => AppActions.ShowPauseMenu(_pause, _app);
        BuildHero();

        BuildUpdateBar();
        BuildDuplicateBar();
        _stack.Controls.Add(_updateBar);
        _stack.Controls.Add(_dupBar);
        _stack.Controls.Add(_hero);
        _stack.Controls.Add(new SectionHeader("Game versions"));
        _stack.Controls.Add(_versionRows);
        _stack.Controls.Add(new SectionHeader("Activity"));
        _activity.Controls.Add(_activityRows);
        _stack.Controls.Add(_activity);
        _stack.Controls.Add(new SectionHeader("Your computers"));
        _computers.Controls.Add(_computerRows);
        _stack.Controls.Add(_computers);
        _stack.Controls.Add(BuildTools());
        _content.Controls.Add(_stack);
        _content.Resize += (_, _) => FitWidths();

        Controls.Add(_content);
        _banner.EnableThemeToggle(() => TrayApp.ToggleTheme(Service));
        Controls.Add(_banner);   // docks above the content
        Theme.Apply(this);
        Theme.Changed += OnThemeChanged;
        Ui.ScaleWithDisplay(this);
        FitWidths();
    }

    private int RowWidth => Math.Max(Ui.Px(this, 560), _content.ClientSize.Width - Ui.Px(this, 48));

    /// <summary>Cards fill the window's width (it can be resized); the activity and computer cards fit their rows.</summary>
    private void FitWidths()
    {
        foreach (Control c in _stack.Controls) c.Width = RowWidth;
        foreach (var row in _rows.Values) row.Width = RowWidth;
        _stack.Controls[^1].Width = RowWidth;
        foreach (var (card, rows) in new[] { (_activity, _activityRows), (_computers, _computerRows) })
        {
            int inner = RowWidth - card.Padding.Horizontal;
            foreach (Control c in rows.Controls) if (c is InfoRow r) r.Fit(inner);
            card.Height = rows.PreferredSize.Height + card.Padding.Vertical;
        }
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
        Tool("Settings", Draw.Settings, () => OpenSettings());
        Tool("Help", Draw.Info, () => Ui.OpenLink(Feedback.HelpPage));
        Tool("What's new", Draw.Download, () => AppActions.ShowWhatsNew(this));
        Tool("Report a problem", Draw.Warning, () => AppActions.ReportProblem(this, _app));
        Tool("About", Draw.Info, () => ShowAbout(this));
        return tools;
    }

    /// <summary>Opens the settings window (on a page), and refreshes this one when it closes.</summary>
    internal void OpenSettings(string page = "General")
    {
        using var settings = new SettingsForm(_app, _startup, Updater, CheckForUpdates, page);
        settings.ShowDialog(this);
        RefreshView();
    }

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

    private void BuildDuplicateBar()
    {
        _dupTitle.Font = new Font(Font, FontStyle.Bold);
        Theme.SetRole(_dupDetail, TextRole.Muted);
        _dupBar.Highlight = Theme.Warning;
        var show = new ModernButton("Show folder", ButtonKind.Secondary) { Icon = Draw.Folder, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(8, 0, 0, 0) };
        show.Click += (_, _) => { if (Service.Duplicates.FirstOrDefault() is { } d) Open(d.Path); };
        var hide = new ModernButton("Hide", ButtonKind.Ghost) { Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(8, 0, 0, 0) };
        hide.Click += (_, _) => { foreach (var d in Service.Duplicates.ToList()) Service.HideDuplicate(d); RefreshView(); };
        var icon = new Glyph(Draw.Warning, 18f) { Margin = new Padding(0, 0, 12, 0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
        Theme.SetRole(icon, TextRole.Warning);
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        text.Controls.Add(_dupTitle);
        text.Controls.Add(_dupDetail);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.Controls.Add(icon, 0, 0);
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(hide, 2, 0);
        grid.Controls.Add(show, 3, 0);
        _dupBar.Controls.Add(grid);
    }

    private bool _revealUpdate;

    /// <summary>
    /// "Check now" / "Check for updates": checks, and when it's done brings the top of the window into view, where
    /// the update card appears (it's easy to miss when the window is scrolled down to Settings).
    /// </summary>
    internal void CheckForUpdates()
    {
        if (Updater == null) return;
        _revealUpdate = true;
        _ = Updater.CheckAsync();
    }

    private void ScrollToTop() => _content.AutoScrollPosition = Point.Empty;

    /// <summary>For tests: how far down the window is scrolled, and scrolling to the bottom.</summary>
    internal int ScrollY => -_content.AutoScrollPosition.Y;
    internal void ScrollToBottom() => _content.AutoScrollPosition = new Point(0, _content.DisplayRectangle.Height);

    /// <summary>After an update: a short confirmation at the top of the window.</summary>
    internal void ShowUpdated()
    {
        _updatedNote = true;
        ScrollToTop();
        RefreshView();
    }
    private bool _updatedNote;

    /// <summary>For tests: whether the duplicate-folder card is showing.</summary>
    internal bool DuplicateShown => _dupBar.Visible;

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

        // "Updated to …" after an in-app update
        if (_updatedNote)   // stays for this session, so it's still there after the next refresh
        {
            _overallDetail.Text = $"Updated to version {ProductInfo.DisplayVersion} · {_overallDetail.Text}";
        }

        // copies of the cloud folder
        var dups = Service.Duplicates;
        if ((dups.Count > 0) != _dupBar.Visible) { _dupBar.Visible = dups.Count > 0; FitWidths(); }
        if (dups.Count > 0)
        {
            _dupTitle.Text = dups.Count == 1
                ? $"{Service.ProviderName} made a copy of an {ProductInfo.Name} folder"
                : $"{Service.ProviderName} made {dups.Count} copies of {ProductInfo.Name} folders";
            var dupLines = dups.Take(3).Select(d => $"• \"{d.Name}\" ({d.FileCount} file{(d.FileCount == 1 ? "" : "s")}, last changed {StatusWords.Ago(d.NewestUtc, now)})");
            _dupDetail.Text = string.Join("\n", dupLines) + (dups.Count > 3 ? $"\n• and {dups.Count - 3} more (see the log)" : "") +
                $"\nThis happens when two computers save at the same moment. {ProductInfo.Name} only uses \"{dups[0].CopyOf}\" and hasn't moved or " +
                $"deleted anything. Look inside, and delete the copy in {Service.ProviderName} when you're sure you don't need it.";
            _dupDetail.MaximumSize = new Size(Math.Max(300, RowWidth - 330), 0);
            _dupBar.Height = Math.Max(96, _dupTitle.PreferredHeight + _dupDetail.GetPreferredSize(new Size(_dupDetail.MaximumSize.Width, 0)).Height + 30);
        }

        // updates
        if (Updater != null)
        {
            var release = Updater.Available;
            bool show = release != null && Updater.State is UpdateState.Available or UpdateState.Downloading;
            bool appeared = show && !_updateBar.Visible;
            if (show != _updateBar.Visible) { _updateBar.Visible = show; FitWidths(); }
            if (appeared || (_revealUpdate && Updater.State != UpdateState.Checking))
            {
                _revealUpdate = false;
                ScrollToTop();
            }
            if (show)
            {
                _updateTitle.Text = release!.IsPrerelease ? $"Test build {release.Version} is available" : $"{ProductInfo.Name} {release.Version} is available";
                _updateDetail.Text = Updater.State == UpdateState.Downloading
                    ? "Downloading and checking the installer…"
                    : $"You have {Updater.CurrentVersion}. Updating takes a few seconds and keeps all your settings.";
                _updateNow.Enabled = Updater.State != UpdateState.Downloading;
                _updateNow.Text = release.WindowsInstaller != null ? "Update now" : "Download";
            }
        }
        _pause.Icon = Service.Settings.Paused ? Draw.Sync : Draw.Pause;
        _pause.Text = Service.Settings.Paused ? "Resume" : "Pause…";

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
                var row = Ui.Fit(new VersionRow(key, Resolve, OpenRestore) { Width = RowWidth }, this);
                _rows[key] = row;
                _versionRows.Controls.Add(row);
            }
            Theme.Apply(this);
            _versionRows.ResumeLayout();
        }
        foreach (var (key, worker, version) in wanted) _rows[key].Show(worker, version, now, Service.RulesFor(key).Describe());

        RefreshActivity(now);
        RefreshComputers(now);
        FitWidths();
    }

    // ------------------------------------------------------------------ activity and computers

    /// <summary>For tests: the activity lines shown, and the computers listed.</summary>
    internal IReadOnlyList<string> ActivityLines => _activityRows.Controls.OfType<InfoRow>().Select(r => r.Headline).ToList();
    internal IReadOnlyList<string> ComputerLines => _computerRows.Controls.OfType<InfoRow>().Select(r => r.Headline).ToList();

    private void RefreshActivity(DateTime now)
    {
        var entries = Service.Activity.List(ActivityShown);
        var key = string.Join("|", entries.Select(e => e.Utc.Ticks)) + StatusWords.Ago(entries.FirstOrDefault()?.Utc, now) + Theme.IsDark;
        if (key == _activityKey) return;
        _activityKey = key;
        _activityRows.SuspendLayout();
        foreach (Control c in _activityRows.Controls.Cast<Control>().ToList()) { _activityRows.Controls.Remove(c); c.Dispose(); }
        if (entries.Count == 0)
            _activityRows.Controls.Add(Ui.Fit(new InfoRow(Draw.Clock, "Nothing synced yet", "Uploads, downloads and restores will appear here.", TextRole.Muted), this));
        foreach (var e in entries)
        {
            var entry = e;
            var (glyph, role) = ActivityGlyph(e.Kind);
            var detail = $"{e.Version} · {StatusWords.Ago(e.Utc, now)}{(e.FileCount > 0 ? $" · {e.FileCount} file{(e.FileCount == 1 ? "" : "s")}" : "")}";
            var row = Ui.Fit(new InfoRow(glyph, e.Headline, detail, role), this);
            if (e.Files.Count > 0) row.Activated += () => ActivityDialog.ShowDetails(this, entry);
            _activityRows.Controls.Add(row);
        }
        var all = new ModernButton("Show all activity", ButtonKind.Ghost) { Height = 30, Icon = Draw.History, Margin = new Padding(0, 4, 0, 4) };
        all.Click += (_, _) => { using var d = new ActivityDialog(_app); d.ShowDialog(this); };
        _activityRows.Controls.Add(Ui.Fit(all, this));
        Theme.Apply(this);
        _activityRows.ResumeLayout();
    }

    internal static (string Glyph, TextRole Role) ActivityGlyph(ActivityKind kind) => kind switch
    {
        ActivityKind.Uploaded => (Draw.Upload, TextRole.Accent),
        ActivityKind.Downloaded => (Draw.Download, TextRole.Good),
        ActivityKind.Conflict => (Draw.Warning, TextRole.Warning),
        ActivityKind.Restored or ActivityKind.Snapshot or ActivityKind.GameUpdated => (Draw.History, TextRole.Accent),
        _ => (Draw.Sync, TextRole.Accent),
    };

    private void RefreshComputers(DateTime now)
    {
        if (now - _computersReadUtc > TimeSpan.FromSeconds(60))
        {
            _computersReadUtc = now;
            try { _computerList = Service.Computers(); } catch (Exception) { }
        }
        var key = string.Join("|", _computerList.Select(c => c.Name + c.LastSyncUtc.Ticks)) + StatusWords.Ago(_computerList.FirstOrDefault()?.LastSyncUtc, now) + Theme.IsDark;
        if (key == _computersKey) return;
        _computersKey = key;
        _computerRows.SuspendLayout();
        foreach (Control c in _computerRows.Controls.Cast<Control>().ToList()) { _computerRows.Controls.Remove(c); c.Dispose(); }
        if (_computerList.Count == 0)
            _computerRows.Controls.Add(Ui.Fit(new InfoRow(Draw.Computer, "No computers yet",
                $"Each computer appears here after it syncs with {ProductInfo.Name} 3.1 or later.", TextRole.Muted), this));
        foreach (var c in _computerList)
        {
            var name = c.IsThisComputer ? $"{c.Name} (this computer)" : c.Name;
            var action = c.LastAction.Length > 0 ? $"{c.LastAction.ToLowerInvariant()} {StatusWords.Ago(c.LastSyncUtc, now)}" : $"synced {StatusWords.Ago(c.LastSyncUtc, now)}";
            var detail = string.Join(" · ", new[] { c.System, c.AppVersion.Length > 0 ? $"version {c.AppVersion}" : "", action, string.Join(", ", c.Versions) }.Where(x => x.Length > 0));
            _computerRows.Controls.Add(Ui.Fit(new InfoRow(Draw.Computer, name, detail, c.IsThisComputer ? TextRole.Accent : TextRole.Muted), this));
        }
        Theme.Apply(this);
        _computerRows.ResumeLayout();
    }

    private void OpenRestore(VersionSync w)
    {
        AppActions.OpenBackups(this, _app, w);
        RefreshView();
    }

    private void Resolve(VersionSync w)
    {
        if (w.State.Conflict is not { } c) return;

        if (ConflictDialog.Ask(this, w.Version.FolderName, c) is { } keep) _app.Run(() => Service.Resolve(w, keep));
    }

    internal static string AboutText =>
        $"{ProductInfo.Tagline}\n\n{ProductInfo.NotAffiliated}\n\n{ProductInfo.TrademarkNotice}\n\n" +
        $"Version {ProductInfo.DisplayVersion}. {ProductInfo.Licence}.\n\n{ProductInfo.FontCredit}";

    internal static void ShowAbout(IWin32Window? owner) => AboutDialog.ShowFor(owner);

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
            ThemedDialog.Warn(this, $"Couldn't open {folder}: {e.Message}");
        }
    }

    /// <summary>One game version: name and version, status pill, upload/download times, and its action if any.</summary>
    private sealed class VersionRow : Card
    {
        private readonly Label _title = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
        private readonly Label _details = new() { AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
        private readonly StatusPill _pill = new() { Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 0, 0) };
        private readonly ModernButton _action = new("Choose which to keep...", ButtonKind.Primary) { Anchor = AnchorStyles.Right, Visible = false, Height = 30, Margin = new Padding(10, 0, 0, 0) };
        private readonly ModernButton _restore = new("Backups…", ButtonKind.Ghost) { Anchor = AnchorStyles.Right, Visible = false, Height = 30, Margin = new Padding(10, 0, 0, 0), Icon = Draw.History, AccessibleDescription = "Restore a backup, or save a snapshot" };
        private VersionSync? _worker;

        public VersionRow(string key, Action<VersionSync> resolve, Action<VersionSync> restore)
        {
            Key = key;
            Height = 70;
            Padding = new Padding(18, 10, 16, 10);
            Margin = new Padding(0, 0, 0, 8);
            _title.Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold);
            Theme.SetRole(_details, TextRole.Muted);
            _action.Click += (_, _) => { if (_worker != null) resolve(_worker); };
            _restore.Click += (_, _) => { if (_worker != null) restore(_worker); };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
            grid.Controls.Add(_restore, 3, 0);
            grid.SetRowSpan(_restore, 2);
            Controls.Add(grid);
        }

        public string Key { get; }
        public string Title => _title.Text;

        public void Show(VersionSync? worker, GameVersion version, DateTime now, string? rules = null)
        {
            _worker = worker;
            _title.Text = version.FolderName + (version.ClientVersion != null ? $"   {version.ClientVersion}" : "");
            if (worker == null)
            {
                _details.Text = version.CanEnable ? "Not synced on this PC. Switch it on in setup to sync it." : "Not played on this PC yet";
                _pill.Set("Off", Theme.Muted);
                _action.Visible = false;
                _restore.Visible = false;
                Highlight = null;
            }
            else
            {
                var tone = StatusWords.Tone(worker.Status);
                var status = string.IsNullOrEmpty(worker.StatusText) ? worker.Status.ToString() : worker.StatusText;
                _pill.Set(status.Length > 48 ? status[..45] + "..." : status, ToneColor(tone));
                var pending = worker.State.PendingPush ? " · upload pending" : "";
                _details.Text = (worker.State.LastError is { Length: > 0 } err && tone == StatusTone.Attention ? err : StatusWords.Details(worker, now)) + pending +
                                (rules != null ? $" · {rules}" : "");
                AccessibleName = $"{_title.Text}: {status}. {_details.Text}";
                _action.Visible = worker.State.Conflict != null;
                _restore.Visible = true;
                Highlight = tone == StatusTone.Attention ? Theme.Error : null;
            }
            Invalidate();
        }
    }
}
