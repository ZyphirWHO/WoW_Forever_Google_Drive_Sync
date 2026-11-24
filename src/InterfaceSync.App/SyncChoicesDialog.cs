using InterfaceSync.Core;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

using InterfaceSync.Core.Localization;

namespace InterfaceSync.App;

/// <summary>
/// "Choose what syncs" for one game version: the characters and addons, ticked when they sync. What's unticked is
/// left alone on this computer and in the cloud, so other computers keep syncing it.
/// </summary>
internal sealed class SyncChoicesDialog : Form
{
    private readonly CheckedListBox _characters = new() { CheckOnClick = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, AccessibleName = L.T("Characters that sync") };
    private readonly CheckedListBox _addons = new() { CheckOnClick = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, AccessibleName = L.T("Addons that sync") };
    private readonly List<string> _characterKeys = [];

    public SyncChoicesDialog(SyncService service, VersionSync worker)
    {
        Text = L.F("{0}: choose what syncs", worker.Version.FolderName);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 560);
        MinimumSize = new Size(560, 420);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var current = service.RulesFor(worker.Version.FolderName);
        var choices = service.ChoicesFor(worker);
        foreach (var (key, label) in choices.Characters)
        {
            _characterKeys.Add(key);
            _characters.Items.Add(label, !current.Characters.Contains(key, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var addon in choices.Addons)
            _addons.Items.Add(addon, !current.Addons.Contains(addon, StringComparer.OrdinalIgnoreCase));

        var title = Ui.Text(L.F("Choose what syncs: {0}", worker.Version.FolderName), bold: true, size: 14f);
        title.Margin = new Padding(0, 0, 0, 4);
        var note = Ui.Text(L.T("Ticked characters and addons sync on this computer. Untick one to keep this computer's own settings for it: they aren't uploaded or replaced, and the cloud copy stays as it is for your other computers. Account-wide settings (keybindings, macros) always sync. Ticking one again downloads the cloud's copy, after backing up this computer's."),
            TextRole.Muted);
        note.MaximumSize = new Size(700, 0);
        note.Margin = new Padding(0, 0, 0, 12);

        Control Column(string heading, CheckedListBox list, string empty)
        {
            var frame = new Card { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 6, 8), Margin = new Padding(0, 0, 8, 0) };
            if (list.Items.Count == 0)
            {
                var none = Ui.Text(empty, TextRole.Muted);
                none.Dock = DockStyle.Top;
                frame.Controls.Add(none);
            }
            else frame.Controls.Add(list);
            var head = Ui.Text(heading, bold: true);
            head.Margin = new Padding(0, 0, 0, 6);
            var all = new ModernButton(L.T("All"), ButtonKind.Ghost) { Height = 26, Margin = new Padding(8, 0, 0, 0) };
            all.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
            var col = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 0, 8, 0) };
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            col.Controls.Add(Ui.Row(head, all), 0, 0);
            col.Controls.Add(frame, 0, 1);
            return col;
        }

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.Controls.Add(Column(L.T("Characters"), _characters, L.T("No characters yet: log in with one first.")), 0, 0);
        columns.Controls.Add(Column(L.T("Addons"), _addons, L.T("No addon settings yet.")), 1, 0);

        var save = new ModernButton(L.T("Save"), ButtonKind.Primary) { DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new ModernButton(L.T("Cancel"), ButtonKind.Ghost) { DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([save, cancel]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 16, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(columns, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
    }

    /// <summary>What's unticked.</summary>
    public SyncRules Rules => new()
    {
        Characters = Enumerable.Range(0, _characters.Items.Count).Where(i => !_characters.GetItemChecked(i)).Select(i => _characterKeys[i]).ToList(),
        Addons = Enumerable.Range(0, _addons.Items.Count).Where(i => !_addons.GetItemChecked(i)).Select(i => (string)_addons.Items[i]).ToList(),
    };

    /// <summary>For tests.</summary>
    internal IReadOnlyList<string> CharacterNames => _characters.Items.Cast<string>().ToList();
    internal IReadOnlyList<string> AddonNames => _addons.Items.Cast<string>().ToList();
    internal void SetCharacter(int index, bool synced) => _characters.SetItemChecked(index, synced);
    internal void SetAddon(int index, bool synced) => _addons.SetItemChecked(index, synced);
}
