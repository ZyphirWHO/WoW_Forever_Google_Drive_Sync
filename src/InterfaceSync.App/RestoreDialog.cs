using InterfaceSync.Core;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.App;

/// <summary>
/// Restore a game version's settings from one of its backups: a list (when, why, what it holds), then Restore.
/// The current settings are backed up first, so a restore can itself be undone from this list.
/// </summary>
internal sealed class RestoreDialog : Form
{
    private readonly IStatusHost _app;
    private readonly VersionSync _worker;
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
    private readonly ModernButton _restore = new("Restore", ButtonKind.Primary) { Enabled = false };
    private readonly Label _note = new() { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 10) };

    public RestoreDialog(IStatusHost app, VersionSync worker)
    {
        _app = app;
        _worker = worker;
        Text = $"Restore {worker.Version.FolderName}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 470);
        MinimumSize = new Size(560, 360);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var title = new Label { Text = $"Restore {worker.Version.FolderName} settings", AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _note.Text = "Pick a backup to put this game version's settings back as they were at that time. Close the game first. " +
                     "Your current settings are backed up before anything changes, so you can undo a restore from this list.";
        Theme.SetRole(_note, TextRole.Muted);

        _list.Columns.Add("When", 210);
        _list.Columns.Add("What", 300);
        _list.Columns.Add("Holds", 150);
        _list.SelectedIndexChanged += (_, _) => _restore.Enabled = _list.SelectedItems.Count > 0;
        _list.DoubleClick += (_, _) => Restore();
        _restore.Click += (_, _) => Restore();
        var cancel = new ModernButton("Cancel", ButtonKind.Ghost) { DialogResult = DialogResult.Cancel };
        var openFolder = new ModernButton("Open backups folder", ButtonKind.Ghost) { Icon = Draw.Folder };
        openFolder.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(_app.Service.Paths.Backups, worker.Version.FolderName)) { UseShellExecute = true }); }
            catch (Exception) { }
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        _restore.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.AddRange([_restore, cancel, openFolder]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 16, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_note, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = _restore;
        CancelButton = cancel;
        Fill();
        Theme.Apply(this);
    }

    internal static string When(BackupInfo b) =>
        $"{b.CreatedUtc.ToLocalTime():ddd d MMM, HH:mm}  ({StatusWords.Ago(b.CreatedUtc, DateTime.UtcNow)})";

    internal static string Holds(BackupInfo b) =>
        b.Partial ? $"{b.FileCount} changed file{(b.FileCount == 1 ? "" : "s")}" : $"whole folder, {b.FileCount} file{(b.FileCount == 1 ? "" : "s")}";

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var b in _app.Service.Backups.List(_worker.Version.FolderName))
            _list.Items.Add(new ListViewItem([When(b), b.Description, Holds(b)]) { Tag = b });
        if (_list.Items.Count == 0)
            _list.Items.Add(new ListViewItem(["No backups yet", "One is made before every download or restore", ""]) { ForeColor = Theme.Muted });
    }

    /// <summary>For tests.</summary>
    internal int BackupCount => _list.Items.Cast<ListViewItem>().Count(i => i.Tag is BackupInfo);
    internal void Select(int index) { _list.Items[index].Selected = true; _list.Items[index].Focused = true; }
    internal SyncOutcome? LastOutcome { get; private set; }
    internal bool Quiet { get; set; }   // tests: no message boxes

    private void Restore()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BackupInfo backup) return;
        if (!ThemedDialog.Ask(this,
                "Your current settings are backed up first, and the restored settings are then uploaded to your other computers.",
                "Restore", "Cancel", $"Put {_worker.Version.FolderName}'s settings back as they were on {backup.CreatedUtc.ToLocalTime():f}?"))
            return;
        RestoreNow(backup);
    }

    internal void RestoreNow(BackupInfo backup)
    {
        _restore.Enabled = false;
        SyncOutcome? outcome = null;
        _app.Run(() => outcome = _app.Service.Restore(_worker, backup), () =>
        {
            LastOutcome = outcome;
            if (IsDisposed) return;
            var done = outcome is SyncOutcome.Pushed or SyncOutcome.NothingToDo or SyncOutcome.Waiting;
            if (!Quiet) ThemedDialog.Show(this, done
                    ? $"{_worker.Version.FolderName}'s settings are back as they were on {backup.CreatedUtc.ToLocalTime():f}" +
                      (outcome == SyncOutcome.Pushed ? " and were uploaded to your cloud folder." : ".")
                    : $"{_worker.State.LastError ?? _worker.StatusText}",
                done ? "Restored" : "The settings weren't restored", done ? DialogKind.Info : DialogKind.Warning);
            if (done) { DialogResult = DialogResult.OK; Close(); }
            else { Fill(); _restore.Enabled = _list.SelectedItems.Count > 0; }
        });
    }
}
