using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterfaceSync.Core;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Mac;

/// <summary>
/// A game version's backups and snapshots: pick one (when, why, what it holds), then Restore. Also saves a named
/// snapshot, deletes a snapshot and saves any backup as a zip. The current settings are backed up before a restore,
/// so a restore can itself be undone from this list.
/// </summary>
internal sealed class RestoreWindow : Window
{
    private readonly IMacHost _host;
    private readonly VersionSync _worker;
    private readonly ListBox _list = new() { MinHeight = 220, MaxHeight = 320 };
    private readonly Button _restore, _delete, _zip;
    private readonly Action? _done;
    private List<BackupInfo> _backups = [];

    public RestoreWindow(IMacHost host, VersionSync worker, Action? done = null)
    {
        _host = host;
        _worker = worker;
        _done = done;
        Title = $"{worker.Version.FolderName}: backups and snapshots";
        Icon = MacIcons.App;
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _restore = Ui.Button("Restore", () => Restore(done), ButtonKind.Primary, Ui.Sync);
        _restore.IsEnabled = false;
        _delete = Ui.Button("Delete", () => { if (Selected is { } b) DeleteSnapshot(b); }, ButtonKind.Ghost);
        _zip = Ui.Button("Save as zip…", () => { if (Selected is { } b) _ = SaveZip(b); }, ButtonKind.Ghost);
        _delete.IsEnabled = _zip.IsEnabled = false;
        _list.SelectionChanged += (_, _) =>
        {
            var b = Selected;
            _restore.IsEnabled = _zip.IsEnabled = b != null;
            _delete.IsEnabled = b is { Pinned: true } or { GameUpdate: true };
        };
        var cancel = Ui.Button("Close", Close, ButtonKind.Ghost);
        var snapshot = Ui.Button("Save snapshot…", AskSnapshot, ButtonKind.Secondary, Ui.Upload);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { snapshot, _zip, _delete } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, _restore } };
        Grid.SetColumn(right, 1);
        bottom.Children.Add(left);
        bottom.Children.Add(right);

        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 6, Children =
        {
            Ui.Text($"{worker.Version.FolderName}: backups and snapshots", 20, FontWeight.SemiBold),
            Ui.Text("Pick a backup to put this game version's settings back as they were at that time. Quit the game first. " +
                    "Your current settings are backed up before anything changes, so you can undo a restore from this list. " +
                    "Save a snapshot to keep your current setup under a name until you delete it.", 12.5, brush: "InterfaceSyncMuted", bottom: 8),
            _list,
            bottom,
        } };
        Content = root;
        Fill();
    }

    private void Fill()
    {
        var all = _host.Service.Backups.List(_worker.Version.FolderName);
        _backups = all.Where(b => b.Pinned).Concat(all.Where(b => !b.Pinned)).ToList();   // the player's snapshots first
        var items = _backups.Select(b => (object)new StackPanel { Spacing = 1, Margin = new Thickness(2, 4), Children =
        {
            Ui.Text($"{b.CreatedUtc.ToLocalTime():ddd d MMM, HH:mm}  ·  {StatusWords.Ago(b.CreatedUtc, DateTime.UtcNow)}", 13, FontWeight.SemiBold, b.Pinned ? "InterfaceSyncAccent" : null),
            Ui.Text($"{b.Description} · {(b.Partial ? $"{b.FileCount} changed file{(b.FileCount == 1 ? "" : "s")}" : $"whole folder, {b.FileCount} files")}", 12, brush: "InterfaceSyncMuted"),
        } }).ToList();
        if (items.Count == 0) items.Add(Ui.Text("No backups yet. One is made before every download or restore.", 12.5, brush: "InterfaceSyncMuted"));
        _list.ItemsSource = items;
    }

    private BackupInfo? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _backups.Count ? _backups[_list.SelectedIndex] : null;

    private void AskSnapshot() =>
        new InputWindow("Save a snapshot",
            $"A copy of {_worker.Version.FolderName}'s settings as they are now, kept until you delete it. Give it a name you'll recognise, like \"Before the raid UI rework\".",
            "Save", $"Snapshot {DateTime.Now:d MMM}", name => SaveSnapshot(name)).Show();

    /// <summary>Saves the current settings under a name (also used by tests).</summary>
    internal BackupInfo? SaveSnapshot(string name)
    {
        if (BackupStore.CleanSnapshotName(name).Length == 0)
        {
            Dialogs.Message("Use letters or numbers in the name.", "That name can't be used", warning: true).Show();
            return null;
        }
        BackupInfo? snap = null;
        try { snap = _host.Service.SaveSnapshot(_worker, name); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Dialogs.Message(e.Message, "The snapshot couldn't be saved", warning: true).Show();
        }
        Fill();
        _done?.Invoke();
        return snap;
    }

    private void DeleteSnapshot(BackupInfo b) =>
        MacActions.Ask("Delete this snapshot?", $"\"{b.Description}\" from {b.CreatedUtc.ToLocalTime():f} is deleted from this Mac. Your settings and other backups aren't changed.", "Delete", () =>
        {
            try { _host.Service.DeleteSnapshot(b); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Dialogs.Message(e.Message, "The snapshot couldn't be deleted", warning: true).Show();
            }
            Fill();
            _done?.Invoke();
        });

    private async Task SaveZip(BackupInfo b)
    {
        if (StorageProvider is not { CanSave: true } storage) return;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save a copy of this backup",
            SuggestedFileName = ZipName(b),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Zip file") { Patterns = ["*.zip"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            SaveZip(b, path);
            Dialogs.Message($"Saved to {path}. To use it, restore it here, or unzip it into the game's WTF/Account folder while the game is closed.", "Saved").Show();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialogs.Message(e.Message, "The zip couldn't be saved", warning: true).Show();
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
    internal int BackupCount => _backups.Count;
    internal void Choose(int index) => _list.SelectedIndex = index;
    internal SyncOutcome? LastOutcome { get; private set; }

    private void Restore(Action? done)
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _backups.Count) return;
        var backup = _backups[_list.SelectedIndex];
        _restore.IsEnabled = false;
        SyncOutcome? outcome = null;
        _host.Run(() => outcome = _host.Service.Restore(_worker, backup), () =>
        {
            LastOutcome = outcome;
            bool ok = outcome is SyncOutcome.Pushed or SyncOutcome.NothingToDo or SyncOutcome.Waiting;
            Dialogs.Message(ok
                ? $"Restored. {_worker.Version.FolderName}'s settings are back as they were on {backup.CreatedUtc.ToLocalTime():f}" +
                  (outcome == SyncOutcome.Pushed ? " and were uploaded to your cloud folder." : ".")
                : $"The settings weren't restored: {_worker.State.LastError ?? _worker.StatusText}").Show();
            done?.Invoke();
            if (ok) Close(); else { Fill(); }
        });
    }

    /// <summary>For tests: restore without the list.</summary>
    internal void RestoreAt(int index) { Choose(index); Restore(null); }
}
