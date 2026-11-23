using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using InterfaceSync.Core;
using B = InterfaceSync.Core.ThemePalette.Banner;

namespace InterfaceSync.App;

/// <summary>
/// The look shared by every window: the night-blue banner with the crest, gold section headings and the gold
/// primary button. All original artwork; the heading font is Cinzel (SIL Open Font License, see assets/fonts).
/// </summary>
internal static class Brand
{
    private static readonly PrivateFontCollection Fonts = new();
    private static readonly FontFamily? Cinzel = LoadCinzel();

    public static Image Crest { get; } = LoadImage("crest.png");

    /// <summary>Cinzel Bold, or a serif that every Windows has if it couldn't be loaded.</summary>
    public static Font Heading(float size) =>
        Cinzel != null ? new Font(Cinzel, size, FontStyle.Bold, GraphicsUnit.Point) : new Font("Georgia", size, FontStyle.Bold, GraphicsUnit.Point);

    internal static bool HasCinzel => Cinzel != null;

    public static Color C(string hex) { var (r, g, b) = ThemePalette.Rgb(hex); return Color.FromArgb(r, g, b); }

    private static FontFamily? LoadCinzel()
    {
        try
        {
            using var stream = typeof(Brand).Assembly.GetManifestResourceStream("Cinzel-Bold.ttf");
            if (stream == null) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            // GDI+ keeps reading the font from this memory for the life of the app, so it is never freed.
            var memory = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Fonts.AddMemoryFont(memory, bytes.Length);
            return Fonts.Families.FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    private static Image LoadImage(string name)
    {
        using var stream = typeof(Brand).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded image {name} is missing.");
        return new Bitmap(Image.FromStream(stream));
    }

    /// <summary>Text drawn with GDI+ (the embedded font isn't visible to GDI's TextRenderer).</summary>
    internal static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment align = StringAlignment.Near)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, rect, format);
    }

    /// <summary>A small gold diamond, the ornament used on rules.</summary>
    internal static void Diamond(Graphics g, float cx, float cy, float r, Color fill)
    {
        using var brush = new SolidBrush(fill);
        g.FillPolygon(brush, [new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy)]);
    }
}

/// <summary>The banner across the top of each window: crest, title in Cinzel, subtitle, gold rule.</summary>
internal sealed class BrandBanner : Control
{
    private readonly Font _title = Brand.Heading(17f);
    private readonly Font _subtitle;
    private string _sub;

    private ThemeToggle? _toggle;

    /// <summary>Shows the sun/moon switch at the top right; <paramref name="toggle"/> flips light and dark.</summary>
    public void EnableThemeToggle(Action toggle)
    {
        if (_toggle != null) return;
        _toggle = new ThemeToggle(toggle);
        Controls.Add(_toggle);
        PlaceToggle();
    }

    internal ThemeToggle? Toggle => _toggle;

    protected override void OnResize(EventArgs e) { base.OnResize(e); PlaceToggle(); }

    private void PlaceToggle()
    {
        if (_toggle == null) return;
        _toggle.Location = new Point(Width - _toggle.Width - 20, (int)((Height - 2.5f - _toggle.Height) / 2));
    }

    public BrandBanner(string subtitle)
    {
        _sub = subtitle;
        _subtitle = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 9.5f);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 84;
        Margin = Padding.Empty;
        Dock = DockStyle.Top;
        TabStop = false;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = $"{ProductInfo.Name}: {subtitle}";
    }

    public string Subtitle { get => _sub; set { _sub = value; AccessibleName = $"{ProductInfo.Name}: {value}"; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;

        using (var bg = new LinearGradientBrush(r, Brand.C(B.Top), Brand.C(B.Bottom), LinearGradientMode.Vertical))
            g.FillRectangle(bg, r);
        // a soft glow behind the crest
        using (var glow = new GraphicsPath())
        {
            glow.AddEllipse(-30, -40, 200, 170);
            using var pgb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, Brand.C(B.Gold)), SurroundColors = [Color.FromArgb(0, Brand.C(B.Gold))] };
            g.FillPath(pgb, glow);
        }
        // fine gold filigree along the top edge
        using (var pen = new Pen(Color.FromArgb(90, Brand.C(B.Rule)), 1f))
            g.DrawLine(pen, r.Right - 250, 10, r.Right - 120, 10);
        Brand.Diamond(g, r.Right - 250, 10, 2.5f, Color.FromArgb(140, Brand.C(B.Rule)));
        Brand.Diamond(g, r.Right - 120, 10, 2.5f, Color.FromArgb(140, Brand.C(B.Rule)));

        int crest = Height - 22;
        g.DrawImage(Brand.Crest, new Rectangle(16, 9, crest, crest));
        float x = 16 + crest + 12;
        Brand.DrawText(g, ProductInfo.Name, _title, Brand.C(B.Title), new RectangleF(x, 12, r.Width - x - 16, 34));
        Brand.DrawText(g, _sub, _subtitle, Brand.C(B.Subtitle), new RectangleF(x + 1, 44, r.Width - x - 16, 22));

        // gold rule with a centre diamond
        float y = r.Bottom - 2.5f;
        using (var rule = new LinearGradientBrush(new RectangleF(0, y - 1, r.Width, 3), Brand.C(B.GoldDark), Brand.C(B.GoldLight), LinearGradientMode.Horizontal))
        {
            var blend = new ColorBlend { Positions = [0f, 0.5f, 1f], Colors = [Brand.C(B.GoldDark), Brand.C(B.GoldLight), Brand.C(B.GoldDark)] };
            rule.InterpolationColors = blend;
            g.FillRectangle(rule, 0, y - 1, r.Width, 2.5f);
        }
        Brand.Diamond(g, r.Width / 2f, y, 4.5f, Brand.C(B.GoldLight));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _title.Dispose(); _subtitle.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A section heading: small caps in Cinzel, in the theme's accent, followed by a thin rule.</summary>
internal sealed class SectionHeader : Control
{
    private readonly Font _font = Brand.Heading(10.5f);

    public SectionHeader(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Height = 26;
        Width = 600;
        Margin = new Padding(0, 10, 0, 4);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);
        var accent = Theme.Accent;
        var size = g.MeasureString(Text, _font);
        Brand.DrawText(g, Text, _font, accent, new RectangleF(0, 0, size.Width + 4, Height));
        float x = size.Width + 10, y = Height / 2f + 1;
        if (x + 20 < Width)
        {
            using var pen = new Pen(Color.FromArgb(120, accent), 1f);
            g.DrawLine(pen, x + 6, y, Width - 2, y);
            Brand.Diamond(g, x + 2, y, 2.5f, Color.FromArgb(170, accent));
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The light/dark switch in the banner: a pill with a sun on the left and a crescent moon on the right; a gold knob
/// sits behind the current one. Click, Space or Enter flips it.
/// </summary>
internal sealed class ThemeToggle : Button
{
    private readonly Action _toggle;
    private readonly ToolTip _tip = new();
    private bool _hover;

    public ThemeToggle(Action toggle)
    {
        _toggle = toggle;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Size = new Size(70, 34);
        Cursor = Cursors.Hand;
        Theme.Changed += Refresh;
        Refresh();
    }

    private new void Refresh()
    {
        var text = Theme.IsDark ? "Switch to light mode" : "Switch to dark mode";
        AccessibleName = text;
        AccessibleDescription = Theme.IsDark ? "Dark mode is on" : "Light mode is on";
        _tip.SetToolTip(this, text);
        Invalidate();
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); _toggle(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // the banner's gradient behind us, so the pill sits on it seamlessly
        int top = Parent is { } p ? Top : 0, height = Parent?.Height ?? Height;
        using (var bg = new LinearGradientBrush(new Rectangle(0, -top, Width, Math.Max(1, height)), Brand.C(B.Top), Brand.C(B.Bottom), LinearGradientMode.Vertical))
            g.FillRectangle(bg, ClientRectangle);

        var pill = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Draw.Rounded(pill, pill.Height / 2))
        {
            using var fill = new SolidBrush(Color.FromArgb(_hover ? 70 : 45, 0, 0, 0));
            g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(_hover ? 200 : 130, Brand.C(B.Rule)));
            g.DrawPath(pen, path);
        }
        float d = Height - 8, y = 4;
        var sunSlot = new RectangleF(4, y, d, d);
        var moonSlot = new RectangleF(Width - 4 - d, y, d, d);
        var knob = Theme.IsDark ? moonSlot : sunSlot;
        using (var k = new SolidBrush(Brand.C(B.Gold))) g.FillEllipse(k, knob);

        var onGold = Brand.C(B.OnGold);
        var idle = Color.FromArgb(200, Brand.C(B.Subtitle));
        DrawSun(g, sunSlot, Theme.IsDark ? idle : onGold);
        DrawMoon(g, moonSlot, Theme.IsDark ? onGold : idle);
        if (Focused && ShowFocusCues)
        {
            using var focus = Draw.Rounded(RectangleF.Inflate(pill, -2, -2), pill.Height / 2 - 2);
            using var pen = new Pen(Brand.C(B.GoldLight)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focus);
        }
    }

    internal static void DrawSun(Graphics g, RectangleF slot, Color color)
    {
        float cx = slot.X + slot.Width / 2, cy = slot.Y + slot.Height / 2, r = slot.Width * 0.17f;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            float r1 = r + 2.6f, r2 = r + 5.2f;
            g.DrawLine(pen, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1, cx + (float)Math.Cos(a) * r2, cy + (float)Math.Sin(a) * r2);
        }
    }

    internal static void DrawMoon(Graphics g, RectangleF slot, Color color)
    {
        float cx = slot.X + slot.Width / 2, cy = slot.Y + slot.Height / 2, r = slot.Width * 0.3f;
        using var moon = new GraphicsPath();
        moon.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
        using var bite = new GraphicsPath();
        bite.AddEllipse(cx - r + r * 0.75f, cy - r - r * 0.35f, 2 * r, 2 * r);
        using var region = new Region(moon);
        region.Exclude(bite);
        using var brush = new SolidBrush(color);
        g.FillRegion(brush, region);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Theme.Changed -= Refresh; _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
