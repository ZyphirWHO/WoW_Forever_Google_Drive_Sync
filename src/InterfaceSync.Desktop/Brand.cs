using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using InterfaceSync.Core.Localization;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InterfaceSync.Core;
using B = InterfaceSync.Core.ThemePalette.Banner;

namespace InterfaceSync.Desktop;

/// <summary>
/// The look shared by every window: night-blue banner with the crest, gold section headings and the gold primary
/// button. All original artwork; headings use Cinzel (SIL Open Font License, see assets/fonts).
/// </summary>
internal static class Brand
{
    public static readonly FontFamily Heading = new("avares://InterfaceSync/Assets/Fonts#Cinzel");
    private static Bitmap? _crest;
    public static Bitmap Crest => _crest ??= new Bitmap(typeof(Brand).Assembly.GetManifestResourceStream("app.png")!);

    public static Color C(string hex) { var (r, g, b) = ThemePalette.Rgb(hex); return Color.FromRgb(r, g, b); }
    public static IBrush Brush(string hex) => new SolidColorBrush(C(hex));

    /// <summary>The banner: crest, product name in Cinzel, a subtitle, gold rule with a centre diamond.</summary>
    public static (Control banner, TextBlock subtitle) Banner(string subtitleText, Action? toggleTheme = null)
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
            Data = Geometry.Parse("M 0,1 L 120,1"),
            Stroke = new SolidColorBrush(Color.FromArgb(90, 0xC9, 0xA2, 0x4E)), StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 14, 0),
        };
        var banner = new Grid
        {
            Height = 86, ClipToBounds = false,
            Children = { new Border { Background = gradient }, new Border { Background = glow }, filigree, row, rule, diamond },
        };
        if (toggleTheme != null)
        {
            filigree.Margin = new Thickness(0, 10, 110, 0);
            banner.Children.Add(ThemeToggle(toggleTheme));
        }
        return (banner, subtitle);
    }

    // 24x24 icons for the light/dark switch (original).
    internal const string SunIcon = "M12,8 A4,4 0 1 1 11.99,8 Z M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22 M4.9,4.9 L6.3,6.3 M17.7,17.7 L19.1,19.1 M4.9,19.1 L6.3,17.7 M17.7,6.3 L19.1,4.9";
    internal const string MoonIcon = "M15.5,3 A9,9 0 1 0 21,15.5 A7.2,7.2 0 0 1 15.5,3 Z";

    /// <summary>
    /// The light/dark switch at the top right of the banner: a pill with a sun on the left and a crescent moon on
    /// the right; a gold knob sits behind the current one.
    /// </summary>
    public static Button ThemeToggle(Action toggle, bool onDarkSurface = true)
    {
        var knob = new Avalonia.Controls.Shapes.Ellipse { Width = 26, Height = 26, Fill = Brush(B.Gold), VerticalAlignment = VerticalAlignment.Center };
        Path Icon(string data, bool filled)
        {
            var p = new Path { Data = Geometry.Parse(data), Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (filled) p.Fill = Brush(B.Subtitle); else p.Stroke = Brush(B.Subtitle);
            return p;
        }
        var sun = Icon(SunIcon, false);
        var moon = Icon(MoonIcon, true);
        var sunSlot = new Grid { Width = 26, Height = 26, Children = { sun } };
        var moonSlot = new Grid { Width = 26, Height = 26, Children = { moon } };
        var slots = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(3), Children = { knob, sunSlot, moonSlot } };
        Grid.SetColumn(moonSlot, 2);
        var pill = new Border
        {
            Width = 72, Height = 34, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 0xC9, 0xA2, 0x4E)), Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            Child = slots,
        };
        var button = new Button
        {
            Content = pill, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 2),
        };
        button.Resources["ButtonBackground"] = Brushes.Transparent;
        button.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        button.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = Brushes.Transparent;
        void Show()
        {
            bool dark = Look.IsDark;
            Grid.SetColumn(knob, dark ? 2 : 0);
            var onGold = Brush(B.OnGold);
            var idle = onDarkSurface ? Brush(B.Subtitle) : Look.Muted;
            sun.Stroke = dark ? idle : onGold;
            moon.Fill = dark ? onGold : idle;
            ToolTip.SetTip(button, dark ? "Switch to light mode" : "Switch to dark mode");
            Avalonia.Automation.AutomationProperties.SetName(button, dark ? "Switch to light mode" : "Switch to dark mode");
        }
        if (!onDarkSurface)
        {
            Ui.Res(pill, Border.BackgroundProperty, "InterfaceSyncRaised");
            Ui.Res(pill, Border.BorderBrushProperty, "InterfaceSyncBorder");
        }
        button.Click += (_, _) => toggle();
        Action changed = () => Avalonia.Threading.Dispatcher.UIThread.Post(Show);
        button.AttachedToVisualTree += (_, _) => { Look.Changed += changed; Show(); };
        button.DetachedFromVisualTree += (_, _) => Look.Changed -= changed;
        button.PointerEntered += (_, _) => pill.BorderBrush = Brush(B.GoldLight);
        button.PointerExited += (_, _) =>
        {
            if (onDarkSurface) pill.BorderBrush = new SolidColorBrush(Color.FromArgb(150, 0xC9, 0xA2, 0x4E));
            else Ui.Res(pill, Border.BorderBrushProperty, "InterfaceSyncBorder");
        };
        Show();
        return button;
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

/// <summary>The modern building blocks: cards, vector icons, status pills, the step indicator and button kinds.</summary>
internal static class Ui
{
    public static T Res<T>(T c, AvaloniaProperty property, string key) where T : Control
    {
        c.Bind(property, c.GetResourceObservable(key));
        return c;
    }

    public static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, string? brush = null, double bottom = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };
        if (brush != null) Res(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static Border Card(params Control[] children)
    {
        var stack = new StackPanel { Spacing = 6 };
        foreach (var c in children) stack.Children.Add(c);
        return CardOf(stack);
    }

    public static Border CardOf(Control child)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12), Margin = new Thickness(0, 0, 0, 12), Child = child };
        Res(b, Border.BackgroundProperty, "InterfaceSyncSurface");
        Res(b, Border.BorderBrushProperty, "InterfaceSyncBorder");
        return b;
    }

    /// <summary>A card's heading: icon, title, and an optional control on the right.</summary>
    public static Grid Header(string icon, string title, Control? right = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        var i = Icon(icon, 18);
        i.Margin = new Thickness(0, 0, 10, 0);
        var t = Text(title, 14, FontWeight.SemiBold);
        t.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(t, 1);
        g.Children.Add(i);
        g.Children.Add(t);
        if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(right, 2); g.Children.Add(right); }
        return g;
    }

    // 24x24 line icons, drawn here (original).
    public const string Cloud = "M7,19 A5,5 0 0 1 6.5,9.1 A7,7 0 0 1 19.6,9 A5,5 0 0 1 18,19 Z";
    public const string Sync = "M4.5,12 A7.5,7.5 0 0 1 17.8,7.3 M18.5,3.5 V7.8 H14.2 M19.5,12 A7.5,7.5 0 0 1 6.2,16.7 M5.5,20.5 V16.2 H9.8";
    public const string Shield = "M12,2.5 L19.5,5.5 V11 C19.5,15.8 16.3,19.6 12,21.5 C7.7,19.6 4.5,15.8 4.5,11 V5.5 Z";
    public const string Game = "M7.5,7 H16.5 A4.5,4.5 0 0 1 16.5,16 H7.5 A4.5,4.5 0 0 1 7.5,7 Z M7,11.5 H10 M8.5,10 V13 M15,11 H15.1 M17,12.5 H17.1";
    public const string Power = "M12,3 V11 M7.2,6.2 A8,8 0 1 0 16.8,6.2";
    public const string Paint = "M12,3.5 A8.5,8.5 0 1 0 12,20.5 A8.5,8.5 0 1 0 12,3.5 Z M12,3.5 V20.5";
    public const string Check = "M5,12.5 L10,17.5 L19,7";
    public const string Warning = "M12,3.5 L21.5,20 H2.5 Z M12,9.5 V14 M12,16.8 V17";
    public const string Clock = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 M12,7.5 V12 L15,14";
    public const string Pause = "M9,6 V18 M15,6 V18";
    public const string Folder = "M3.5,6.5 H9.5 L11.5,8.5 H20.5 V18.5 H3.5 Z";
    public const string Info = "M12,3.5 A8.5,8.5 0 1 1 11.99,3.5 M12,11 V16.5 M12,7.8 V8";
    public const string Upload = "M12,16 V4.5 M7.5,9 L12,4.5 L16.5,9 M5,19.5 H19";
    public const string Download = "M12,4.5 V16 M7.5,11.5 L12,16 L16.5,11.5 M5,19.5 H19";
    public const string Computer = "M4,5.5 H20 V15.5 H4 Z M9,19.5 H15 M12,15.5 V19.5";
    public const string Home = "M4,11 L12,4 L20,11 M6,9.5 V20 H18 V9.5 M10,20 V14 H14 V20";
    public const string Close = "M6,6 L18,18 M18,6 L6,18";
    public const string History = "M4,12 A8,8 0 1 0 6.3,6.3 M4,4 V8 H8 M12,8 V12 L15,14";
    public const string ChevronRight = "M9,6 L15,12 L9,18";
    public const string Trash = "M5,7 H19 M10,7 V5 H14 V7 M7,7 L8,20 H16 L17,7";
    public const string Zip = "M6,3 H14 L18,7 V21 H6 Z M14,3 V7 H18 M11,10 H12 M11,13 H12 M11,16 H12";
    public const string Settings = "M12,8.5 A3.5,3.5 0 1 1 11.99,8.5 M12,2.5 V5 M12,19 V21.5 M2.5,12 H5 M19,12 H21.5 M5.3,5.3 L7,7 M17,17 L18.7,18.7 M5.3,18.7 L7,17 M17,7 L18.7,5.3";

    public static Avalonia.Controls.Shapes.Path Icon(string data, double size = 18, string brush = "InterfaceSyncAccent")
    {
        var p = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
        };
        Res(p, Avalonia.Controls.Shapes.Shape.StrokeProperty, brush);
        return p;
    }

    /// <summary>A small rounded tag: coloured dot and text.</summary>
    public static Border Pill(string text, string brush)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        Res(dot, Avalonia.Controls.Shapes.Shape.FillProperty, brush);
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        var b = new Border
        {
            CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Padding = new Thickness(9, 3, 11, 3),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, label } },
        };
        Res(b, Border.BorderBrushProperty, brush);
        b.Background = Brushes.Transparent;
        return b;
    }

    public static string ToneBrush(StatusTone tone) => tone switch
    {
        StatusTone.Good => "InterfaceSyncGood",
        StatusTone.Busy => "InterfaceSyncInfo",
        StatusTone.Attention => "InterfaceSyncError",
        StatusTone.Paused => "InterfaceSyncMuted",
        _ => "InterfaceSyncWarning",
    };

    public static string ToneIcon(StatusTone tone) => tone switch
    {
        StatusTone.Good => Check,
        StatusTone.Busy => Sync,
        StatusTone.Attention => Warning,
        StatusTone.Paused => Pause,
        _ => Clock,
    };

    /// <summary>Numbered dots joined by lines; done and current in gold.</summary>
    public static Control Steps(string[] steps, int current)
    {
        var g = new Grid { Margin = new Thickness(40, 14, 40, 6) };
        for (int i = 0; i < steps.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int i = 0; i < steps.Length; i++)
        {
            bool done = i < current, now = i == current;
            var circle = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center };
            Res(circle, Border.BorderBrushProperty, done || now ? "InterfaceSyncAccent" : "InterfaceSyncBorder");
            if (done || now) Res(circle, Border.BackgroundProperty, "InterfaceSyncAccent");
            circle.Child = new TextBlock
            {
                Text = done ? "✓" : (i + 1).ToString(), FontSize = 11, FontWeight = now ? FontWeight.Bold : FontWeight.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = done || now ? Brand.Brush(ThemePalette.Banner.OnGold) : null,
            };
            if (!(done || now)) Res((TextBlock)circle.Child, TextBlock.ForegroundProperty, "InterfaceSyncMuted");
            var label = Text(steps[i], 11.5, now ? FontWeight.SemiBold : FontWeight.Normal, now ? "InterfaceSyncText" : "InterfaceSyncMuted");
            label.TextAlignment = TextAlignment.Center;
            label.Margin = new Thickness(0, 4, 0, 0);
            var cell = new StackPanel { Children = { circle, label } };
            if (i < steps.Length - 1)
            {
                // a line from this dot's centre to the next one's: the middle half of a two-column span
                var line = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 10, 14, 0) };
                Res(line, Border.BackgroundProperty, done ? "InterfaceSyncAccent" : "InterfaceSyncBorder");
                var span = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), Children = { line } };
                Grid.SetColumn(line, 1);
                Grid.SetColumnSpan(line, 2);
                Grid.SetColumn(span, i);
                Grid.SetColumnSpan(span, 2);
                g.Children.Add(span);
            }
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
        return g;
    }

    /// <summary>A button's content: text, with an icon before it when given.</summary>
    public static object ButtonContent(string label, string? icon, ButtonKind kind = ButtonKind.Secondary)
    {
        if (icon == null) return label;
        var i = Icon(icon, 15, kind == ButtonKind.Ghost ? "InterfaceSyncAccent" : "InterfaceSyncText");
        if (kind == ButtonKind.Primary) i.Stroke = Brand.Brush(ThemePalette.Banner.OnGold);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { i, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
    }

    /// <summary>A small round button with only an icon (close, help), named for screen readers.</summary>
    public static Button IconButton(string icon, string name, Action onClick)
    {
        var b = new Button
        {
            Content = Icon(icon, 15, "InterfaceSyncMuted"), Padding = new Thickness(8), MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Resources["ButtonBackground"] = Brushes.Transparent;
        b.Resources["ButtonBorderBrush"] = Brushes.Transparent;
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        ToolTip.SetTip(b, name);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// A message across the top of a page (an update, a copy of the cloud folder, settings changed on two computers):
    /// a coloured edge and icon, a title, optional detail, buttons, and an optional close.
    /// </summary>
    public static Border MessageBar(string toneBrush, string icon, string title, string? detail, Control[] buttons, Action? dismiss = null, bool stacked = false)
    {
        var i = Icon(icon, 20, toneBrush);
        i.VerticalAlignment = VerticalAlignment.Top;
        i.Margin = new Thickness(0, 1, 12, 0);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { Text(title, 13.5, FontWeight.SemiBold) } };
        if (detail != null) text.Children.Add(Text(detail, 12, brush: "InterfaceSyncMuted"));
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var b in buttons) right.Children.Add(b);
        if (dismiss != null) right.Children.Add(IconButton(Close, L.T("Dismiss"), dismiss));
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { i, text, right } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
        if (stacked)
        {
            // narrow places (the panel by the clock): the buttons go under the text
            Grid.SetColumn(right, 1);
            Grid.SetRow(right, 1);
            g.RowDefinitions = new RowDefinitions("Auto,Auto");
            right.Margin = new Thickness(0, 8, 0, 0);
            right.HorizontalAlignment = HorizontalAlignment.Left;
        }
        var bar = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1, 1, 1, 1), Padding = new Thickness(16, 12, 12, 12), Child = g, Tag = title };
        Res(bar, Border.BackgroundProperty, "InterfaceSyncSurface");
        Res(bar, Border.BorderBrushProperty, toneBrush);
        Avalonia.Automation.AutomationProperties.SetName(bar, title);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(bar, Avalonia.Automation.AutomationLiveSetting.Polite);
        return bar;
    }

    public static Button Button(string label, Action onClick, ButtonKind kind = ButtonKind.Secondary, string? icon = null)
    {
        var content = ButtonContent(label, icon, kind);
        var b = new Button { Content = content, Padding = new Thickness(14, 7), MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => onClick();
        if (kind == ButtonKind.Primary) Brand.Primary(b);
        if (kind == ButtonKind.Ghost)
        {
            b.Resources["ButtonBackground"] = Brushes.Transparent;
            b.Resources["ButtonBorderBrush"] = Brushes.Transparent;
            b.Resources["ButtonBorderBrushPointerOver"] = Brushes.Transparent;
            Res(b, Avalonia.Controls.Button.ForegroundProperty, "InterfaceSyncAccent");
        }
        return b;
    }
}

internal enum ButtonKind { Secondary, Primary, Ghost }
