using WoWSync.Core;
using WoWSync.Core.Abstractions;
using WoWSync.Core.Detection;
using WoWSync.Core.Service;
using WoWSync.Core.Sync;

namespace WoWSync.App;

/// <summary>
/// First run: shows what was found, lets the player change any path and pick game versions, then starts syncing.
/// No typing needed when detection works.
/// </summary>
internal sealed class FirstRun : Form
{
    private readonly SyncService _service;
    private readonly IRegistry _registry;
    private readonly Label _wowPath = PathLabel();
    private readonly Label _drivePath = PathLabel();
    private readonly Label _cloudPath = PathLabel();
    private readonly CheckedListBox _versions = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false, Height = 110 };
    private readonly Label _notes = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(140, 70, 0) };
    private readonly Button _start = new() { Text = "Start syncing", AutoSize = true };

    private FirstRun(SyncService service, IRegistry registry)
    {
        _service = service;
        _registry = registry;
        Text = "WoWSync setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label { Text = "WoWSync found these on this PC. Check them, then start syncing.", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 0)!, 3);

        AddRow(grid, 1, "World of Warcraft:", _wowPath, ChangeButton(ChooseWow));
        grid.Controls.Add(new Label { Text = "Sync these game versions:", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 2);
        grid.Controls.Add(_versions, 1, 2);
        AddRow(grid, 3, "Google Drive:", _drivePath, ChangeButton(ChooseDrive));
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
        AcceptButton = _start;
        CancelButton = cancel;
        _versions.ItemCheck += (_, _) => BeginInvoke(UpdateStartButton);
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
        _drivePath.Text = drive is { Found: true } ? $"{drive.MyDrivePath}  ({(drive.Mode == DriveMode.Streaming ? "streaming" : "mirrored")})" : "Not found: choose your \"My Drive\" folder";
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
            notes.Add("Google Drive is streaming files. In File Explorer, right-click My Drive\\WoWSync and choose Offline access → Available offline.");
        if (LegacyV11.FindRunEntries(_registry) is { Count: > 0 } legacy)
            notes.Add($"WoWSync 1.1 is still set to start at login ({string.Join(", ", legacy)}). Stop the 1.1 agent before syncing with 2.0.");
        notes.Add("On this PC's first sync, if Google Drive already has settings for a game version, they replace this PC's. A backup of this PC's settings is kept and can be restored.");
        _notes.Text = string.Join(Environment.NewLine + Environment.NewLine, notes);
        UpdateStartButton();
    }

    private void UpdateStartButton() =>
        _start.Enabled = _service.Wow is { Found: true } && _service.Drive is { Found: true } && _versions.CheckedItems.Count > 0;

    private void ChooseWow()
    {
        var path = PickFolder("Choose your World of Warcraft folder (the one that contains _retail_ or _forever_).");
        if (path == null) return;
        _service.Settings.WowInstallOverride = path;
        _service.SaveSettings(_service.Settings);
        if (_service.Wow is not { Source: DetectionSource.UserOverride })
            MessageBox.Show(this, "That folder doesn't contain a World of Warcraft game version (such as _retail_ or _forever_).", "WoWSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Fill();
    }

    private void ChooseDrive()
    {
        var path = PickFolder("Choose your Google Drive \"My Drive\" folder.");
        if (path == null) return;
        _service.Settings.DriveOverride = path;
        _service.SaveSettings(_service.Settings);
        Fill();
    }

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
        _service.SaveSettings(settings);
        DialogResult = DialogResult.OK;
        Close();
    }

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
