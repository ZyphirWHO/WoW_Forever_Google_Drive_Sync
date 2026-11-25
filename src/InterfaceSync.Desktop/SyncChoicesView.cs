using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Desktop;

/// <summary>
/// "Choose what syncs" for one game version, in the side panel: the characters and addons, ticked when they sync,
/// with a filter for long addon lists. What's unticked is left alone on this computer and in the cloud. Addons that
/// aren't installed here are left out by default (a switch), listed on their own, and their leftover settings on this
/// computer can be cleaned up (backed up first, with Undo).
/// </summary>
internal sealed class SyncChoicesView : UserControl
{
    private readonly List<(CheckBox Box, string Key)> _characters = [];
    private readonly List<(CheckBox Box, string Name)> _addons = [];
    private readonly TextBox _filter = new();
    private readonly ToggleSwitch _leaveOut = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly List<string> _notInstalled = [];

    public SyncChoicesView(SyncService service, VersionSync worker, Action<SyncRules, bool> save, Action cancel, Action<string>? cleanUp = null)
    {
        var current = service.RulesFor(worker.Version.FolderName);
        var choices = service.ChoicesFor(worker);
        _leaveOut.IsChecked = service.LeavesOutUninstalledAddons(worker.Version.FolderName);
        _leaveOut.IsEnabled = choices.Installed != null;
        AutomationProperties.SetName(_leaveOut, L.T("Leave out addons that aren't installed"));

        Control Section(string heading, IEnumerable<(string key, string label, bool on)> items, bool characters, string? empty)
        {
            var list = new StackPanel { Spacing = 0 };
            foreach (var (key, label, on) in items)
            {
                var box = new CheckBox { Content = label, IsChecked = on, Padding = new Thickness(6, 2) };
                AutomationProperties.SetName(box, label);
                list.Children.Add(box);
                if (characters) _characters.Add((box, key)); else _addons.Add((box, key));
            }
            if (list.Children.Count == 0 && empty != null) list.Children.Add(Ui.Text(empty, 12.5, brush: "InterfaceSyncMuted"));
            var all = Ui.Button(L.T("All"), () => Set(characters, true), ButtonKind.Ghost);
            var none = Ui.Button(L.T("None"), () => Set(characters, false), ButtonKind.Ghost);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 4), Children = { Ui.Text(heading, 14, FontWeight.SemiBold), all, none } };
            Grid.SetColumn(all, 1);
            Grid.SetColumn(none, 2);
            ((Control)head.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            return new StackPanel { Children = { head, list } };
        }

        _filter.Watermark = L.T("Find a character or addon");
        AutomationProperties.SetName(_filter, L.T("Find a character or addon"));
        _filter.TextChanged += (_, _) => ApplyFilter();

        var body = new StackPanel { Children =
        {
            Ui.Text(Os.IsMac
                ? L.T("Ticked characters and addons sync on this Mac. Untick one to keep this Mac's own settings for it: they aren't uploaded or replaced, and the cloud copy stays as it is for your other computers. Account-wide settings (keybindings, macros) always sync. Ticking one again downloads the cloud's copy, after backing up this Mac's.")
                : L.T("Ticked characters and addons sync on this computer. Untick one to keep this computer's own settings for it: they aren't uploaded or replaced, and the cloud copy stays as it is for your other computers. Account-wide settings (keybindings, macros) always sync. Ticking one again downloads the cloud's copy, after backing up this computer's."),
                12.5, brush: "InterfaceSyncMuted", bottom: 10),
            _filter,
            Section(L.T("Characters"), choices.Characters.Select(c => (c.Key, c.Label, !current.Characters.Contains(c.Key, StringComparer.OrdinalIgnoreCase))), true,
                L.T("No characters yet: log in with one first.")),
            LeaveOutRow(),
            Section(L.T("Addons"), choices.Addons.Where(a => choices.IsInstalled(a) || _leaveOut.IsChecked != true)
                .Select(a => (a, choices.IsInstalled(a) ? a : L.F("{0} (not installed)", a), !current.Addons.Contains(a, StringComparer.OrdinalIgnoreCase))), false,
                L.T("No addon settings yet.")),
        } };
        foreach (var a in choices.Addons.Where(a => !choices.IsInstalled(a))) _notInstalled.Add(a);
        if (_leaveOut.IsChecked == true && _notInstalled.Count > 0) body.Children.Add(NotInstalledSection(service, worker, cleanUp));

        var ok = Ui.Button(L.T("Save"), () => save(Rules, _leaveOut.IsChecked == true), ButtonKind.Primary);
        ok.IsDefault = true;
        var no = Ui.Button(L.T("Cancel"), cancel, ButtonKind.Ghost);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { no, ok } };
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = body });
        Content = dock;
    }

    /// <summary>The switch: leave out the settings of addons that aren't installed on this computer.</summary>
    private Control LeaveOutRow()
    {
        var text = new StackPanel { Spacing = 2, Children =
        {
            Ui.Text(L.T("Leave out addons that aren't installed"), 13.5, FontWeight.SemiBold),
            Ui.Text(_leaveOut.IsEnabled
                ? L.T("Settings of addons you've removed aren't downloaded or uploaded on this computer. Your other computers keep theirs. Install the addon again and its settings come back.")
                : L.T("The AddOns folder couldn't be read, so every addon's settings sync."), 12, brush: "InterfaceSyncMuted"),
        } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 0), Children = { text, _leaveOut } };
        Grid.SetColumn(_leaveOut, 1);
        _leaveOut.VerticalAlignment = VerticalAlignment.Center;
        _leaveOut.Margin = new Thickness(12, 0, 0, 0);
        return g;
    }

    /// <summary>Addons with settings that aren't installed here, and the clean-up of this computer's copies.</summary>
    private Control NotInstalledSection(SyncService service, VersionSync worker, Action<string>? cleanUp)
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(Ui.Text(L.T("Not installed on this computer"), 14, FontWeight.SemiBold));
        stack.Children.Add(Ui.Text(string.Join(", ", _notInstalled), 12.5, brush: "InterfaceSyncMuted"));
        var local = service.UninstalledAddonDataFor(worker);
        if (local.FileCount > 0 && cleanUp != null)
        {
            var size = local.Bytes < 1024 * 1024 ? $"{Math.Max(1, local.Bytes / 1024)} KB" : $"{local.Bytes / (1024.0 * 1024):0.#} MB";
            var clean = Ui.Button(L.T("Clean up"), () => cleanUp(size), ButtonKind.Secondary, Ui.Trash);
            var info = new StackPanel { Spacing = 2, Children =
            {
                Ui.Text(L.F("Their settings use {0} on this computer", size), 13, FontWeight.SemiBold),
                Ui.Text(L.T("They do nothing here and make backups bigger. Clean up removes this computer's copies (backed up first, so you can undo it); the cloud copies stay for your other computers."), 12, brush: "InterfaceSyncMuted"),
            } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { info, clean } };
            Grid.SetColumn(clean, 1);
            clean.VerticalAlignment = VerticalAlignment.Center;
            clean.Margin = new Thickness(12, 0, 0, 0);
            var card = Ui.CardOf(row);
            card.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(card);
            CleanUpOffered = true;
        }
        return stack;
    }

    internal bool CleanUpOffered { get; private set; }
    internal IReadOnlyList<string> NotInstalled => _notInstalled;
    internal bool LeaveOutUninstalled { get => _leaveOut.IsChecked == true; set => _leaveOut.IsChecked = value; }

    private void Set(bool characters, bool on)
    {
        foreach (var box in characters ? _characters.Select(c => c.Box) : _addons.Select(a => a.Box))
            if (box.IsVisible) box.IsChecked = on;
    }

    private void ApplyFilter()
    {
        var f = (_filter.Text ?? "").Trim();
        foreach (var box in _characters.Select(c => c.Box).Concat(_addons.Select(a => a.Box)))
            box.IsVisible = f.Length == 0 || ((string?)box.Content ?? "").Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What's unticked.</summary>
    public SyncRules Rules => new()
    {
        Characters = _characters.Where(c => c.Box.IsChecked != true).Select(c => c.Key).ToList(),
        Addons = _addons.Where(a => a.Box.IsChecked != true).Select(a => a.Name).ToList(),
    };

    internal IReadOnlyList<string> CharacterNames => _characters.Select(c => (string)c.Box.Content!).ToList();
    internal IReadOnlyList<string> AddonNames => _addons.Select(a => a.Name).ToList();
    internal void SetCharacter(int i, bool on) => _characters[i].Box.IsChecked = on;
    internal void SetAddon(int i, bool on) => _addons[i].Box.IsChecked = on;
    internal void Filter(string text) => _filter.Text = text;
}
