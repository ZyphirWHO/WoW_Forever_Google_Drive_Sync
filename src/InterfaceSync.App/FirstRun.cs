using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.App;

/// <summary>
/// First run, as a short wizard: Welcome, Your setup (what was found; change anything), Your settings (only when the
/// cloud already has settings: which copy this PC uses), Finish (start with Windows, appearance), then the first
/// sync with each version's result and a Finish button. No typing needed when detection works.
/// </summary>
internal sealed class FirstRun : Form
{
    private const int PageWidth = 664;   // content width inside the window's side margins

    private readonly SyncService _service;
    private readonly IRegistry _registry;
    private readonly IStartAtLogin? _startup;

    // chrome
    private readonly BrandBanner _banner = new($"Welcome! Let's set up {ProductInfo.Name} on this PC.");
    private readonly StepIndicator _steps = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 6, 28, 12) };
    private readonly ModernButton _back = new("Back");
    private readonly ModernButton _cancel = new("Cancel", ButtonKind.Ghost) { DialogResult = DialogResult.Cancel };
    private readonly ModernButton _next = new("Next", ButtonKind.Primary);
    private readonly ModernButton _finish = new("Finish", ButtonKind.Primary) { Visible = false, Enabled = false };
    private int _page;
    private bool _canStart;

    // pages
    private readonly FlowLayoutPanel _welcomePage = Page();
    private readonly FlowLayoutPanel _setupPage = Page();
    private readonly FlowLayoutPanel _choicesPage = Page();
    private readonly FlowLayoutPanel _finalPage = Page();
    private readonly FlowLayoutPanel _progressPage = Page();

    // "Your setup"
    private readonly Label _wowPath = PathLabel();
    private readonly Label _drivePath = PathLabel();
    private readonly Label _cloudPath = PathLabel();
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, Margin = new Padding(0, 4, 8, 4) };
    private bool _filling;
    private readonly CheckBox _keepOnDevice = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 2), Visible = false };
    private readonly CheckedListBox _versions = new() { CheckOnClick = true, IntegralHeight = false, Width = PageWidth - 34, Height = 60, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 0, 2) };
    private readonly Label _notes = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(2, 0, 0, 8) };

    // "Your settings"
    private readonly Label _choicesIntro = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 0, 0, 12) };
    private readonly FlowLayoutPanel _choicesPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
    private readonly List<(string version, RadioButton useCloud, RadioButton useMine)> _choices = [];

    // "Finish"
    private readonly ToggleSwitch _startAtLogin = new($"Start {ProductInfo.Name} when I sign in to Windows (recommended)");
    private readonly Label _startAtLoginNote = new() { AutoSize = true, MaximumSize = new Size(PageWidth - 40, 0), Margin = new Padding(50, 0, 0, 4), Visible = false };
    private readonly ComboBox _appearance = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Margin = new Padding(0, 2, 0, 2) };
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new Size(PageWidth - 34, 0), Margin = new Padding(0, 2, 0, 2) };

    // first sync
    private readonly Label _progressHeading = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 10) };
    private readonly ListView _results = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = PageWidth, Height = 150 };
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = PageWidth, Height = 6, Margin = new Padding(0, 10, 0, 0) };
    private readonly Label _afterword = new() { AutoSize = true, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 12, 0, 0) };
    private bool _syncStarted;
    private volatile bool _syncing;

    internal static readonly string HowItWorks =
        "• When you quit the game, this PC's interface settings (keybindings, macros, addon settings) are copied to your cloud folder.\n" +
        "• When you start up another computer, it copies in the newer settings before you play.\n" +
        "• Quit the game on one computer before playing on another. Every change is backed up first, and nothing is synced while the game runs.";

    internal FirstRun(SyncService service, IRegistry registry, IStartAtLogin? startAtLogin = null)
    {
        _service = service;
        _registry = registry;
        _startup = startAtLogin;
        Text = $"{ProductInfo.Name} setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;
        ClientSize = new Size(PageWidth + 56 + SystemInformation.VerticalScrollBarWidth, 660);

        BuildWelcome();
        BuildSetup();
        BuildChoices();
        BuildFinal();
        BuildProgress();
        foreach (var page in new[] { _welcomePage, _setupPage, _choicesPage, _finalPage, _progressPage }) _content.Controls.Add(page);

        // footer: Back on the left; Cancel and the main button on the right
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(28, 13, 28, 13) };
        footer.Paint += (_, e) => { using var pen = new Pen(Theme.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _next.Margin = _finish.Margin = new Padding(8, 0, 0, 0);
        _cancel.Margin = new Padding(0);
        right.Controls.AddRange([_finish, _next, _cancel]);
        _back.Dock = DockStyle.Left;
        footer.Controls.Add(right);
        footer.Controls.Add(_back);

        Controls.Add(_content);
        Controls.Add(footer);
        Controls.Add(_steps);
        Controls.Add(_banner);   // docked last = on top

        _next.Click += (_, _) => Next();
        _back.Click += (_, _) => ShowPage(_page - 1);
        _finish.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        AcceptButton = _next;
        CancelButton = _cancel;

        // ItemCheck fires before the item changes (and while Fill adds pre-ticked items, before the window
        // exists), so work out the new count from the event instead of deferring with BeginInvoke.
        _versions.ItemCheck += (_, e) => UpdateStartButton(
            _versions.CheckedItems.Count
            + (e.NewValue == CheckState.Checked ? 1 : 0)
            - (e.CurrentValue == CheckState.Checked ? 1 : 0));
        Fill();
        Theme.SetRole(_notes, TextRole.Warning);
        Theme.SetRole(_startAtLoginNote, TextRole.Muted);
        Theme.Apply(this);
        ShowPage(0);
    }

    /// <summary>Shows the window; true when the player started syncing.</summary>
    public static bool RunSetup(SyncService service, IRegistry registry, IStartAtLogin? startAtLogin = null)
    {
        using var form = new FirstRun(service, registry, startAtLogin);
        return form.ShowDialog() == DialogResult.OK;
    }

    // ------------------------------------------------------------------ pages

    private static FlowLayoutPanel Page() => new()
    {
        FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Location = new Point(28, 6), Visible = false,
    };

    private Label Title(string text) => new()
    {
        Text = text, AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, 15f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2),
    };

    private static Label Lead(string text)
    {
        var l = new Label { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 0, 0, 14) };
        Theme.SetRole(l, TextRole.Muted);
        return l;
    }

    private static Card MakeCard(params Control[] rows)
    {
        var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Location = new Point(16, 12) };
        inner.Controls.AddRange(rows);
        var card = new Card { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(PageWidth, 0), Padding = new Padding(16, 12, 16, 14) };
        card.Controls.Add(inner);
        return card;
    }

    /// <summary>A card's heading row: icon, title, and an optional button on the right.</summary>
    private Control CardHeader(string glyph, string title, Control? action = null)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Width = PageWidth - 34, Height = 40, Margin = new Padding(0, 0, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = new Glyph(glyph, 15f) { Anchor = AnchorStyles.Left };
        Theme.SetRole(icon, TextRole.Accent);
        row.Controls.Add(icon, 0, 0);
        row.Controls.Add(new Label { Text = title, AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold) }, 1, 0);
        if (action != null) { action.Anchor = AnchorStyles.Right; action.Margin = Padding.Empty; row.Controls.Add(action, 2, 0); }
        return row;
    }

    private Control Feature(string glyph, string title, string text)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = new Glyph(glyph, 20f) { Margin = new Padding(0, 4, 0, 0) };
        Theme.SetRole(icon, TextRole.Accent);
        row.Controls.Add(icon, 0, 0);
        row.SetRowSpan(icon, 2);
        row.Controls.Add(new Label { Text = title, AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) }, 1, 0);
        var body = new Label { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(PageWidth - 90, 0), Margin = Padding.Empty };
        Theme.SetRole(body, TextRole.Muted);
        row.Controls.Add(body, 1, 1);
        return MakeCard(row);
    }

    private void BuildWelcome()
    {
        _welcomePage.Controls.Add(Title($"Welcome to {ProductInfo.Name}"));
        _welcomePage.Controls.Add(Lead("Keep your keybindings, macros and addon settings the same on every computer you play on. Setup takes about a minute."));
        _welcomePage.Controls.Add(Feature(Draw.Cloud, "Saved when you quit",
            "When you quit the game, this PC's interface settings are copied to your own cloud folder (Google Drive, OneDrive or Dropbox)."));
        _welcomePage.Controls.Add(Feature(Draw.Sync, "Ready on every computer",
            "When you start up another computer, it copies in the newer settings before you play."));
        _welcomePage.Controls.Add(Feature(Draw.Shield, "Safe by design",
            "Quit the game on one computer before playing on another. Every change is backed up first, and nothing is synced while the game runs. " +
            "Your game files and addons themselves are never touched."));
    }

    private void BuildSetup()
    {
        _setupPage.Controls.Add(Title("Check what was found"));
        _setupPage.Controls.Add(Lead($"{ProductInfo.Name} looked for World of Warcraft and your cloud folder on this PC. If anything is wrong, change it here."));

        var versionsLabel = new Label { Text = "Game versions to sync", AutoSize = true, Margin = new Padding(0, 10, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        _setupPage.Controls.Add(MakeCard(CardHeader(Draw.Game, "World of Warcraft", ChangeButton(ChooseWow)), _wowPath, versionsLabel, _versions));

        _provider.SelectedIndexChanged += (_, _) => { if (!_filling) ProviderChanged(); };
        var keptIn = new Label { Text = "Your settings are kept in", AutoSize = true, Margin = new Padding(0, 8, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        _setupPage.Controls.Add(MakeCard(CardHeader(Draw.Cloud, "Cloud folder", ChangeButton(ChooseDrive)), _provider, _drivePath, keptIn, _cloudPath, _keepOnDevice));
        _setupPage.Controls.Add(_notes);
    }

    private void BuildChoices()
    {
        _choicesPage.Controls.Add(Title("Which settings should this PC use?"));
        Theme.SetRole(_choicesIntro, TextRole.Muted);
        _choicesPage.Controls.Add(_choicesIntro);
        _choicesPage.Controls.Add(_choicesPanel);
    }

    private void BuildFinal()
    {
        _finalPage.Controls.Add(Title("Almost done"));
        _finalPage.Controls.Add(Lead("A couple of preferences, then the first sync runs."));
        if (_startup != null)
        {
            var why = _startup.CannotEnableReason;
            _startAtLogin.Checked = why == null;
            _startAtLogin.Enabled = why == null;
            _startAtLoginNote.Text = why ?? "Recommended, so your settings are always synced when you quit the game.";
            _startAtLoginNote.Visible = true;
            _finalPage.Controls.Add(MakeCard(CardHeader(Draw.Power, "Start automatically"), _startAtLogin, _startAtLoginNote));
        }

        foreach (var (_, label) in TrayApp.ThemeChoices) _appearance.Items.Add(label);
        _appearance.SelectedIndex = Array.FindIndex(TrayApp.ThemeChoices, c => c.Theme == Theme.Mode);
        _appearance.SelectedIndexChanged += (_, _) =>
        {
            var chosen = TrayApp.ThemeChoices[Math.Max(0, _appearance.SelectedIndex)].Theme;
            if (chosen != Theme.Mode) TrayApp.ChooseTheme(_service, chosen);
        };
        _finalPage.Controls.Add(MakeCard(CardHeader(Draw.Paint, "Appearance", _appearance)));
        _finalPage.Controls.Add(MakeCard(CardHeader(Draw.Check, "Ready to sync"), _summary));
    }

    private void BuildProgress()
    {
        _progressHeading.Font = new Font(Font.FontFamily, 15f, FontStyle.Bold);
        _results.Columns.Add("Game version", 140);
        _results.Columns.Add("Result", PageWidth - 146);
        _progressPage.Controls.Add(_progressHeading);
        _progressPage.Controls.Add(_results);
        _progressPage.Controls.Add(_bar);
        _progressPage.Controls.Add(_afterword);
    }

    // ------------------------------------------------------------------ navigation

    private FlowLayoutPanel[] Pages => _choices.Count > 0
        ? [_welcomePage, _setupPage, _choicesPage, _finalPage]
        : [_welcomePage, _setupPage, _finalPage];

    private int PageCount => Pages.Length;

    private string[] StepNames => _choices.Count > 0
        ? ["Welcome", "Your setup", "Your settings", "Finish"]
        : ["Welcome", "Your setup", "Finish"];

    private void ShowPage(int index)
    {
        if (_syncStarted) return;
        _page = Math.Clamp(index, 0, PageCount - 1);
        var current = Pages[_page];
        _content.SuspendLayout();
        foreach (Control page in _content.Controls) page.Visible = page == current;
        _content.AutoScrollPosition = Point.Empty;
        _content.ResumeLayout();
        _steps.SetSteps(StepNames, _page);
        if (current == _finalPage) UpdateSummary();
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        if (_syncStarted) return;
        bool last = _page == PageCount - 1;
        _back.Visible = _page > 0;
        _next.Text = last ? "Start syncing" : "Next";
        _next.Enabled = _page == 0 || _canStart;
    }

    private void Next()
    {
        if (_page == PageCount - 1) { if (_canStart) StartSyncing(); }
        else ShowPage(_page + 1);
    }

    private void UpdateSummary()
    {
        var versions = Enumerable.Range(0, _versions.Items.Count).Where(_versions.GetItemChecked)
            .Select(i => ((VersionItem)_versions.Items[i]).Version.FolderName).ToList();
        var provider = _service.Drive?.Provider.DisplayName() ?? _service.EffectiveProvider().DisplayName();
        _summary.Text = versions.Count == 0
            ? "No game version is ticked. Go back and tick at least one."
            : $"{string.Join(", ", versions)} will sync through {provider}. Click Start syncing to run the first sync now.";
    }

    // For the UI smoke tests: the wizard page shown ("Welcome", "Your setup", ...).
    internal string CurrentStep => _syncStarted ? "Syncing" : StepNames[_page];
    internal void ClickNext() => _next.PerformClick();

    private sealed record VersionItem(GameVersion Version)
    {
        public override string ToString() =>
            $"{Version.FolderName}{(Version.ClientVersion != null ? $"  ({Version.ClientVersion})" : "")}";
    }

    private void Fill()
    {
        var wow = _service.Wow;
        var drive = _service.Drive;
        _wowPath.Text = wow is { Found: true } ? string.Join(Environment.NewLine, wow.Installs.Select(i => i.Root)) : "Not found: choose its folder";
        var provider = drive?.Provider ?? _service.EffectiveProvider();
        var all = _service.DetectAllProviders();
        _filling = true;
        _provider.Items.Clear();
        foreach (var p in CloudProviders.All)
            _provider.Items.Add(new ProviderItem(p, all[p].Found));
        _provider.SelectedIndex = Array.IndexOf(CloudProviders.All, provider);
        _filling = false;
        _drivePath.Text = drive is { Found: true }
            ? drive.Provider == CloudProvider.GoogleDrive && drive.Mode != DriveMode.Unknown
                ? $"{drive.MyDrivePath}  ({(drive.Mode == DriveMode.Streaming ? "streaming" : "mirrored")})"
                : drive.MyDrivePath!
            : $"Not found: choose your {provider.DisplayName()} folder";
        _cloudPath.Text = drive is { Found: true } ? PathUtil.Join(drive.MyDrivePath!, CloudLayout.AppFolderName) : "-";
        _keepOnDevice.Visible = _service.CanKeepOnDevice;
        _keepOnDevice.Text = $"Always keep the {CloudLayout.AppFolderName} folder on this device (recommended)";

        _versions.Items.Clear();
        var notLaunched = new List<string>();
        foreach (var v in _service.Versions)
        {
            if (!v.CanEnable) { notLaunched.Add(v.FolderName); continue; }
            bool on = !_service.Settings.EnabledVersions.TryGetValue(v.FolderName, out var saved) || saved;
            _versions.Items.Add(new VersionItem(v), on);
        }
        // Only as tall as it needs to be (up to five rows), so the window fits on small screens.
        _versions.Height = Math.Clamp(_versions.Items.Count, 1, 5) * _versions.ItemHeight + 8;

        var notes = new List<string>();
        if (notLaunched.Count > 0)
            notes.Add($"Not shown because they haven't been played on this PC yet: {string.Join(", ", notLaunched)}.");
        if (drive is { Mode: DriveMode.Streaming })
            notes.Add($"Google Drive is streaming files. {drive.Provider.KeepOfflineHint()}");
        else if (drive is { Found: true } && drive.Provider != CloudProvider.GoogleDrive)
            notes.Add($"{drive.Provider.DisplayName()} can keep files online-only. {drive.Provider.KeepOfflineHint()}");
        if (LegacyV11.FindRunEntries(_registry) is { Count: > 0 } legacy)
            notes.Add($"The previous version (WoWSync 1.1) is still set to start at login ({string.Join(", ", legacy)}). Stop its agent before syncing with {ProductInfo.Name}.");
        _notes.Text = string.Join(Environment.NewLine + Environment.NewLine, notes);
        _notes.Visible = notes.Count > 0;
        FillChoices();
        UpdateStartButton();
        ShowPage(Math.Min(_page, PageCount - 1));
    }

    /// <summary>
    /// For each game version whose cloud copy already has settings: download them, or upload this PC's instead.
    /// Shows where the cloud copy came from and when each was last changed; the newer one is preselected.
    /// </summary>
    private void FillChoices()
    {
        _choicesPanel.Controls.Clear();
        _choices.Clear();
        var previews = _service.PreviewFirstSync().Where(p => p.NeedsChoice).ToList();
        if (previews.Count == 0) { _choicesPanel.Visible = false; return; }
        _choicesPanel.Visible = true;
        _choicesIntro.Text = $"{previews[0].ProviderName} already has settings from another computer. For each game version, pick which " +
                             "settings this PC should use. The newer copy is picked for you, and the other is backed up either way.";
        foreach (var p in previews)
        {
            var useCloud = WrappedRadio(p.CloudOptionText(), p.Suggested == FirstSyncChoice.UseCloud);
            var useMine = WrappedRadio(p.LocalOptionText(), p.Suggested == FirstSyncChoice.UseThisComputer);
            var title = new Label { Text = p.Version, AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
            var group = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            group.Controls.AddRange([title, useCloud, useMine]);   // one group per version, so each pair is its own choice
            _choicesPanel.Controls.Add(MakeCard(group));
            _choices.Add((p.Version, useCloud, useMine));
        }
    }

    private void UpdateStartButton() => UpdateStartButton(_versions.CheckedItems.Count);

    private void UpdateStartButton(int checkedCount)
    {
        _canStart = _service.Wow is { Found: true } && _service.Drive is { Found: true } && checkedCount > 0;
        UpdateFooter();
    }

    // For the UI smoke tests.
    internal bool CanStart => _canStart;
    internal int VersionCount => _versions.Items.Count;
    internal int CheckedCount => _versions.CheckedItems.Count;
    internal void SetChecked(int index, bool value) => _versions.SetItemChecked(index, value);
    internal IReadOnlyList<string> ChoiceVersions => _choices.Select(c => c.version).ToList();
    internal void ChooseThisComputer(string version) => _choices.First(c => c.version == version).useMine.Checked = true;

    private void ChooseWow()
    {
        var path = PickFolder("Choose your World of Warcraft folder (the one that contains _retail_ or _forever_).");
        if (path == null) return;
        _service.Settings.WowInstallOverride = path;
        _service.SaveSettings(_service.Settings);
        if (_service.Wow is not { Source: DetectionSource.UserOverride })
            MessageBox.Show(this, "That folder doesn't contain a World of Warcraft game version (such as _retail_ or _forever_).", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Fill();
    }

    private void ChooseDrive()
    {
        var provider = _service.EffectiveProvider();
        if (_service.Settings.CloudProvider == null) _service.Settings.CloudProvider = provider;
        var path = PickFolder(provider == CloudProvider.GoogleDrive
            ? "Choose your Google Drive \"My Drive\" folder."
            : $"Choose your {provider.DisplayName()} folder.");
        if (path == null) return;
        _service.Settings.DriveOverride = path;
        _service.SaveSettings(_service.Settings);
        Fill();
    }

    private sealed record ProviderItem(CloudProvider Provider, bool Found)
    {
        public override string ToString() => Found ? Provider.DisplayName() : $"{Provider.DisplayName()} (not found on this PC)";
    }

    private void ProviderChanged()
    {
        if (_provider.SelectedItem is not ProviderItem item) return;
        _service.Settings.CloudProvider = item.Provider;
        _service.Settings.DriveOverride = null; // a chosen folder belongs to the previous app
        _service.SaveSettings(_service.Settings);
        Fill();
    }

    // For the UI smoke tests.
    internal IReadOnlyList<string> ProviderChoices => _provider.Items.Cast<object>().Select(o => o.ToString()!).ToList();
    internal void ChooseProvider(CloudProvider p) => _provider.SelectedIndex = Array.IndexOf(CloudProviders.All, p);
    internal string CloudFolderText => _drivePath.Text;
    internal bool KeepOnDeviceOffered => _keepOnDevice.Visible;
    internal bool KeepOnDeviceTicked => _keepOnDevice.Checked;

    private string? PickFolder(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = false };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void StartSyncing()
    {
        var settings = _service.Settings;
        for (int i = 0; i < _versions.Items.Count; i++)
        {
            var item = (VersionItem)_versions.Items[i];
            settings.EnabledVersions[item.Version.FolderName] = _versions.GetItemChecked(i);
        }
        foreach (var (version, useCloud, useMine) in _choices)
            if (settings.EnabledVersions.TryGetValue(version, out var on) && on)
                settings.FirstSyncChoices[version] = useMine.Checked ? FirstSyncChoice.UseThisComputer : FirstSyncChoice.UseCloud;
        settings.SetupComplete = true;
        settings.CloudProvider ??= _service.EffectiveProvider();
        _service.SaveSettings(settings);
        if (_keepOnDevice.Visible && _keepOnDevice.Checked) _service.KeepCloudFolderOnDevice();
        ApplyStartAtLogin();
        ShowProgressAndSync();
    }

    private void ApplyStartAtLogin()
    {
        if (_startup == null || !_startAtLogin.Enabled) return;
        try
        {
            if (_startAtLogin.Checked) _startup.Enable();
            else if (_startup.IsEnabled) _startup.Disable();
            _service.Log.Info($"{_startup.Label}: {(_startAtLogin.Checked ? "on" : "off")}.");
        }
        catch (Exception e) { _service.Log.Warn($"Couldn't change {_startup.Label}: {e.Message}"); }
    }

    /// <summary>For tests.</summary>
    internal bool StartAtLoginChecked { get => _startAtLogin.Checked; set => _startAtLogin.Checked = value; }

    private string RestartLine => _startup?.IsEnabled == true
        ? $"It starts by itself when you sign in to Windows (switch that off in the tray menu: {_startup.Label})."
        : $"Start {ProductInfo.Name} again after restarting this PC, or switch on {_startup?.Label ?? "Start with Windows"} in the tray menu.";

    // ------------------------------------------------------------------ first sync

    private void ShowProgressAndSync()
    {
        _syncStarted = true;
        _syncing = true;
        foreach (Control page in _content.Controls) page.Visible = page == _progressPage;
        _steps.SetSteps(StepNames, StepNames.Length);
        _back.Visible = _next.Visible = _cancel.Visible = false;
        _finish.Visible = true;
        AcceptButton = _finish;
        CancelButton = null;
        ControlBox = false; // no closing mid-copy
        _progressHeading.Text = "Running the first sync...";
        _banner.Subtitle = "Running the first sync";
        _afterword.Text =
            $"{ProductInfo.Name} keeps running in the system tray: its icon by the clock (it may be under the ^ arrow).\n\n" +
            "What to do now: just play. When you quit the game, your settings are copied to the cloud within a minute. " +
            $"On your other computer, open {ProductInfo.Name} with the game closed and it copies them in.\n\n" +
            RestartLine;

        var workers = _service.Workers;
        _results.Items.Clear();
        foreach (var w in workers)
            _results.Items.Add(new ListViewItem([w.Version.FolderName, "Waiting..."]) { Tag = w });

        Task.Run(() =>
        {
            int problems = 0;
            for (int i = 0; i < workers.Count; i++)
            {
                var w = workers[i];
                int row = i;
                Post(() => _results.Items[row].SubItems[1].Text = "Syncing...");
                bool firstSync = !w.State.Joined;
                SyncOutcome outcome;
                try { outcome = _service.SyncNow(w); }
                catch (Exception e) { outcome = SyncOutcome.Failed; w.State.LastError = e.Message; }
                var (text, problem) = Describe(w, outcome, firstSync);
                if (problem) problems++;
                Post(() =>
                {
                    var item = _results.Items[row];
                    item.SubItems[1].Text = text;
                    if (problem) item.ForeColor = Theme.Error;
                    _results.Invalidate();
                });
            }
            Post(() => FinishProgress(workers.Count, problems));
        });
    }

    private void FinishProgress(int versions, int problems)
    {
        _banner.Subtitle = problems == 0 ? "All set" : "Almost there";
        _syncing = false;
        _bar.Visible = false;
        ControlBox = true;
        _progressHeading.Text = versions == 0
            ? "Setup complete. No game version is switched on."
            : problems == 0 ? "You're all set." : "Setup complete, but something needs a look (shown in red).";
        _afterword.Text =
            $"{ProductInfo.Name} keeps running in the system tray: its icon by the clock (it may be under the ^ arrow). " +
            "It syncs on its own whenever you close the game. Click the icon to see its status at any time.\n\n" + RestartLine;
        _finish.Enabled = true;
        _finish.Focus();
    }

    /// <summary>Plain-words result for one version's first sync, and whether it needs the player.</summary>
    internal static (string text, bool problem) Describe(VersionSync w, SyncOutcome outcome, bool firstSync) =>
        FirstSyncReport.Describe(w, outcome, firstSync);

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_syncing) { e.Cancel = true; return; }          // never close mid-copy
        if (_syncStarted) DialogResult = DialogResult.OK;   // closing after the sync counts as Finish
        base.OnFormClosing(e);
    }

    // For the UI smoke tests.
    internal void ClickStart() { if (_canStart) StartSyncing(); }
    internal bool IsFinished => _finish.Enabled;
    internal IReadOnlyList<string> Results => _results.Items.Cast<ListViewItem>().Select(i => $"{i.Text}: {i.SubItems[1].Text}").ToList();

    /// <summary>A radio button whose long text wraps instead of running off the window.</summary>
    private RadioButton WrappedRadio(string text, bool chosen)
    {
        const int width = PageWidth - 34;
        var height = TextRenderer.MeasureText(text, Font, new Size(width - 44, 0), TextFormatFlags.WordBreak).Height;   // room for the round button and its gap
        return new RadioButton { Text = text, AutoSize = false, Size = new Size(width, height + 10), Checked = chosen, TextAlign = ContentAlignment.TopLeft, CheckAlign = ContentAlignment.TopLeft, UseMnemonic = false };
    }

    /// <summary>Taller than the screen (small laptop, large text): shrink; the page scrolls instead.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        if (Height > area.Height)
        {
            Height = area.Height - 16;
            Top = area.Top + 8;
        }
    }

    /// <summary>For tests: whether the page has to scroll to fit.</summary>
    internal bool Scrolls => _content.VerticalScroll.Visible;

    private static Label PathLabel()
    {
        var l = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(PageWidth - 34, 0), Margin = new Padding(0, 2, 0, 4) };
        Theme.SetRole(l, TextRole.Muted);
        return l;
    }

    private ModernButton ChangeButton(Action onClick)
    {
        var b = new ModernButton("Change...") { Height = 30 };
        b.Click += (_, _) => onClick();
        return b;
    }
}
