using InterfaceSync.Core;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.App;

/// <summary>
/// A game version's backups and snapshots: a list (when, why, what it holds), then Restore. Also saves a named
/// snapshot, deletes a snapshot, and saves a copy of any backup as a zip. The current settings are backed up before a
/// restore, so a restore can itself be undone from this list.
/// </summary>
internal sealed class RestoreDialog : Form
{
    private readonly IStatusHost _app;
    private readonly VersionSync _worker;
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
    private readonly ModernButton _restore = new("Restore", ButtonKind.Primary) { Enabled = false };
    private readonly ModernButton _delete = new("Delete", ButtonKind.Ghost) { Enabled = false, AccessibleDescription = "Deletes the selected snapshot" };
    private readonly ModernButton _zip = new("Save as zip…", ButtonKind.Ghost) { Enabled = false };
    private readonly Label _note = new() { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 10) };

    public RestoreDialog(IStatusHost app, VersionSync worker)
    {
        _app = app;
        _worker = worker;
        Text = $"{worker.Version.FolderName}: backups and snapshots";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(800, 500);
        MinimumSize = new Size(560, 360);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var title = new Label { Text = $"{worker.Version.FolderName}: backups and snapshots", AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        _note.Text = "Pick a backup to put this game version's settings back as they were at that time. Close the game first. " +
                     "Your current settings are backed up before anything changes, so you can undo a restore from this list. " +
                     "Save a snapshot to keep your current setup under a name until you delete it.";
        _note.MaximumSize = new Size(740, 0);
        Theme.SetRole(_note, TextRole.Muted);

        _list.Columns.Add("When", 210);
        _list.Columns.Add("What", 340);
        _list.Columns.Add("Holds", 170);
        _list.AccessibleName = "Backups and snapshots";
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _delete.Click += (_, _) => { if (Selected is { } b) DeleteSnapshot(b); };
        _zip.Click += (_, _) => { if (Selected is { } b) SaveZip(b); };
        var snapshot = new ModernButton("Save snapshot…", ButtonKind.Secondary) { Icon = Draw.Upload };
        snapshot.Click += (_, _) => SaveSnapshot();
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
        foreach (var b in new[] { _delete, _zip, snapshot, openFolder }) b.Margin = new Padding(0, 0, 6, 0);
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Left };
        left.Controls.AddRange([snapshot, _zip, _delete]);
        buttons.Controls.AddRange([_restore, cancel]);
        buttons.Dock = DockStyle.Right;
        buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        buttons.WrapContents = false;
        var bottom = new Panel { Dock = DockStyle.Fill, Height = 46, Margin = new Padding(0, 12, 0, 0) };
        bottom.Controls.Add(left);
        bottom.Controls.Add(buttons);
        buttons.Margin = Padding.Empty;
        cancel.Text = "Close";
        _ = openFolder;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 16, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_note, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(bottom, 0, 3);
        Controls.Add(layout);
        AcceptButton = _restore;
        CancelButton = cancel;
        Fill();
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
    }

    private BackupInfo? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as BackupInfo : null;

    private void UpdateButtons()
    {
        var b = Selected;
        _restore.Enabled = b != null;
        _zip.Enabled = b != null;
        _delete.Enabled = b is { Pinned: true } or { GameUpdate: true };
    }

    /// <summary>Saves the current settings as a named snapshot (the name is asked for).</summary>
    internal BackupInfo? SaveSnapshot()
    {
        var name = InputDialog.Ask(this, "Save a snapshot",
            $"A copy of {_worker.Version.FolderName}'s settings as they are now, kept until you delete it. Give it a name you'll recognise, like \"Before the raid UI rework\".",
            "Save", $"Snapshot {DateTime.Now:d MMM}", Core.Backup.BackupStore.MaxSnapshotNameLength);
        if (name == null) return null;
        if (Core.Backup.BackupStore.CleanSnapshotName(name).Length == 0)
        {
            ThemedDialog.Warn(this, "Use letters or numbers in the name.", "That name can't be used");
            return null;
        }
        BackupInfo? snap = null;
        try { snap = _app.Service.SaveSnapshot(_worker, name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ThemedDialog.Warn(this, e.Message, "The snapshot couldn't be saved");
            return null;
        }
        if (snap == null) ThemedDialog.Info(this, "There are no settings to save yet. Play this game version once first.", "Nothing to save");
        Fill();
        return snap;
    }

    private void DeleteSnapshot(BackupInfo b)
    {
        if (!Quiet && !ThemedDialog.Ask(this, $"\"{b.Description}\" from {b.CreatedUtc.ToLocalTime():f} is deleted from this computer. Your settings and other backups aren't changed.",
                "Delete", "Cancel", "Delete this snapshot?"))
            return;
        try { _app.Service.DeleteSnapshot(b); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ThemedDialog.Warn(this, e.Message, "The snapshot couldn't be deleted");
        }
        Fill();
        UpdateButtons();
    }

    /// <summary>Asks where, then saves a copy of the backup as a zip there.</summary>
    private void SaveZip(BackupInfo b)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save a copy of this backup",
            Filter = "Zip file (*.zip)|*.zip",
            FileName = ZipName(b),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SaveZip(b, dialog.FileName);
            ThemedDialog.Info(this, $"Saved to {dialog.FileName}. To use it, restore it here, or unzip it into the game's WTF\\Account folder while the game is closed.", "Saved");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ThemedDialog.Warn(this, e.Message, "The zip couldn't be saved");
        }
    }

    internal static string ZipName(BackupInfo b) =>
        $"{ProductInfo.Id}-{b.VersionFolder.Trim('_')}-{b.CreatedUtc.ToLocalTime():yyyy-MM-dd-HHmm}{(b.Pinned ? "-" + b.SnapshotName!.Replace(' ', '-') : "")}.zip";

    /// <summary>Writes the zip (replacing a file of that name the player agreed to replace).</summary>
    internal static void SaveZip(BackupInfo b, string path)
    {
        var temp = path + ".partial";
        if (File.Exists(temp)) File.Delete(temp);
        System.IO.Compression.ZipFile.CreateFromDirectory(b.Path, temp, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(temp, path, overwrite: true);
    }

    internal static string When(BackupInfo b) =>
        $"{b.CreatedUtc.ToLocalTime():ddd d MMM, HH:mm}  ({StatusWords.Ago(b.CreatedUtc, DateTime.UtcNow)})";

    internal static string Holds(BackupInfo b) =>
        b.Partial ? $"{b.FileCount} changed file{(b.FileCount == 1 ? "" : "s")}" : $"whole folder, {b.FileCount} file{(b.FileCount == 1 ? "" : "s")}";

    private void Fill()
    {
        _list.Items.Clear();
        // Snapshots first (the ones the player made), then everything else newest first.
        var all = _app.Service.Backups.List(_worker.Version.FolderName);
        foreach (var b in all.Where(b => b.Pinned).Concat(all.Where(b => !b.Pinned)))
            _list.Items.Add(new ListViewItem([When(b), b.Description, Holds(b)]) { Tag = b, ForeColor = b.Pinned ? Theme.Accent : SystemColors.WindowText });
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
