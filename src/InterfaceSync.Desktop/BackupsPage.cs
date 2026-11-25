using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterfaceSync.Core;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Desktop;

/// <summary>
/// Backups and snapshots, one tab per game version. Restoring happens at once (a copy of the current settings is
/// saved first) with Undo in the note that follows; deleting a snapshot can be undone the same way. A snapshot is
/// named and saved right on the page.
/// </summary>
internal sealed class BackupsPage : Page
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 14) };
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBox _name = new() { MinWidth = 300, MaxLength = BackupStore.MaxSnapshotNameLength };
    private readonly TextBlock _sizeNote = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly HashSet<string> _pendingDelete = new(StringComparer.OrdinalIgnoreCase);
    private string? _version;
    private string _key = "";
    private List<BackupInfo> _shown = [];

    public BackupsPage(MainWindow shell) : base(shell)
    {
        _name.Watermark = L.T("Name, e.g. Before the raid UI rework");
        AutomationProperties.SetName(_name, L.T("Snapshot name"));
        var save = Ui.Button(L.T("Save snapshot"), () => SaveSnapshot(_name.Text ?? ""), ButtonKind.Primary, Ui.Upload);
        _name.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SaveSnapshot(_name.Text ?? ""); e.Handled = true; } };
        var snapRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _name, save } };
        Grid.SetColumn(save, 1);
        save.Margin = new Thickness(8, 0, 0, 0);
        var snapCard = Ui.Card(
            Ui.Text(L.T("Save a snapshot"), 14, FontWeight.SemiBold),
            Ui.Text(L.T("A copy of this game version's settings as they are now, kept until you delete it."), 12, brush: "InterfaceSyncMuted"),
            snapRow);

        var open = Ui.Button(L.T("Open folder"), () => Actions.OpenFolder(Shell, Service.Paths.Backups), ButtonKind.Ghost, Ui.Folder);
        var howText = new StackPanel { Spacing = 3, Children =
        {
            Ui.Text(L.T("How backups work"), 13.5, FontWeight.SemiBold),
            Ui.Text(L.F("Before anything on this computer or in the cloud is replaced, the files are copied to a backup first. The newest {0} are kept per game version, plus your snapshots (kept until you delete them) and a copy made before each of the last {1} game updates.",
                BackupStore.KeepPerVersion, BackupStore.KeepGameUpdates), 12, brush: "InterfaceSyncMuted"),
            _sizeNote,
        } };
        var how = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { howText, open } };
        Grid.SetColumn(open, 1);
        open.VerticalAlignment = VerticalAlignment.Top;

        Content = Scroll(new StackPanel { Children =
        {
            Heading(L.T("Backups"), L.T("Put a game version's settings back as they were, or keep a snapshot of your setup. Close the game first.")),
            _tabs,
            snapCard,
            Brand.Section(L.T("Backups and snapshots")),
            _list,
            Ui.CardOf(how),
        } });
    }

    public override string Key => "Backups";

    public override void Arrive(object? argument)
    {
        if (argument is string v) { _version = v; _key = ""; }
    }

    private VersionSync? Worker => Service.Workers.FirstOrDefault(w => w.Version.FolderName.Equals(_version, StringComparison.OrdinalIgnoreCase));

    public override void Refresh()
    {
        var workers = Service.Workers;
        if (Worker == null) _version = workers.FirstOrDefault()?.Version.FolderName;
        var list = _version == null ? [] : Service.Backups.List(_version).Where(b => !_pendingDelete.Contains(b.Path)).ToList();
        var now = DateTime.UtcNow;
        var key = $"{string.Join(",", workers.Select(w => w.Version.FolderName))}|{_version}|{string.Join(",", list.Select(b => b.Path))}|{StatusWords.Ago(list.FirstOrDefault()?.CreatedUtc, now)}|{Look.IsDark}";
        if (key == _key) return;
        _key = key;

        _tabs.Children.Clear();
        foreach (var w in workers)
        {
            var name = w.Version.FolderName;
            var tab = Ui.Button(name, () => { _version = name; _key = ""; Refresh(); }, name == _version ? ButtonKind.Primary : ButtonKind.Secondary);
            AutomationProperties.SetItemStatus(tab, name == _version ? "selected" : "");
            _tabs.Children.Add(tab);
        }
        _tabs.IsVisible = workers.Count > 1;

        _list.Children.Clear();
        _shown = list.Where(b => b.Pinned).Concat(list.Where(b => !b.Pinned)).ToList();
        if (_version == null)
            _list.Children.Add(HomePage.EmptyState(Ui.History, L.T("No game version is synced yet"), L.T("Backups appear here once a game version syncs.")));
        else if (_shown.Count == 0)
            _list.Children.Add(HomePage.EmptyState(Ui.History, L.T("No backups yet"), L.T("One is made before every download or restore.")));
        foreach (var b in _shown) _list.Children.Add(Row(b, now));

        long bytes = 0;
        try { bytes = FolderScanner.AllFiles(new PhysicalFileSystem(), Service.Paths.Backups).Sum(f => f.Size); } catch (Exception) { }
        _sizeNote.Text = L.F("Backups use {0}.", FormatSize(bytes));
    }

    private Control Row(BackupInfo b, DateTime now)
    {
        var icon = Ui.Icon(b.Pinned ? Ui.Upload : b.GameUpdate ? Ui.Game : Ui.History, 18, b.Pinned ? "InterfaceSyncAccent" : "InterfaceSyncMuted");
        icon.Margin = new Thickness(0, 2, 14, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var text = new StackPanel { Spacing = 2, Children =
        {
            Ui.Text(b.Description, 13.5, FontWeight.SemiBold, b.Pinned ? "InterfaceSyncAccent" : null),
            Ui.Text($"{b.CreatedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", L.Culture)} · {StatusWords.Ago(b.CreatedUtc, now)} · {Holds(b)}", 12, brush: "InterfaceSyncMuted"),
        } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (b.Pinned || b.GameUpdate) buttons.Children.Add(Ui.IconButton(Ui.Trash, L.T("Delete"), () => Delete(b)));
        buttons.Children.Add(Ui.IconButton(Ui.Zip, L.T("Save as zip…"), () => _ = SaveZip(b)));
        buttons.Children.Add(Ui.Button(L.T("Restore"), () => Restore(b), ButtonKind.Secondary, Ui.History));
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { icon, text, buttons } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        var card = Ui.CardOf(g);
        card.Margin = new Thickness(0);
        AutomationProperties.SetName(card, $"{b.Description}, {b.CreatedUtc.ToLocalTime():f}");
        return card;
    }

    internal static string Holds(BackupInfo b) =>
        b.Partial ? L.N(b.FileCount, "{0} changed file", "{0} changed files") : L.N(b.FileCount, "whole folder, {0} file", "whole folder, {0} files");

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // ------------------------------------------------------------------ actions

    /// <summary>Saves the current settings under a name (also used by tests).</summary>
    internal BackupInfo? SaveSnapshot(string name)
    {
        if (Worker is not { } worker) return null;
        if (BackupStore.CleanSnapshotName(name).Length == 0)
        {
            Shell.ShowToast(L.T("Give the snapshot a name with letters or numbers."));
            return null;
        }
        BackupInfo? snap = null;
        try { snap = Service.SaveSnapshot(worker, name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Shell.ShowToast(L.F("The snapshot couldn't be saved: {0}", e.Message));
            return null;
        }
        if (snap == null) { Shell.ShowToast(L.T("There are no settings to save yet. Play this game version once first.")); return null; }
        _name.Text = "";
        _key = "";
        Refresh();
        Shell.ShowToast(L.F("Saved the snapshot \"{0}\"", snap.SnapshotName ?? name));
        return snap;
    }

    /// <summary>Restores at once (after saving a copy of the current settings) and offers Undo.</summary>
    internal void Restore(BackupInfo backup)
    {
        if (Worker is not { } worker) return;
        (SyncOutcome Outcome, BackupInfo? Undo) result = default;
        Host.Run(() => result = Service.RestoreWithUndo(worker, backup), () =>
        {
            LastOutcome = result.Outcome;
            _key = "";
            Shell.Refresh();
            bool ok = result.Outcome is SyncOutcome.Pushed or SyncOutcome.NothingToDo or SyncOutcome.Waiting;
            if (!ok)
            {
                Shell.ShowToast(L.F("The settings weren't restored: {0}", worker.State.LastError ?? StatusWords.StatusText(worker)), seconds: 10);
                return;
            }
            var when = backup.CreatedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", L.Culture);
            var undo = result.Undo;
            Shell.ShowToast(L.F("{0} is back as it was on {1}.", worker.Version.FolderName, when), undo == null ? null : () =>
                Host.Run(() => Service.Restore(worker, undo), () => { _key = ""; Shell.Refresh(); Shell.ShowToast(L.T("Restore undone.")); }), seconds: 10);
        });
    }

    internal SyncOutcome? LastOutcome { get; private set; }

    /// <summary>Hides the snapshot at once; it's really deleted when the note goes away without Undo.</summary>
    private void Delete(BackupInfo b)
    {
        _pendingDelete.Add(b.Path);
        _key = "";
        Refresh();
        Shell.ShowToast(L.F("Deleted \"{0}\"", b.Description),
            undo: () => { _pendingDelete.Remove(b.Path); _key = ""; Refresh(); },
            expired: () =>
            {
                _pendingDelete.Remove(b.Path);
                try { Service.DeleteSnapshot(b); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Shell.ShowToast(L.F("The snapshot couldn't be deleted: {0}", e.Message));
                }
                _key = "";
                Refresh();
            });
    }

    private async Task SaveZip(BackupInfo b)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Save a copy of this backup"),
            SuggestedFileName = ZipName(b),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(L.T("Zip file")) { Patterns = ["*.zip"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            SaveZip(b, path);
            Shell.ShowToast(L.F("Saved to {0}", path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Shell.ShowToast(L.F("The zip couldn't be saved: {0}", e.Message));
        }
    }

    internal static string ZipName(BackupInfo b) =>
        $"{ProductInfo.Id}-{b.VersionFolder.Trim('_')}-{b.CreatedUtc.ToLocalTime():yyyy-MM-dd-HHmm}{(b.Pinned ? "-" + b.SnapshotName!.Replace(' ', '-') : "")}.zip";

    internal static void SaveZip(BackupInfo b, string path)
    {
        var temp = path + ".partial";
        if (File.Exists(temp)) File.Delete(temp);
        System.IO.Compression.ZipFile.CreateFromDirectory(b.Path, temp, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>For tests.</summary>
    internal IReadOnlyList<BackupInfo> Shown => _shown;
    internal void DeleteForTest(BackupInfo b) => Delete(b);
}
