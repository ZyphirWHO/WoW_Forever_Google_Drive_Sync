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
/// First run: shows what was found, lets the player change any path and pick game versions, then runs
/// each version's first sync with its result shown, and a Finish button once everything is done.
/// No typing needed when detection works.
/// </summary>
internal sealed class FirstRun : Form
{
    private readonly SyncService _service;
    private readonly IRegistry _registry;
    private readonly Label _wowPath = PathLabel();
    private readonly Label _drivePath = PathLabel();
    private readonly Label _cloudPath = PathLabel();
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Margin = new Padding(0, 4, 8, 4) };
    private bool _filling;
    private readonly CheckBox _keepOnDevice = new() { AutoSize = true, Margin = new Padding(0, 2, 0, 4), Visible = false };
    private readonly CheckedListBox _versions = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false, Height = 110 };
    private readonly Label _notes = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Button _start = new() { Text = "Start syncing", AutoSize = true };
    private readonly BrandBanner _banner = new($"Welcome! Let's set up {ProductInfo.Name} on this PC.");
    private readonly IStartAtLogin? _startup;
    private readonly CheckBox _startAtLogin = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 2), Text = $"Start {ProductInfo.Name} when I sign in to Windows (recommended)" };
    private readonly Label _startAtLoginNote = new() { AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(18, 0, 0, 6), Visible = false };
    private readonly FlowLayoutPanel _choicesPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 8) };
    private readonly List<(string version, RadioButton useCloud, RadioButton useMine)> _choices = [];

    internal static readonly string HowItWorks =
        "• When you quit the game, this PC's interface settings (keybindings, macros, addon settings) are copied to your cloud folder.\n" +
        "• When you start up another computer, it copies in the newer settings before you play.\n" +
        "• Quit the game on one computer before playing on another. Every change is backed up first, and nothing is synced while the game runs.";
    private readonly TableLayoutPanel _setupGrid = new() { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };

    // First-sync progress page
    private readonly TableLayoutPanel _progressPage = new() { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Visible = false };
    private readonly Label _progressHeading = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
    private readonly ListView _results = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = 640, Height = 150 };
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 640, Height = 14, Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _afterword = new() { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 10, 0, 0) };
    private readonly Button _finish = new() { Text = "Finish", AutoSize = true, Enabled = false };
    private bool _syncStarted;
    private volatile bool _syncing;

    internal FirstRun(SyncService service, IRegistry registry, IStartAtLogin? startAtLogin = null)
    {
        _service = service;
        _registry = registry;
        _startup = startAtLogin;
        Text = $"{ProductInfo.Name} setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Padding.Empty;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var grid = _setupGrid;
        grid.Dock = DockStyle.None;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        top.Controls.Add(new SectionHeader("How it works") { Margin = new Padding(0, 0, 0, 4) });
        top.Controls.Add(new Label { Text = HowItWorks, AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 6) });
        top.Controls.Add(new SectionHeader("Found on this PC"));
        top.Controls.Add(new Label { Text = "Check these, then start syncing.", AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        grid.Controls.Add(top, 0, 0);
        grid.SetColumnSpan(top, 3);

        AddRow(grid, 1, "World of Warcraft:", _wowPath, ChangeButton(ChooseWow));
        grid.Controls.Add(new Label { Text = "Sync these game versions:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 2);
        grid.Controls.Add(_versions, 1, 2);
        var cloudCell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        cloudCell.Controls.Add(_provider);
        cloudCell.Controls.Add(_drivePath);
        cloudCell.Controls.Add(_keepOnDevice);
        AddRow(grid, 3, "Cloud folder:", cloudCell, ChangeButton(ChooseDrive));
        _provider.SelectedIndexChanged += (_, _) => { if (!_filling) ProviderChanged(); };
        AddRow(grid, 4, "Settings are kept in:", _cloudPath, null);
        var lower = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        lower.Controls.Add(_choicesPanel);
        lower.Controls.Add(_notes);
        if (_startup != null)
        {
            var why = _startup.CannotEnableReason;
            _startAtLogin.Checked = why == null;
            _startAtLogin.Enabled = why == null;
            _startAtLoginNote.Text = why ?? "";
            _startAtLoginNote.Visible = why != null;
            lower.Controls.Add(_startAtLogin);
            lower.Controls.Add(_startAtLoginNote);
        }
        grid.Controls.Add(lower, 0, 5);
        grid.SetColumnSpan(lower, 3);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _start.Click += (_, _) => StartSyncing();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_start);
        grid.Controls.Add(buttons, 0, 6);
        grid.SetColumnSpan(buttons, 3);

        BuildProgressPage();
        _progressPage.Dock = DockStyle.None;
        // Banner across the top, then the setup page or (once started) the progress page.
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(16, 12, 16, 14), Margin = Padding.Empty };
        body.Controls.Add(grid);
        body.Controls.Add(_progressPage);
        var outer = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Margin = Padding.Empty, Padding = Padding.Empty };
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _banner.Dock = DockStyle.Fill;
        outer.Controls.Add(_banner, 0, 0);
        outer.Controls.Add(body, 0, 1);
        Controls.Add(outer);
        Theme.SetPrimary(_start);
        Theme.SetPrimary(_finish);
        AcceptButton = _start;
        CancelButton = cancel;
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
    }

    /// <summary>Shows the window; true when the player started syncing.</summary>
    public static bool RunSetup(SyncService service, IRegistry registry, IStartAtLogin? startAtLogin = null)
    {
        using var form = new FirstRun(service, registry, startAtLogin);
        return form.ShowDialog() == DialogResult.OK;
    }

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
        FillChoices();
        UpdateStartButton();
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
        _choicesPanel.Controls.Add(new Label
        {
            Text = $"{previews[0].ProviderName} already has settings from another computer. For each game version, choose which to keep:",
            AutoSize = true, MaximumSize = new Size(600, 0), Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4),
        });
        foreach (var p in previews)
        {
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(8, 2, 0, 6) };
            box.Controls.Add(new Label { Text = p.Version, AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            var useCloud = new RadioButton { Text = p.CloudOptionText(), AutoSize = true, MaximumSize = new Size(590, 0), Checked = p.Suggested == FirstSyncChoice.UseCloud };
            var useMine = new RadioButton { Text = p.LocalOptionText(), AutoSize = true, MaximumSize = new Size(590, 0), Checked = p.Suggested == FirstSyncChoice.UseThisComputer };
            box.Controls.Add(useCloud);
            box.Controls.Add(useMine);
            _choicesPanel.Controls.Add(box);
            _choices.Add((p.Version, useCloud, useMine));
        }
    }

    private void UpdateStartButton() => UpdateStartButton(_versions.CheckedItems.Count);

    private void UpdateStartButton(int checkedCount) =>
        _start.Enabled = _service.Wow is { Found: true } && _service.Drive is { Found: true } && checkedCount > 0;

    // For the UI smoke tests.
    internal bool CanStart => _start.Enabled;
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

    // ------------------------------------------------------------------ first-sync progress

    private void BuildProgressPage()
    {
        _progressHeading.Font = new Font(Font, FontStyle.Bold);
        _results.Columns.Add("Game version", 130);
        _results.Columns.Add("Result", 505);
        _finish.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_finish);
        _progressPage.Controls.Add(_progressHeading);
        _progressPage.Controls.Add(_results);
        _progressPage.Controls.Add(_bar);
        _progressPage.Controls.Add(_afterword);
        _progressPage.Controls.Add(buttons);
    }

    private void ShowProgressAndSync()
    {
        _syncStarted = true;
        _syncing = true;
        _setupGrid.Visible = false;
        _progressPage.Visible = true;
        AcceptButton = _finish;
        CancelButton = null;
        ControlBox = false; // no closing mid-copy
        _progressHeading.Text = "Running the first sync...";
        _banner.Subtitle = "Running the first sync";
        _afterword.Text =
            $"{ProductInfo.Name} keeps running in the system tray: its icon by the clock (it may be under the ^ arrow).\n\n" +
            "What to do now: just play. When you quit the game, your settings are copied to the cloud within a minute. " +
            "On your other computer, open Interface Sync with the game closed and it copies them in. " +
            "Click the tray icon any time to see what it did, change cloud folder, or open your backups.\n\n" +
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
            : problems == 0 ? "Setup complete." : "Setup complete, but something needs a look (shown in red).";
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
    internal void ClickStart() => _start.PerformClick();
    internal bool IsFinished => _finish.Enabled;
    internal IReadOnlyList<string> Results => _results.Items.Cast<ListViewItem>().Select(i => $"{i.Text}: {i.SubItems[1].Text}").ToList();

    private static Label PathLabel() => new() { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 6, 8, 6) };

    private Button ChangeButton(Action onClick)
    {
        var b = new Button { Text = "Change...", AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string caption, Control value, Control? button)
    {
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        grid.Controls.Add(value, 1, row);
        if (button != null) grid.Controls.Add(button, 2, row);
    }
}
