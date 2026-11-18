using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Service;
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
    private readonly CheckedListBox _versions = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false, Height = 110 };
    private readonly Label _notes = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(140, 70, 0) };
    private readonly Button _start = new() { Text = "Start syncing", AutoSize = true };
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

    internal FirstRun(SyncService service, IRegistry registry)
    {
        _service = service;
        _registry = registry;
        Text = $"{ProductInfo.Name} setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var grid = _setupGrid;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label { Text = $"{ProductInfo.Name} found these on this PC. Check them, then start syncing.", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 3);

        AddRow(grid, 1, "World of Warcraft:", _wowPath, ChangeButton(ChooseWow));
        grid.Controls.Add(new Label { Text = "Sync these game versions:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 2);
        grid.Controls.Add(_versions, 1, 2);
        var cloudCell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        cloudCell.Controls.Add(_provider);
        cloudCell.Controls.Add(_drivePath);
        AddRow(grid, 3, "Cloud folder:", cloudCell, ChangeButton(ChooseDrive));
        _provider.SelectedIndexChanged += (_, _) => { if (!_filling) ProviderChanged(); };
        AddRow(grid, 4, "Settings are kept in:", _cloudPath, null);
        grid.Controls.Add(_notes, 0, 5);
        grid.SetColumnSpan(_notes, 3);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        _start.Click += (_, _) => StartSyncing();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_start);
        grid.Controls.Add(buttons, 0, 6);
        grid.SetColumnSpan(buttons, 3);

        Controls.Add(grid);
        BuildProgressPage();
        Controls.Add(_progressPage);
        AcceptButton = _start;
        CancelButton = cancel;
        // ItemCheck fires before the item changes (and while Fill adds pre-ticked items, before the window
        // exists), so work out the new count from the event instead of deferring with BeginInvoke.
        _versions.ItemCheck += (_, e) => UpdateStartButton(
            _versions.CheckedItems.Count
            + (e.NewValue == CheckState.Checked ? 1 : 0)
            - (e.CurrentValue == CheckState.Checked ? 1 : 0));
        Fill();
    }

    /// <summary>Shows the window; true when the player started syncing.</summary>
    public static bool RunSetup(SyncService service, IRegistry registry)
    {
        using var form = new FirstRun(service, registry);
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
        notes.Add($"On this PC's first sync, if {provider.DisplayName()} already has settings for a game version, they replace this PC's. A backup of this PC's settings is kept and can be restored.");
        _notes.Text = string.Join(Environment.NewLine + Environment.NewLine, notes);
        UpdateStartButton();
    }

    private void UpdateStartButton() => UpdateStartButton(_versions.CheckedItems.Count);

    private void UpdateStartButton(int checkedCount) =>
        _start.Enabled = _service.Wow is { Found: true } && _service.Drive is { Found: true } && checkedCount > 0;

    // For the UI smoke tests.
    internal bool CanStart => _start.Enabled;
    internal int VersionCount => _versions.Items.Count;
    internal int CheckedCount => _versions.CheckedItems.Count;
    internal void SetChecked(int index, bool value) => _versions.SetItemChecked(index, value);

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
        settings.SetupComplete = true;
        settings.CloudProvider ??= _service.EffectiveProvider();
        _service.SaveSettings(settings);
        ShowProgressAndSync();
    }

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
        _afterword.Text = "";

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
                    if (problem) item.ForeColor = Color.FromArgb(180, 30, 30);
                });
            }
            Post(() => FinishProgress(workers.Count, problems));
        });
    }

    private void FinishProgress(int versions, int problems)
    {
        _syncing = false;
        _bar.Visible = false;
        ControlBox = true;
        _progressHeading.Text = versions == 0
            ? "Setup complete. No game version is switched on."
            : problems == 0 ? "Setup complete." : "Setup complete, but something needs a look (shown in red).";
        _afterword.Text =
            $"{ProductInfo.Name} keeps running in the system tray: its icon by the clock (it may be under the ^ arrow). " +
            "It syncs on its own whenever you close the game. Click the icon to see its status at any time.";
        _finish.Enabled = true;
        _finish.Focus();
    }

    /// <summary>Plain-words result for one version's first sync, and whether it needs the player.</summary>
    internal static (string text, bool problem) Describe(VersionSync w, SyncOutcome outcome, bool firstSync) => outcome switch
    {
        SyncOutcome.Pushed when firstSync => ($"Done. {w.Cloud.ProviderName} had no settings for this version yet, so this PC's were uploaded.", false),
        SyncOutcome.Pushed => ($"Done. This PC's settings were uploaded to {w.Cloud.ProviderName}.", false),
        SyncOutcome.Pulled when firstSync => ($"Done. The settings already in {w.Cloud.ProviderName} were copied to this PC (this PC's previous settings are backed up).", false),
        SyncOutcome.Pulled => ($"Done. Newer settings from {w.Cloud.ProviderName} were copied to this PC.", false),
        SyncOutcome.NothingToDo => (w.Status == SyncStatus.Idle && w.StatusText.StartsWith("Nothing", StringComparison.Ordinal)
            ? w.StatusText : "Done. Already in sync.", false),
        SyncOutcome.Waiting when w.Status == SyncStatus.GameRunning =>
            ("Waiting: this game is running. It will sync after you close it.", false),
        SyncOutcome.Waiting => ($"Waiting: {w.StatusText}. It will sync on its own when ready.", false),
        SyncOutcome.Busy => ("Will sync in the background in a moment.", false),
        SyncOutcome.Conflict => ("Needs your choice: settings changed in two places. Open the status window to choose.", true),
        _ => ($"Problem: {(string.IsNullOrEmpty(w.StatusText) ? w.State.LastError : w.StatusText)}", true),
    };

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
