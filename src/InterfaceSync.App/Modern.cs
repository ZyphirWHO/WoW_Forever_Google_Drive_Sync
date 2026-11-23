using System.Drawing.Drawing2D;
using P = InterfaceSync.Core.ThemePalette;

namespace InterfaceSync.App;

internal enum ButtonKind { Secondary, Primary, Ghost }

/// <summary>Drawing helpers shared by the modern controls.</summary>
internal static class Draw
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The colour behind a control (its parent's), for painting rounded corners.</summary>
    public static Color Behind(Control c) => c.Parent?.BackColor ?? Theme.Window;

    /// <summary>Icon glyphs from Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10).</summary>
    public static readonly string IconFont =
        new System.Drawing.Text.InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public const string Cloud = "", Sync = "", Shield = "", Game = "", Folder = "",
        Check = "", Warning = "", Clock = "", Pause = "", Settings = "", More = "",
        Info = "", History = "", Computer = "", Power = "", Paint = "", Upload = "", Download = "";
}

/// <summary>A rounded button: gold primary, outlined secondary, or text-only ghost. Colours follow the theme.</summary>
internal sealed class ModernButton : Button
{
    private bool _hover, _down;

    public ModernButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        AutoSize = false;
        Height = 34;
        Margin = new Padding(0, 4, 8, 4);
        Padding = new Padding(16, 0, 16, 0);
        if (kind == ButtonKind.Primary && !Font.Bold) Font = new Font(Font, FontStyle.Bold);
    }

    public ButtonKind Kind { get; set; }

    /// <summary>An optional icon glyph shown before the text.</summary>
    public string? Icon { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        int icon = Icon != null ? 22 : 0;
        return new Size(Math.Max(84, text.Width + icon + Padding.Horizontal), Height);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Width = GetPreferredSize(Size.Empty).Width; }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Width = GetPreferredSize(Size.Empty).Width; }
    protected override void OnCreateControl() { base.OnCreateControl(); Width = GetPreferredSize(Size.Empty).Width; }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.Behind(this));
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, border, text;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = Brand.C(_down ? P.Banner.GoldDark : _hover ? P.Banner.GoldLight : P.Banner.Gold);
                border = Brand.C(P.Banner.GoldDark);
                text = Brand.C(P.Banner.OnGold);
                break;
            case ButtonKind.Ghost:
                fill = _down ? Theme.Raised : _hover ? Theme.Hover : Draw.Behind(this);
                border = fill;
                text = Theme.Accent;
                break;
            default:
                fill = _down ? Theme.Border : _hover ? Theme.Hover : Theme.Raised;
                border = Theme.Border;
                text = Theme.Text;
                break;
        }
        if (!Enabled)
        {
            fill = Color.FromArgb(Kind == ButtonKind.Primary ? 90 : 140, fill);
            text = Theme.Muted;
        }
        using (var path = Draw.Rounded(r, 8))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = Draw.Rounded(RectangleF.Inflate(r, -2.5f, -2.5f), 6);
            using var pen = new Pen(Color.FromArgb(160, Theme.Accent)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focus);
        }
        var content = TextRenderer.MeasureText(Text, Font);
        int iconWidth = Icon != null ? 22 : 0;
        int x = (Width - content.Width - iconWidth) / 2;
        if (Icon != null)
        {
            using var iconFont = new Font(Draw.IconFont, 10f);
            TextRenderer.DrawText(g, Icon, iconFont, new Rectangle(x, 0, 20, Height), text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + iconWidth, 0, content.Width + 2, Height), text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

/// <summary>A rounded panel on the surface colour, with a hairline border. Children take its background.</summary>
internal class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(0, 0, 0, 12);
    }

    /// <summary>A thin accent bar down the left edge (for the card that needs the player).</summary>
    public Color? Highlight { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.Behind(this));
        using var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10);
        using (var brush = new SolidBrush(BackColor)) g.FillPath(brush, path);
        using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
        if (Highlight is { } h)
        {
            using var bar = Draw.Rounded(new RectangleF(1, 10, 4, Height - 20), 2);
            using var brush = new SolidBrush(h);
            g.FillPath(brush, bar);
        }
    }
}

/// <summary>A label in the icon font: a small glyph next to text.</summary>
internal sealed class Glyph : Label
{
    public Glyph(string glyph, float size = 16f)
    {
        Text = glyph;
        Font = new Font(Draw.IconFont, size);
        AutoSize = true;
        UseMnemonic = false;
        Margin = new Padding(0, 2, 10, 0);
    }
}

/// <summary>The setup steps across the top: numbered dots joined by a line; done, current (gold), to come.</summary>
internal sealed class StepIndicator : Control
{
    private string[] _steps = [];
    private int _current;

    public StepIndicator()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 58;
        Dock = DockStyle.Top;
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    public void SetSteps(string[] steps, int current)
    {
        _steps = steps;
        _current = current;
        AccessibleName = current < steps.Length ? $"Step {current + 1} of {steps.Length}: {steps[current]}" : "Done";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (_steps.Length == 0) return;
        float margin = 70, y = 20, r = 10;
        float span = _steps.Length == 1 ? 0 : (Width - 2 * margin) / (_steps.Length - 1);
        using var small = new Font(Font.FontFamily, 8.5f);
        using var bold = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        for (int i = 0; i < _steps.Length - 1; i++)
        {
            using var pen = new Pen(i < _current ? Theme.Accent : Theme.Border, 2f);
            g.DrawLine(pen, margin + i * span + r + 3, y, margin + (i + 1) * span - r - 3, y);
        }
        for (int i = 0; i < _steps.Length; i++)
        {
            float cx = margin + i * span;
            var circle = new RectangleF(cx - r, y - r, 2 * r, 2 * r);
            bool done = i < _current, now = i == _current;
            using (var fill = new SolidBrush(done || now ? Theme.Accent : BackColor)) g.FillEllipse(fill, circle);
            using (var pen = new Pen(done || now ? Theme.Accent : Theme.Border, 1.5f)) g.DrawEllipse(pen, circle);
            var mark = done ? "✓" : (i + 1).ToString();
            var markColor = done || now ? Brand.C(P.Banner.OnGold) : Theme.Muted;
            TextRenderer.DrawText(g, mark, now ? bold : small, Rectangle.Round(circle), markColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var label = new Rectangle((int)(cx - 70), (int)(y + r + 4), 140, 18);
            TextRenderer.DrawText(g, _steps[i], now ? bold : small, label, now ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>An on/off switch with a label; behaves as a CheckBox (Checked, CheckedChanged, keyboard).</summary>
internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Height = 28;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 4, 0, 4);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(TextRenderer.MeasureText(Text, Font).Width + 58, 28);

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Width = GetPreferredSize(Size.Empty).Width; }
    protected override void OnCreateControl() { base.OnCreateControl(); Width = GetPreferredSize(Size.Empty).Width; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.Behind(this));
        var track = new RectangleF(1, (Height - 20) / 2f, 40, 20);
        using (var path = Draw.Rounded(track, 10))
        {
            using var fill = new SolidBrush(Checked ? Theme.Accent : Theme.Raised);
            g.FillPath(fill, path);
            using var pen = new Pen(Checked ? Theme.Accent : Theme.Border);
            g.DrawPath(pen, path);
        }
        float knob = 14;
        float kx = Checked ? track.Right - knob - 3 : track.X + 3;
        using (var k = new SolidBrush(Checked ? Brand.C(P.Banner.OnGold) : Theme.Muted))
            g.FillEllipse(k, kx, track.Y + 3, knob, knob);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Color.FromArgb(160, Theme.Accent)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(50, 0, Width - 50, Height), Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>A small rounded status tag: coloured dot and text.</summary>
internal sealed class StatusPill : Control
{
    private Color _color = Color.Gray;

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 24;
        Margin = new Padding(0, 2, 0, 2);
        TabStop = false;
    }

    public void Set(string text, Color color)
    {
        Text = text;
        _color = color;
        AccessibleName = text;
        Width = TextRenderer.MeasureText(text, Font).Width + 30;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.Behind(this));
        using (var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 11))
        {
            using var fill = new SolidBrush(Color.FromArgb(38, _color));
            g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(110, _color));
            g.DrawPath(pen, path);
        }
        using (var dot = new SolidBrush(_color)) g.FillEllipse(dot, 9, Height / 2f - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(21, 0, Width - 23, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}
