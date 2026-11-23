using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InterfaceSync.Core;
using B = InterfaceSync.Core.ThemePalette.Banner;

namespace InterfaceSync.Mac;

/// <summary>
/// The look shared by every window: night-blue banner with the crest, gold section headings and the gold primary
/// button. All original artwork; headings use Cinzel (SIL Open Font License, see assets/fonts).
/// </summary>
internal static class MacBrand
{
    public static readonly FontFamily Heading = new("avares://InterfaceSync/Assets/Fonts#Cinzel");
    private static Bitmap? _crest;
    public static Bitmap Crest => _crest ??= new Bitmap(typeof(MacBrand).Assembly.GetManifestResourceStream("app.png")!);

    public static Color C(string hex) { var (r, g, b) = ThemePalette.Rgb(hex); return Color.FromRgb(r, g, b); }
    public static IBrush Brush(string hex) => new SolidColorBrush(C(hex));

    /// <summary>The banner: crest, product name in Cinzel, a subtitle, gold rule with a centre diamond.</summary>
    public static (Control banner, TextBlock subtitle) Banner(string subtitleText)
    {
        var subtitle = new TextBlock { Text = subtitleText, FontSize = 13, Foreground = Brush(B.Subtitle), TextTrimming = TextTrimming.CharacterEllipsis };
        var title = new TextBlock { Text = ProductInfo.Name, FontFamily = Heading, FontWeight = FontWeight.Bold, FontSize = 24, Foreground = Brush(B.Title) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0, Children = { title, subtitle } };
        var crest = new Image { Source = Crest, Width = 60, Height = 60, Margin = new Thickness(18, 10, 14, 12) };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(text, 1);
        row.Children.Add(crest);
        row.Children.Add(text);

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(C(B.Top), 0), new GradientStop(C(B.Bottom), 1) },
        };
        var glow = new RadialGradientBrush
        {
            Center = new RelativePoint(0.08, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.08, 0.3, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.25, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(1.2, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(70, 0xD2, 0xA9, 0x54), 0), new GradientStop(Color.FromArgb(0, 0xD2, 0xA9, 0x54), 1) },
        };
        var rule = new Border
        {
            Height = 2.5, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(C(B.GoldDark), 0), new GradientStop(C(B.GoldLight), 0.5), new GradientStop(C(B.GoldDark), 1) },
            },
        };
        var diamond = Diamond(5, C(B.GoldLight));
        diamond.VerticalAlignment = VerticalAlignment.Bottom;
        diamond.HorizontalAlignment = HorizontalAlignment.Center;
        diamond.Margin = new Thickness(0, 0, 0, -3.75);
        var filigree = new Path
        {
            Data = Geometry.Parse("M 0,1 L 120,1 M 120,1 L 120,24"),
            Stroke = new SolidColorBrush(Color.FromArgb(90, 0xC9, 0xA2, 0x4E)), StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 14, 0),
        };
        var banner = new Grid
        {
            Height = 86, ClipToBounds = false,
            Children = { new Border { Background = gradient }, new Border { Background = glow }, filigree, row, rule, diamond },
        };
        return (banner, subtitle);
    }

    public static Path Diamond(double r, Color fill) => new()
    {
        Data = Geometry.Parse($"M {r},0 L {2 * r},{r} L {r},{2 * r} L 0,{r} Z"),
        Fill = new SolidColorBrush(fill),
        Width = 2 * r, Height = 2 * r,
    };

    /// <summary>A section heading: Cinzel in the theme's accent with a thin rule after it.</summary>
    public static Control Section(string text)
    {
        var label = new TextBlock { Text = text, FontFamily = Heading, FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("InterfaceSyncAccent"));
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0), Opacity = 0.45 };
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("InterfaceSyncAccent"));
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 12, 0, 6) };
        Grid.SetColumn(line, 1);
        g.Children.Add(label);
        g.Children.Add(line);
        return g;
    }

    /// <summary>Makes a button the window's main action: gold, with dark text, in both themes.</summary>
    public static T Primary<T>(T button) where T : Button
    {
        var r = button.Resources;
        r["ButtonBackground"] = Brush(B.Gold);
        r["ButtonBackgroundPointerOver"] = Brush(B.GoldLight);
        r["ButtonBackgroundPressed"] = Brush(B.GoldDark);
        r["ButtonBackgroundDisabled"] = new SolidColorBrush(Color.FromArgb(110, 0xD2, 0xA9, 0x54));
        r["ButtonForeground"] = Brush(B.OnGold);
        r["ButtonForegroundPointerOver"] = Brush(B.OnGold);
        r["ButtonForegroundPressed"] = Brush(B.OnGold);
        r["ButtonForegroundDisabled"] = new SolidColorBrush(Color.FromArgb(150, 0x1E, 0x16, 0x08));
        r["ButtonBorderBrush"] = Brush(B.GoldDark);
        r["ButtonBorderBrushPointerOver"] = Brush(B.GoldDark);
        r["ButtonBorderBrushPressed"] = Brush(B.GoldDark);
        button.FontWeight = FontWeight.SemiBold;
        return button;
    }
}
