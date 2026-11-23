using InterfaceSync.Core.Logging;
using InterfaceSync.Core;
using System.Diagnostics;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Platform;

namespace InterfaceSync.App;

/// <summary>What the status window needs from the tray app (lets tests build the window without a tray).</summary>
internal interface IStatusHost
{
    SyncService Service { get; }
    void Run(Action work, Action? afterOnUi = null);
}

/// <summary>Per-version state, recent log lines and the rare manual actions.</summary>
internal sealed class StatusForm : Form
{
    private readonly IStatusHost _app;
    private SyncService Service => _app.Service;

    private readonly Label _overall = new() { AutoSize = true, Font = new Font((SystemFonts.MessageBoxFont ?? DefaultFont).FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _paths = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill, Height = 130 };
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _syncNow = new() { Text = "Sync now", AutoSize = true };
    private readonly Button _pause = new() { Text = "Pause syncing", AutoSize = true };
    private readonly Button _resolve = new() { Text = "Choose settings to keep...", AutoSize = true, Enabled = false };
    private readonly ComboBox _appearance = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox _startAtLogin = new() { AutoSize = true, AutoCheck = false, Margin = new Padding(16, 8, 3, 3), Visible = false };
    private bool _allowClose;

    /// <summary>Start with Windows; the checkbox is hidden when not set (tests).</summary>
    internal IStartAtLogin? StartAtLogin
    {
        get => _startup;
        init { _startup = value; _startAtLogin.Visible = value != null; if (value != null) { _startAtLogin.Text = value.Label; _startAtLogin.Checked = value.IsEnabled; } }
    }
    private readonly IStartAtLogin? _startup;

    public StatusForm(IStatusHost app)
    {
        _app = app;
        Text = $"{ProductInfo.Name} {ProductInfo.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 600);
        MinimumSize = new Size(640, 420);
        Font = SystemFonts.MessageBoxFont ?? Font;
        Padding = Padding.Empty;
        Icon = TrayIcons.AppIcon;


        _list.Columns.Add("Game version", 130);
        _list.Columns.Add("Status", 260);
        _list.Columns.Add("Last push", 120);
        _list.Columns.Add("Last pull", 120);
        _list.Columns.Add("Notes", 180);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        _syncNow.Click += (_, _) => _app.Run(Service.SyncNow);
        _pause.Click += (_, _) => _app.Run(() => Service.SetPaused(!Service.Settings.Paused));
        _resolve.Click += (_, _) => ResolveSelected();
        var openLog = new Button { Text = "Open log", AutoSize = true };
        openLog.Click += (_, _) => Open(Service.Paths.Logs);
        var openBackups = new Button { Text = "Open backups", AutoSize = true };
        openBackups.Click += (_, _) => Open(Service.Paths.Backups);
        var changeCloud = new Button { Text = "Change cloud folder...", AutoSize = true };
        changeCloud.Click += (_, _) => ChangeCloudFolder();
        var about = new Button { Text = "About", AutoSize = true };
        about.Click += (_, _) => ShowAbout(this);
        var copyDiag = new Button { Text = "Copy diagnostics", AutoSize = true };
        copyDiag.Click += (_, _) =>
        {
            Clipboard.SetText(Service.Diagnostics());
            MessageBox.Show(this, "Diagnostics copied. Paste them into your message or GitHub issue.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        foreach (var (_, label) in TrayApp.ThemeChoices) _appearance.Items.Add(label);
        SelectAppearance();
        _appearance.SelectedIndexChanged += (_, _) =>
        {
            var chosen = TrayApp.ThemeChoices[Math.Max(0, _appearance.SelectedIndex)].Theme;
            if (chosen != Theme.Mode) TrayApp.ChooseTheme(Service, chosen);
        };
        _startAtLogin.Click += (_, _) => ToggleStartAtLogin();
        var appearanceLabel = new Label { Text = "Appearance:", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange([_syncNow, _pause, _resolve, changeCloud, openLog, openBackups, copyDiag, about, appearanceLabel, _appearance, _startAtLogin]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(14, 10, 14, 12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_overall, 0, 0);
        layout.Controls.Add(_paths, 0, 1);
        layout.Controls.Add(new SectionHeader("Game versions") { Dock = DockStyle.Fill }, 0, 2);
        layout.Controls.Add(_list, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        layout.Controls.Add(new SectionHeader("Recent activity") { Dock = DockStyle.Fill }, 0, 5);
        layout.Controls.Add(_logBox, 0, 6);
        Controls.Add(layout);
        Controls.Add(new BrandBanner("Your interface settings, on every computer"));   // docks above the layout
        Theme.SetPrimary(_syncNow);

        Theme.Apply(this);
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>The Start with Windows checkbox (also used by tests).</summary>
    internal void ToggleStartAtLogin()
    {
        if (_startup == null) return;
        TrayApp.ToggleStartAtLogin(_startup, Service.Log, this);
        _startAtLogin.Checked = _startup.IsEnabled;
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        SelectAppearance();
        RefreshView();
    }

    private void SelectAppearance()
    {
        var index = Array.FindIndex(TrayApp.ThemeChoices, c => c.Theme == Theme.Mode);
        if (_appearance.SelectedIndex != index) _appearance.SelectedIndex = index;
    }

    /// <summary>The selected appearance (for tests).</summary>
    internal AppTheme SelectedAppearance => TrayApp.ThemeChoices[_appearance.SelectedIndex].Theme;

    internal void ChooseAppearance(AppTheme theme) =>
        _appearance.SelectedIndex = Array.FindIndex(TrayApp.ThemeChoices, c => c.Theme == theme);

    internal ListView VersionList => _list;

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

    public void RefreshView()
    {
        if (IsDisposed || !Visible) return;

        _overall.Text = Service.StateText;
        Theme.SetRole(_overall, Service.State == OverallState.NeedsAttention ? TextRole.Error : TextRole.Normal);
        var wow = Service.Wow?.Found == true ? string.Join("; ", Service.Wow.Installs.Select(i => i.Root)) : "not found";
        var drive = Service.Drive?.MyDrivePath ?? "not found";
        _paths.Text = $"World of Warcraft: {wow}{Environment.NewLine}{Service.ProviderName}: {drive}{Environment.NewLine}Log: {Service.Paths.Logs}";

        var selected = _list.SelectedItems.Count > 0 ? (_list.SelectedItems[0].Tag as VersionSync)?.Version.FolderName : null;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var w in Service.Workers)
        {
            var s = w.State;
            var notes = s.Conflict != null ? "Choose which to keep"
                : s.PendingPush ? "Push pending"
                : s.LastError ?? "";
            var item = new ListViewItem([
                w.Version.FolderName,
                string.IsNullOrEmpty(w.StatusText) ? w.Status.ToString() : w.StatusText,
                Format(s.LastPushUtc),
                Format(s.LastPullUtc),
                notes,
            ]) { Tag = w };
            if (w.Status is SyncStatus.NeedsAttention or SyncStatus.Error) item.ForeColor = Theme.Error;
            _list.Items.Add(item);
            if (w.Version.FolderName == selected) item.Selected = true;
        }
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v)))
            _list.Items.Add(new ListViewItem([v.FolderName, v.CanEnable ? "Off" : "Not played on this PC yet", "", "", ""]) { ForeColor = Theme.Muted });
        _list.EndUpdate();

        _pause.Text = Service.Settings.Paused ? "Resume syncing" : "Pause syncing";
        if (_startup != null) _startAtLogin.Checked = _startup.IsEnabled;
        // Newest first, so the latest action is visible without scrolling.
        _logBox.Text = string.Join(Environment.NewLine, Service.RecentLog(200).Reverse().Select(l => l.ToString()));
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var w = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as VersionSync : null;
        _resolve.Enabled = w?.State.Conflict != null;
    }

    private void ResolveSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not VersionSync w || w.State.Conflict is not { } c) return;

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
        $"Version {ProductInfo.Version}. {ProductInfo.Licence}.\n\n{ProductInfo.FontCredit}";

    internal static void ShowAbout(IWin32Window? owner)
    {
        var page = new TaskDialogPage
        {
            Caption = $"About {ProductInfo.Name}",
            Heading = $"{ProductInfo.Name} {ProductInfo.Version}",
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
}
