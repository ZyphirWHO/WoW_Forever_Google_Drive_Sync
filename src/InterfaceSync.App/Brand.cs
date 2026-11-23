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
        // fine gold filigree along the top-right corner
        using (var pen = new Pen(Color.FromArgb(90, Brand.C(B.Rule)), 1f))
        {
            g.DrawLine(pen, r.Right - 150, 10, r.Right - 14, 10);
            g.DrawLine(pen, r.Right - 14, 10, r.Right - 14, 34);
            g.DrawArc(pen, r.Right - 40, 10, 26, 26, 270, 90);
        }
        Brand.Diamond(g, r.Right - 150, 10, 2.5f, Color.FromArgb(140, Brand.C(B.Rule)));

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
