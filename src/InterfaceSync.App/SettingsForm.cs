using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.App;

/// <summary>
/// Every setting in one place, one page per topic: General, Sync, Cloud, Backups, Updates, Advanced. Pages are listed
/// on the left; each setting is a card with a short explanation.
/// </summary>
internal sealed class SettingsForm : Form
{
    public static readonly string[] PageNames = ["General", "Sync", "Cloud", "Backups", "Updates", "Advanced"];
    private static readonly string[] PageGlyphs = [Draw.Settings, Draw.Sync, Draw.Cloud, Draw.History, Draw.Download, Draw.Info];

    private readonly IStatusHost _app;
    private readonly IStartAtLogin? _startup;
    private readonly Updater? _updater;
    private readonly Action? _checkForUpdates;
    private readonly List<NavItem> _nav = [];
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 8, 24, 16) };
    private readonly FlowLayoutPanel _page = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Location = new Point(24, 12) };
    private readonly BrandBanner _banner = new("Settings");
    private string _current = PageNames[0];

    public SettingsForm(IStatusHost app, IStartAtLogin? startup = null, Updater? updater = null, Action? checkForUpdates = null, string page = "General")
    {
        _app = app;
        _startup = startup;
        _updater = updater;
        _checkForUpdates = checkForUpdates;
        Text = $"{ProductInfo.Name}: settings";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 660);
        MinimumSize = new Size(720, 480);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;
        KeyPreview = true;

        var side = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 208, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 16, 8, 16), AccessibleName = "Settings pages", AccessibleRole = AccessibleRole.PageTabList };
        for (int i = 0; i < PageNames.Length; i++)
        {
            var name = PageNames[i];
            var item = new NavItem(name, PageGlyphs[i]) { AccessibleName = name };
            item.Click += (_, _) => ShowPage(name);
            _nav.Add(item);
            side.Controls.Add(item);
        }
        var divider = new Panel { Dock = DockStyle.Left, Width = 1 };
        divider.Paint += (_, e) => e.Graphics.Clear(Theme.Border);
        _content.Controls.Add(_page);
        _content.Resize += (_, _) => FitWidths();
        Controls.Add(_content);
        Controls.Add(divider);
        Controls.Add(side);
        _banner.EnableThemeToggle(() => { TrayApp.ToggleTheme(_app.Service); BeginInvoke(() => ShowPage(_current)); });
        Controls.Add(_banner);
        if (_updater != null) _updater.Changed += OnUpdaterChanged;
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
        ShowPage(PageNames.Contains(page) ? page : "General");
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab move between pages, like tabs.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            int i = Array.IndexOf(PageNames, _current);
            i = (i + ((keyData & Keys.Shift) != 0 ? PageNames.Length - 1 : 1)) % PageNames.Length;
            ShowPage(PageNames[i]);
            _nav[i].Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnUpdaterChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { if (!IsDisposed && _current == "Updates") ShowPage("Updates"); });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _updater != null) _updater.Changed -= OnUpdaterChanged;
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ pages

    internal string CurrentPage => _current;

    /// <summary>For tests: the setting titles on the current page.</summary>
    internal IReadOnlyList<string> RowTitles => _page.Controls.OfType<SettingRow>().Select(r => r.Title).ToList();

    internal void ShowPage(string name)
    {
        _current = name;
        foreach (var n in _nav) n.Current = n.Text == name;
        _page.SuspendLayout();
        foreach (Control c in _page.Controls.Cast<Control>().ToList()) { _page.Controls.Remove(c); c.Dispose(); }
        _page.Controls.Add(Ui.Fit(new SectionHeader(name), this));
        foreach (var row in name switch
                 {
                     "General" => General(),
                     "Sync" => SyncPage(),
                     "Cloud" => CloudPage(),
                     "Backups" => BackupsPage(),
                     "Updates" => UpdatesPage(),
                     _ => AdvancedPage(),
                 })
            _page.Controls.Add(Ui.Fit(row, this));
        Theme.Apply(this);
        _page.ResumeLayout();
        _content.AutoScrollPosition = Point.Empty;
        FitWidths();
    }

    private void FitWidths()
    {
        int width = Math.Max(Ui.Px(this, 420), _content.ClientSize.Width - Ui.Px(this, 48));
        foreach (Control c in _page.Controls)
        {
            if (c is SettingRow row) row.Fit(width);
            else c.Width = width;
        }
    }

    private static ModernButton Button(string text, Action click, ButtonKind kind = ButtonKind.Secondary, string? icon = null)
    {
        var b = new ModernButton(text, kind) { Height = 32, Margin = new Padding(8, 0, 0, 0), Icon = icon };
        b.Click += (_, _) => click();
        return b;
    }

    private IEnumerable<Control> General()
    {
        var theme = new Segmented("Appearance", "Dark", "Light") { Selected = Theme.IsDark ? 0 : 1 };
        theme.Chosen += i => { TrayApp.ChooseTheme(_app.Service, i == 0 ? AppTheme.Dark : AppTheme.Light); BeginInvoke(() => ShowPage("General")); };
        yield return new SettingRow("Appearance", "Dark is easy on the eyes at night; light suits a bright room. The sun/moon switch at the top of every window does the same.", Draw.Paint, theme);

        if (_startup != null)
        {
            var toggle = new ToggleSwitch(_startup.Label) { AutoCheck = false, Checked = _startup.IsEnabled, AccessibleName = _startup.Label };
            toggle.Click += (_, _) => { ToggleStartAtLogin(); };
            _startToggle = toggle;
            yield return new SettingRow(_startup.Label,
                "Starts quietly in the tray when you sign in, so your settings are always up to date before you play.", Draw.Power, toggle);
        }

        var level = new Segmented("Notifications", "All", "Only when needed", "None") { Selected = (int)_app.Service.Settings.Notifications };
        level.Chosen += i => _app.Service.SavePreference(s => s.Notifications = (NotificationLevel)i);
        yield return new SettingRow("Notifications",
            "\"Only when needed\": settings changed in two places, a sync problem, or an update. \"All\" also tells you when settings arrive from another computer. The tray icon always shows the state.",
            Draw.Info, level);
    }

    private ToggleSwitch? _startToggle;

    /// <summary>The start-at-login switch (also used by tests).</summary>
    internal void ToggleStartAtLogin()
    {
        if (_startup == null) return;
        TrayApp.ToggleStartAtLogin(_startup, _app.Service.Log, this);
        if (_startToggle != null) _startToggle.Checked = _startup.IsEnabled;
    }

    private IEnumerable<Control> SyncPage()
    {
        var s = _app.Service.Settings;
        var pauseText = s.Paused
            ? s.PausedUntilUtc is { } until ? $"Paused until {StatusWords.Until(until, DateTime.UtcNow)}. Nothing is uploaded or downloaded until then." : "Paused until you resume it."
            : "Syncing is on. Pause it for a while if you want to try something out without it reaching your other computers.";
        ModernButton pause = null!;
        pause = Button(s.Paused ? "Resume" : "Pause…", () => { AppActions.ShowPauseMenu(pause, _app); }, s.Paused ? ButtonKind.Primary : ButtonKind.Secondary, s.Paused ? Draw.Sync : Draw.Pause);
        yield return new SettingRow("Pause syncing", pauseText, Draw.Pause, pause);

        foreach (var v in _app.Service.Versions)
        {
            var worker = _app.Service.Workers.FirstOrDefault(w => PathUtil.SamePath(w.Version.FolderPath, v.FolderPath));
            bool on = _app.Service.IsEnabled(v);
            var toggle = new ToggleSwitch("Sync") { AutoCheck = false, Checked = on, Enabled = v.CanEnable, AccessibleName = $"Sync {v.FolderName}" };
            var version = v;
            toggle.Click += (_, _) => SetVersionEnabled(version, !_app.Service.IsEnabled(version));
            var controls = new List<Control>();
            if (worker != null) controls.Add(Button("Choose what syncs…", () => { AppActions.ChooseWhatSyncs(this, _app, worker); ShowPage("Sync"); }));
            controls.Add(toggle);
            var rules = _app.Service.RulesFor(v.FolderName).Describe();
            var description = !v.CanEnable ? "Not played on this computer yet. Launch it once to sync it."
                : !on ? "Not synced on this computer."
                : rules ?? "Everything syncs: keybindings, macros, chat and addon settings of every character.";
            yield return new SettingRow(v.FolderName + (v.ClientVersion != null ? $"   {v.ClientVersion}" : ""), description, Draw.Game, [.. controls]);
        }

        yield return new SettingRow("The one rule",
            "Quit the game on one computer before playing on another. Settings are synced only while the game is closed, so two computers playing at once can't both win; you'd be asked which to keep.",
            Draw.Shield);
    }

    private void SetVersionEnabled(GameVersion v, bool on)
    {
        _app.Run(() =>
        {
            var settings = _app.Service.Settings;
            settings.EnabledVersions[v.FolderName] = on;
            _app.Service.SaveSettings(settings);
            _app.Service.Log.Info($"{v.FolderName}: syncing switched {(on ? "on" : "off")} on this computer.");
        }, () => { if (!IsDisposed) ShowPage("Sync"); });
    }

    private IEnumerable<Control> CloudPage()
    {
        var service = _app.Service;
        var drive = service.Drive?.MyDrivePath;
        var folder = drive != null ? PathUtil.Join(drive, CloudLayout.AppFolderName) : null;
        var buttons = new List<Control>();
        if (folder != null) buttons.Add(Button("Open", () => Ui.OpenFolder(this, folder), ButtonKind.Ghost, Draw.Folder));
        buttons.Add(Button("Change…", () => { AppActions.ChangeCloudFolder(this, _app); ShowPage("Cloud"); }));
        yield return new SettingRow($"{service.ProviderName}",
            folder != null ? $"Your settings are kept in {folder}. Only this folder is ever written to." : $"{service.ProviderName} wasn't found. Is its app running and signed in?",
            Draw.Cloud, [.. buttons]);
        var provider = service.Drive?.Provider ?? service.EffectiveProvider();
        yield return new SettingRow("Keep it on this computer",
            $"Your cloud app must keep the {CloudLayout.AppFolderName} folder on this computer, not online only. {provider.KeepOfflineHint(OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Windows)}",
            Draw.Computer);
    }

    private IEnumerable<Control> BackupsPage()
    {
        var service = _app.Service;
        long bytes = 0;
        try { bytes = FolderScanner.AllFiles(new PhysicalFileSystem(), service.Paths.Backups).Sum(f => f.Size); } catch (Exception) { }
        yield return new SettingRow("How backups work",
            $"Before anything on this computer or in the cloud is replaced, the files are copied to a backup first. The newest {Core.Backup.BackupStore.KeepPerVersion} are kept per game version, " +
            $"plus your snapshots (kept until you delete them) and a copy made before each of the last {Core.Backup.BackupStore.KeepGameUpdates} game updates. Backups use {FormatSize(bytes)}.",
            Draw.History, Button("Open folder", () => Ui.OpenFolder(this, service.Paths.Backups), ButtonKind.Ghost, Draw.Folder));
        foreach (var w in service.Workers)
        {
            var worker = w;
            var list = service.Backups.List(w.Version.FolderName);
            int snaps = list.Count(b => b.Pinned);
            yield return new SettingRow(w.Version.FolderName,
                $"{list.Count(b => !b.Pinned && !b.GameUpdate)} backup{(list.Count(b => !b.Pinned && !b.GameUpdate) == 1 ? "" : "s")}, {snaps} snapshot{(snaps == 1 ? "" : "s")}, " +
                $"{list.Count(b => b.GameUpdate)} from game updates.",
                Draw.Game, Button("Backups and snapshots…", () => { AppActions.OpenBackups(this, _app, worker); ShowPage("Backups"); }));
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private IEnumerable<Control> UpdatesPage()
    {
        var service = _app.Service;
        if (_updater != null)
        {
            var auto = new ToggleSwitch("Automatically") { AutoCheck = false, Checked = service.Settings.CheckForUpdates, AccessibleName = "Check for updates automatically" };
            auto.Click += (_, _) =>
            {
                var on = !service.Settings.CheckForUpdates;
                service.SavePreference(s => s.CheckForUpdates = on);
                if (on) _ = _updater.CheckAsync();
                ShowPage("Updates");
            };
            var status = _updater.Describe() + (service.Settings.LastUpdateCheckUtc is { } at && _updater.State != UpdateState.Checking
                ? $" Last checked {StatusWords.Ago(at, DateTime.UtcNow)}." : "");
            yield return new SettingRow("Check for updates",
                $"{status} Checks GitHub once a day; updating takes a few seconds and keeps all your settings.",
                Draw.Download, auto, Button("Check now", () => { (_checkForUpdates ?? (() => _ = _updater.CheckAsync()))(); }));
        }
        yield return new SettingRow($"Version {ProductInfo.DisplayVersion}",
            ProductInfo.DisplayVersion.Contains("-dev", StringComparison.OrdinalIgnoreCase)
                ? "This is a test build: it updates to newer test builds and releases."
                : "See what changed in this version.",
            Draw.Info, Button("What's new", () => AppActions.ShowWhatsNew(this)), Button("All releases", () => Ui.OpenLink(Feedback.ReleasesPage), ButtonKind.Ghost));
    }

    private IEnumerable<Control> AdvancedPage()
    {
        var service = _app.Service;
        yield return new SettingRow("Help and questions", "How syncing works, what to do when settings changed in two places, online-only files and more.",
            Draw.Info, Button("Open help", () => Ui.OpenLink(Feedback.HelpPage)));
        yield return new SettingRow("Report a problem",
            "Opens a GitHub issue with the diagnostics filled in (folder names only). You check it before anything is sent.",
            Draw.Warning, Button("Report a problem…", () => AppActions.ReportProblem(this, _app)));
        yield return new SettingRow("Log", $"Everything {ProductInfo.Name} did, in plain words. Kept for {Core.Logging.FileLog.KeepDays} days.",
            Draw.History, Button("View log", () => AppActions.ViewLog(this, _app)), Button("Open folder", () => Ui.OpenFolder(this, service.Paths.Logs), ButtonKind.Ghost, Draw.Folder));
        yield return new SettingRow("Diagnostics", "Paths, versions and the recent log, to paste into a message when asking for help.",
            Draw.Info, Button("Copy diagnostics", () => AppActions.CopyDiagnostics(this, _app)));
        yield return new SettingRow("Data folder", $"Settings, sync state and backups: {service.Paths.Data}",
            Draw.Folder, Button("Open", () => Ui.OpenFolder(this, service.Paths.Data), ButtonKind.Ghost, Draw.Folder));
    }
}
