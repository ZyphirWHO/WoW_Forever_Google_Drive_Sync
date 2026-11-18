using InterfaceSync.Core;
using System.Diagnostics;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

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

    private readonly Label _overall = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label _paths = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill, Height = 130 };
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private readonly Button _syncNow = new() { Text = "Sync now", AutoSize = true };
    private readonly Button _pause = new() { Text = "Pause syncing", AutoSize = true };
    private readonly Button _resolve = new() { Text = "Choose settings to keep...", AutoSize = true, Enabled = false };
    private bool _allowClose;

    public StatusForm(IStatusHost app)
    {
        _app = app;
        Text = $"{ProductInfo.Name} {ProductInfo.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 600);
        MinimumSize = new Size(640, 420);
        Font = SystemFonts.MessageBoxFont ?? Font;
        Padding = new Padding(10);

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
        var copyDiag = new Button { Text = "Copy diagnostics", AutoSize = true };
        copyDiag.Click += (_, _) =>
        {
            Clipboard.SetText(Service.Diagnostics());
            MessageBox.Show(this, "Diagnostics copied. Paste them into your message or GitHub issue.", ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange([_syncNow, _pause, _resolve, openLog, openBackups, copyDiag]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_overall, 0, 0);
        layout.Controls.Add(_paths, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(new Label { Text = "Recent activity", AutoSize = true, Margin = new Padding(0, 4, 0, 2) }, 0, 4);
        layout.Controls.Add(_logBox, 0, 5);
        Controls.Add(layout);
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
        _overall.ForeColor = Service.State == OverallState.NeedsAttention ? Color.FromArgb(180, 30, 30) : SystemColors.ControlText;
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
            if (w.Status is SyncStatus.NeedsAttention or SyncStatus.Error) item.ForeColor = Color.FromArgb(180, 30, 30);
            _list.Items.Add(item);
            if (w.Version.FolderName == selected) item.Selected = true;
        }
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v)))
            _list.Items.Add(new ListViewItem([v.FolderName, v.CanEnable ? "Off" : "Not played on this PC yet", "", "", ""]) { ForeColor = SystemColors.GrayText });
        _list.EndUpdate();

        _pause.Text = Service.Settings.Paused ? "Resume syncing" : "Pause syncing";
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
            Text = $"{c.Reason}\n\nThis PC: last changed {Format(c.LocalNewestUtc)}\n" +
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
