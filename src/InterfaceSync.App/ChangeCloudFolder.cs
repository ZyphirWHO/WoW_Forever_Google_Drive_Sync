using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Service;

namespace InterfaceSync.App;

/// <summary>Moves this PC's syncing (and the cloud data, with a backup) to another cloud app.</summary>
internal sealed class ChangeCloudFolder : Form
{
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Label _where = new() { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 6, 0, 6) };
    private readonly CheckBox _keep = new() { AutoSize = true };
    private readonly Button _ok = new() { Text = "Move", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly IReadOnlyDictionary<CloudProvider, DriveDetectionResult> _found;

    public ChangeCloudFolder(SyncService service)
    {
        Text = $"{ProductInfo.Name}: change cloud folder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;
        _found = service.DetectAllProviders();
        var current = service.Drive?.Provider;

        foreach (var p in CloudProviders.All)
            _provider.Items.Add(_found[p].Found ? (p == current ? $"{p.DisplayName()} (in use now)" : p.DisplayName()) : $"{p.DisplayName()} (not found on this PC)");
        _provider.SelectedIndexChanged += (_, _) => Update();

        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        panel.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "Your settings are copied to the new app and this PC switches to it. The old copy is backed up and " +
                   "left in place, marked as moved, so your other PCs pause until you switch them too. Close the game first.",
        });
        panel.Controls.Add(_provider);
        panel.Controls.Add(_where);
        panel.Controls.Add(_keep);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel });
        buttons.Controls.Add(_ok);
        panel.Controls.Add(buttons);
        Controls.Add(panel);
        AcceptButton = _ok;

        _provider.SelectedIndex = Array.FindIndex(CloudProviders.All, p => p != current && _found[p].Found) is var i and >= 0 ? i : 0;
        Update();
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
    }

    public CloudProvider Chosen => CloudProviders.All[_provider.SelectedIndex];
    public bool KeepOnDevice => _keep.Visible && _keep.Checked;

    private void Update()
    {
        var r = _found[Chosen];
        _where.Text = r.Found ? $"Folder: {PathUtil.Join(r.MyDrivePath!, Core.Sync.CloudLayout.AppFolderName)}" : $"{Chosen.DisplayName()} isn't installed or signed in on this PC.";
        _keep.Visible = Chosen == CloudProvider.OneDrive && r.Found;
        _keep.Text = "Always keep that folder on this device (recommended)";
        _ok.Enabled = r.Found;
    }
}
