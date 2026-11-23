using InterfaceSync.Core;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.App;

internal enum DialogKind { Info, Warning, Error, Question }

/// <summary>
/// A message window in the app's own look (instead of Windows' plain message box): an icon, an optional heading,
/// the message, and rounded buttons. Follows the light/dark theme.
/// </summary>
internal sealed class ThemedDialog : Form
{
    /// <summary>Tests: answer every dialog with its default button instead of showing it.</summary>
    internal static bool AutoAnswer { get; set; }

    private ThemedDialog(string message, string? heading, DialogKind kind, (string text, DialogResult result, ButtonKind style)[] buttons)
    {
        Text = ProductInfo.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;

        var (glyph, role) = kind switch
        {
            DialogKind.Warning => (Draw.Warning, TextRole.Warning),
            DialogKind.Error => (Draw.Warning, TextRole.Error),
            DialogKind.Question => (Draw.Info, TextRole.Accent),
            _ => (Draw.Info, TextRole.Accent),
        };
        var icon = new Glyph(glyph, 22f) { Margin = new Padding(0, 2, 16, 0) };
        Theme.SetRole(icon, role);

        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        if (heading != null)
            text.Controls.Add(new Label { Text = heading, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(440, 0), Font = new Font(Font.FontFamily, 11.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) });
        text.Controls.Add(new Label { Text = message, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(440, 0), Margin = Padding.Empty });

        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
        ModernButton? first = null;
        foreach (var (label, result, style) in buttons.Reverse())
        {
            var b = new ModernButton(label, style) { DialogResult = result, Margin = new Padding(8, 0, 0, 0) };
            row.Controls.Add(b);
            first ??= b;
            if (style == ButtonKind.Primary) AcceptButton = b;
            if (result is DialogResult.Cancel or DialogResult.No) CancelButton = b;
        }
        AcceptButton ??= first;
        CancelButton ??= first;

        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 22, 24, 18) };
        grid.Controls.Add(icon, 0, 0);
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(row, 0, 1);
        grid.SetColumnSpan(row, 2);
        Controls.Add(grid);
        Theme.Apply(this);
    }

    public static DialogResult Show(IWin32Window? owner, string message, string? heading = null, DialogKind kind = DialogKind.Info,
        params (string text, DialogResult result, ButtonKind style)[] buttons)
    {
        if (buttons.Length == 0) buttons = [("OK", DialogResult.OK, ButtonKind.Primary)];
        if (AutoAnswer) return buttons.FirstOrDefault(b => b.style == ButtonKind.Primary).result is var r && r != DialogResult.None ? r : buttons[0].result;
        using var dialog = new ThemedDialog(message, heading, kind, buttons);
        if (owner == null) dialog.StartPosition = FormStartPosition.CenterScreen;
        return dialog.ShowDialog(owner);
    }

    /// <summary>For screenshots: the window without showing it modally.</summary>
    internal static Form Create(string message, string? heading, DialogKind kind, params (string text, DialogResult result, ButtonKind style)[] buttons) =>
        new ThemedDialog(message, heading, kind, buttons.Length > 0 ? buttons : [("OK", DialogResult.OK, ButtonKind.Primary)]);

    public static void Info(IWin32Window? owner, string message, string? heading = null) => Show(owner, message, heading, DialogKind.Info);
    public static void Warn(IWin32Window? owner, string message, string? heading = null) => Show(owner, message, heading, DialogKind.Warning);

    /// <summary>A yes/no question; true for <paramref name="yes"/>.</summary>
    public static bool Ask(IWin32Window? owner, string message, string yes, string no = "Cancel", string? heading = null) =>
        Show(owner, message, heading, DialogKind.Question, (no, DialogResult.Cancel, ButtonKind.Ghost), (yes, DialogResult.OK, ButtonKind.Primary)) == DialogResult.OK;
}

/// <summary>About: the banner, what it is, the notices and the version.</summary>
internal sealed class AboutDialog : Form
{
    public AboutDialog()
    {
        Text = $"About {ProductInfo.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;
        ClientSize = new Size(540, 420);

        var body = new Label { Text = StatusForm.AboutText, AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false, Padding = new Padding(24, 16, 24, 8) };
        var ok = new ModernButton("OK", ButtonKind.Primary) { DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(24, 10, 24, 14) };
        ok.Dock = DockStyle.Right;
        footer.Controls.Add(ok);
        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(new BrandBanner($"Version {ProductInfo.DisplayVersion}"));
        AcceptButton = CancelButton = ok;
        Theme.Apply(this);
    }

    public static void ShowFor(IWin32Window? owner)
    {
        if (ThemedDialog.AutoAnswer) return;
        using var d = new AboutDialog();
        if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
        d.ShowDialog(owner);
    }
}

/// <summary>
/// "Settings changed in two places": this PC's copy and the cloud copy side by side (which computer, when each last
/// changed, the newer one marked), each with its own Keep button. The other copy is backed up either way.
/// </summary>
internal sealed class ConflictDialog : Form
{
    public KeepChoice? Choice { get; private set; }

    public ConflictDialog(string version, ConflictInfo c)
    {
        Text = $"{ProductInfo.Name}: choose which settings to keep";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? Font;
        Icon = TrayIcons.AppIcon;
        ClientSize = new Size(760, 470);

        var heading = new Label { Text = $"{version}: settings changed in two places", AutoSize = true, UseMnemonic = false, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        var reason = new Label { Text = c.Reason, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 14) };
        Theme.SetRole(reason, TextRole.Muted);

        var mine = Side(Draw.Computer, c.LocalMachine != null ? $"This PC ({c.LocalMachine})" : "This PC", c.LocalNewestUtc,
            c.Suggested == KeepChoice.Local, "Keep this PC's settings", KeepChoice.Local,
            "Uses the settings on this PC and uploads them, replacing the cloud copy.");
        var cloud = Side(Draw.Cloud, c.CloudPushedBy != null ? $"Cloud copy (from {c.CloudPushedBy})" : "Cloud copy", c.CloudNewestUtc,
            c.Suggested == KeepChoice.Cloud, "Keep the cloud settings", KeepChoice.Cloud,
            "Downloads the cloud copy, replacing this PC's settings.");

        var sides = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
        sides.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sides.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        mine.Margin = new Padding(0, 0, 8, 0);
        cloud.Margin = new Padding(8, 0, 0, 0);
        sides.Controls.Add(mine, 0, 0);
        sides.Controls.Add(cloud, 1, 0);

        var note = new Label
        {
            Text = "The copy you don't keep is saved as a backup, so you can get it back any time with Restore… on this game version.",
            AutoSize = true, UseMnemonic = false, MaximumSize = new Size(560, 0), Anchor = AnchorStyles.Left,
        };
        Theme.SetRole(note, TextRole.Muted);
        var cancel = new ModernButton("Decide later", ButtonKind.Ghost) { DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
        var footer = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(note, 0, 0);
        footer.Controls.Add(cancel, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 20, 24, 18) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(reason, 0, 1);
        layout.Controls.Add(sides, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        CancelButton = cancel;
        Theme.Apply(this);
    }

    private Card Side(string glyph, string title, DateTime? changedUtc, bool newer, string keepText, KeepChoice choice, string effect)
    {
        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 16), Highlight = newer ? Brand.C(ThemePalette.Banner.Gold) : null };
        var icon = new Glyph(glyph, 22f) { Margin = new Padding(0, 0, 0, 8) };
        Theme.SetRole(icon, TextRole.Accent);
        var name = new Label { Text = title, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(300, 0), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        var when = new Label
        {
            Text = changedUtc is { } t ? $"Last changed {t.ToLocalTime():ddd d MMM, HH:mm}\n({StatusWords.Ago(t, DateTime.UtcNow)})" : "Last changed: unknown",
            AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 8),
        };
        var badge = new StatusPill { Margin = new Padding(0, 0, 0, 8), Visible = newer };
        badge.Set("Newer", Brand.C(ThemePalette.Banner.Gold));
        var what = new Label { Text = effect, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(300, 0), Margin = new Padding(0, 0, 0, 12) };
        Theme.SetRole(what, TextRole.Muted);
        var keep = new ModernButton(keepText, newer ? ButtonKind.Primary : ButtonKind.Secondary);
        keep.Click += (_, _) => { Choice = choice; DialogResult = DialogResult.OK; Close(); };
        if (newer) AcceptButton = keep;

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        stack.Controls.AddRange([icon, name, badge, when, what, keep]);
        card.Controls.Add(stack);
        return card;
    }

    /// <summary>Shows the choice; null when the player decides later.</summary>
    public static KeepChoice? Ask(IWin32Window? owner, string version, ConflictInfo c)
    {
        using var d = new ConflictDialog(version, c);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Choice : null;
    }
}
