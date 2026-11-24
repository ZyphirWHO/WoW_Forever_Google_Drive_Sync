using System.Diagnostics;
using System.Drawing.Drawing2D;
using InterfaceSync.Core;

using InterfaceSync.Core.Localization;

namespace InterfaceSync.App;

/// <summary>Small helpers shared by the windows: display scaling, opening folders and links.</summary>
internal static class Ui
{
    /// <summary>Scale factor of the screen a control is on (1 at 100%, 1.5 at 150%).</summary>
    public static float Scale(Control c) => c.DeviceDpi / 96f;

    /// <summary>A size in pixels at 100%, scaled for the control's screen.</summary>
    public static int Px(Control c, int px) => (int)Math.Round(px * Scale(c));

    /// <summary>
    /// Makes a window scale with the display (150%, 200%): sizes are written for 100% and grown by WinForms when the
    /// window is created. Call at the end of the window's constructor.
    /// </summary>
    public static void ScaleWithDisplay(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96f, 96f);
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>A control created after its window was shown is scaled like the rest of the window.</summary>
    public static T Fit<T>(T control, Control host) where T : Control
    {
        if (host.FindForm() is not { IsHandleCreated: true }) return control;   // the window scales it when it opens
        var f = Scale(host);
        if (Math.Abs(f - 1f) > 0.01f) control.Scale(new SizeF(f, f));
        return control;
    }

    public static void OpenFolder(IWin32Window? owner, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ThemedDialog.Warn(owner, $"Couldn't open {folder}: {e.Message}");
        }
    }

    public static void OpenLink(Uri page)
    {
        try { Process.Start(new ProcessStartInfo(page.ToString()) { UseShellExecute = true }); } catch (Exception) { }
    }

    public static Label Text(string text, TextRole role = TextRole.Normal, bool bold = false, float? size = null)
    {
        var l = new Label { Text = text, AutoSize = true, UseMnemonic = false, Margin = Padding.Empty };
        var font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        if (bold || size != null) l.Font = new Font(font.FontFamily, size ?? font.Size, bold ? FontStyle.Bold : FontStyle.Regular);
        if (role != TextRole.Normal) Theme.SetRole(l, role);
        return l;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    public static FlowLayoutPanel Column(params Control[] controls)
    {
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        col.Controls.AddRange(controls);
        return col;
    }
}

/// <summary>
/// One setting: a title and a muted description on the left, its control(s) on the right, on a card.
/// The description wraps to the card's width.
/// </summary>
internal sealed class SettingRow : Card
{
    private readonly Label _title;
    private readonly Label _description;
    private readonly FlowLayoutPanel _controls;
    private readonly Glyph? _icon;

    public SettingRow(string title, string? description, string? glyph = null, params Control[] controls)
    {
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(0, 0, 0, 8);
        _title = Ui.Text(title, bold: true);
        _title.Margin = new Padding(0, 0, 0, 3);
        _description = Ui.Text(description ?? "", TextRole.Muted);
        _description.Visible = description != null;
        _controls = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(12, 0, 0, 0), Anchor = AnchorStyles.Right };
        _controls.Controls.AddRange(controls);
        AccessibleName = title;
        AccessibleDescription = description;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, AutoSize = false };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (glyph != null)
        {
            _icon = new Glyph(glyph, 14f) { Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 1, 12, 0) };
            Theme.SetRole(_icon, TextRole.Accent);
            grid.Controls.Add(_icon, 0, 0);
        }
        var text = Ui.Column(_title, _description);
        text.Anchor = AnchorStyles.Left;
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(_controls, 2, 0);
        Controls.Add(grid);
    }

    public string Title { get => _title.Text; set => _title.Text = value; }

    public string? Description
    {
        get => _description.Visible ? _description.Text : null;
        set { _description.Text = value ?? ""; _description.Visible = value != null; Fit(Width); }
    }

    public FlowLayoutPanel Controls2 => _controls;

    /// <summary>Sets the width and grows the height to fit the wrapped description.</summary>
    public void Fit(int width)
    {
        Width = width;
        int controlsWidth = _controls.Controls.Count > 0 ? _controls.PreferredSize.Width + 12 : 0;
        int iconWidth = _icon != null ? _icon.PreferredSize.Width + 12 : 0;
        int textWidth = Math.Max(120, width - Padding.Horizontal - controlsWidth - iconWidth - 4);
        _description.MaximumSize = new Size(textWidth, 0);
        _title.MaximumSize = new Size(textWidth, 0);
        int textHeight = _title.GetPreferredSize(new Size(textWidth, 0)).Height + 3 +
                         (_description.Visible ? _description.GetPreferredSize(new Size(textWidth, 0)).Height : 0);
        Height = Math.Max(textHeight, _controls.PreferredSize.Height) + Padding.Vertical + 2;
    }
}

/// <summary>Choose one of a few options, shown as joined buttons; the chosen one is gold.</summary>
internal sealed class Segmented : FlowLayoutPanel
{
    private readonly List<ModernButton> _buttons = [];
    private int _selected = -1;

    public Segmented(string accessibleName, params string[] options)
    {
        FlowDirection = FlowDirection.LeftToRight;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = false;
        Margin = Padding.Empty;
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.Grouping;
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var b = new ModernButton(options[i], ButtonKind.Secondary) { Height = 32, Margin = new Padding(0, 0, 4, 0), AccessibleRole = AccessibleRole.RadioButton };
            b.Click += (_, _) => { Selected = index; Chosen?.Invoke(index); };
            _buttons.Add(b);
            Controls.Add(b);
        }
    }

    public event Action<int>? Chosen;
    public IReadOnlyList<ModernButton> Buttons => _buttons;

    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            for (int i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].Kind = i == value ? ButtonKind.Primary : ButtonKind.Secondary;
                _buttons[i].AccessibleDescription = i == value ? "selected" : null;
                _buttons[i].Invalidate();
            }
        }
    }
}

/// <summary>A page name in the settings window's side list; the current one is highlighted with a gold bar.</summary>
internal sealed class NavItem : Button
{
    private bool _hover;

    public NavItem(string text, string glyph)
    {
        Text = text;
        Glyph = glyph;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 38;
        Width = 176;
        Margin = new Padding(0, 0, 0, 2);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTab;
    }

    public string Glyph { get; }

    private bool _current;
    public bool Current { get => _current; set { _current = value; AccessibleDescription = value ? "current page" : null; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.Behind(this));
        float s = Ui.Scale(this);
        if (Current || _hover)
        {
            using var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * s);
            using var brush = new SolidBrush(Current ? Theme.Selection : Theme.Hover);
            g.FillPath(brush, path);
        }
        if (Current)
        {
            using var bar = Draw.Rounded(new RectangleF(2, Height * 0.25f, 4 * s, Height * 0.5f), 2 * s);
            using var gold = new SolidBrush(Theme.Accent);
            g.FillPath(gold, bar);
        }
        using var iconFont = new Font(Draw.IconFont, 11f);
        var color = Current ? Theme.Text : Theme.Muted;
        TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle((int)(14 * s), 0, (int)(22 * s), Height), Current ? Theme.Accent : color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        using var font = Current ? new Font(Font, FontStyle.Bold) : new Font(Font, FontStyle.Regular);
        TextRenderer.DrawText(g, Text, font, new Rectangle((int)(46 * s), 0, Width - (int)(50 * s), Height), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Color.FromArgb(160, Theme.Accent)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 2, 2, Width - 5, Height - 5);
        }
    }
}

/// <summary>Asks for a short line of text (a snapshot's name), in the app's look.</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _box = new() { Width = 380, Margin = new Padding(0, 8, 0, 0) };

    public InputDialog(string heading, string message, string ok, string initial = "", int maxLength = 40)
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

        _box.Text = initial;
        _box.MaxLength = maxLength;
        _box.AccessibleName = heading;
        var title = Ui.Text(heading, bold: true, size: 11.5f);
        title.Margin = new Padding(0, 0, 0, 6);
        var note = Ui.Text(message, TextRole.Muted);
        note.MaximumSize = new Size(380, 0);
        var okButton = new ModernButton(ok, ButtonKind.Primary) { DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new ModernButton(L.T("Cancel"), ButtonKind.Ghost) { DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
        okButton.Enabled = initial.Trim().Length > 0;
        _box.TextChanged += (_, _) => okButton.Enabled = _box.Text.Trim().Length > 0;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange([okButton, cancel]);
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 20, 24, 16) };
        layout.Controls.Add(title);
        layout.Controls.Add(note);
        layout.Controls.Add(_box);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancel;
        Theme.Apply(this);
        Ui.ScaleWithDisplay(this);
    }

    public string Value => _box.Text.Trim();

    /// <summary>Tests: the answer instead of showing the window (null: Cancel).</summary>
    internal static Func<string?>? AutoAnswer { get; set; }

    public static string? Ask(IWin32Window? owner, string heading, string message, string ok, string initial = "", int maxLength = 40)
    {
        if (AutoAnswer != null) return AutoAnswer();
        using var d = new InputDialog(heading, message, ok, initial, maxLength);
        return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
    }
}
