using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Mac;

/// <summary>
/// Restore a game version's settings from one of its backups: pick one (when, why, what it holds), then Restore.
/// The current settings are backed up first, so a restore can itself be undone from this list.
/// </summary>
internal sealed class RestoreWindow : Window
{
    private readonly IMacHost _host;
    private readonly VersionSync _worker;
    private readonly ListBox _list = new() { MinHeight = 220, MaxHeight = 320 };
    private readonly Button _restore;
    private List<BackupInfo> _backups = [];

    public RestoreWindow(IMacHost host, VersionSync worker, Action? done = null)
    {
        _host = host;
        _worker = worker;
        Title = $"Restore {worker.Version.FolderName}";
        Icon = MacIcons.App;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _restore = Ui.Button("Restore", () => Restore(done), ButtonKind.Primary, Ui.Sync);
        _restore.IsEnabled = false;
        _list.SelectionChanged += (_, _) => _restore.IsEnabled = _list.SelectedIndex >= 0 && _list.SelectedIndex < _backups.Count;
        var cancel = Ui.Button("Cancel", Close, ButtonKind.Ghost);

        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 6, Children =
        {
            Ui.Text($"Restore {worker.Version.FolderName} settings", 20, FontWeight.SemiBold),
            Ui.Text("Pick a backup to put this game version's settings back as they were at that time. Quit the game first. " +
                    "Your current settings are backed up before anything changes, so you can undo a restore from this list.", 12.5, brush: "InterfaceSyncMuted", bottom: 8),
            _list,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, _restore } },
        } };
        Content = root;
        Fill();
    }

    private void Fill()
    {
        _backups = _host.Service.Backups.List(_worker.Version.FolderName).ToList();
        var items = _backups.Select(b => (object)new StackPanel { Spacing = 1, Margin = new Thickness(2, 4), Children =
        {
            Ui.Text($"{b.CreatedUtc.ToLocalTime():ddd d MMM, HH:mm}  ·  {StatusWords.Ago(b.CreatedUtc, DateTime.UtcNow)}", 13, FontWeight.SemiBold),
            Ui.Text($"{b.Description} · {(b.Partial ? $"{b.FileCount} changed file{(b.FileCount == 1 ? "" : "s")}" : $"whole folder, {b.FileCount} files")}", 12, brush: "InterfaceSyncMuted"),
        } }).ToList();
        if (items.Count == 0) items.Add(Ui.Text("No backups yet. One is made before every download or restore.", 12.5, brush: "InterfaceSyncMuted"));
        _list.ItemsSource = items;
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
