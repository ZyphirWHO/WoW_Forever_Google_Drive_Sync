using InterfaceSync.Core.Settings;

namespace InterfaceSync.Core;

/// <summary>
/// The colours both apps use, in one place: an original "night and gold" look in the spirit of fantasy game
/// launchers (no game artwork, logos or fonts). Both themes avoid pure black and pure white, so they are easy on
/// the eyes rather than high contrast.
/// </summary>
public static class ThemePalette
{
    /// <summary>Night and gold: deep blue-slate with parchment-white text and gilded accents. Soft, not high contrast.</summary>
    public static class Dark
    {
        public const string Window = "#171A22";    // window background
        public const string Surface = "#1F232D";   // lists, text boxes, menus, cards
        public const string Raised = "#2A2F3B";    // buttons
        public const string Hover = "#343A48";
        public const string Border = "#3B4253";
        public const string Selection = "#2F3B56"; // selected row
        public const string Text = "#E3DCCB";      // parchment white
        public const string Muted = "#9EA3AE";
        public const string Accent = "#D4AE5F";    // gold: headings, links, focus
        public const string Error = "#E8877C";
        public const string Warning = "#E3B26A";
    }

    /// <summary>Parchment and bronze: warm paper tones with dark ink text.</summary>
    public static class Light
    {
        public const string Window = "#F2EBDC";
        public const string Surface = "#FBF7EF";
        public const string Raised = "#E8DDC6";
        public const string Hover = "#DFD1B4";
        public const string Border = "#C8B48A";
        public const string Selection = "#EAD9AE";
        public const string Text = "#2A2217";
        public const string Muted = "#6B5F4B";
        public const string Accent = "#7A5418";    // bronze
        public const string Error = "#A3261B";
        public const string Warning = "#8A4B00";
    }

    /// <summary>The banner across the top of every window, the same in both themes, and the gold primary button.</summary>
    public static class Banner
    {
        public const string Top = "#22335A";
        public const string Bottom = "#0E1528";
        public const string Title = "#EBCB7C";
        public const string Subtitle = "#C3CCDD";
        public const string Rule = "#C9A24E";
        public const string GoldLight = "#E7C676";
        public const string Gold = "#D2A954";
        public const string GoldDark = "#9C7A35";
        public const string OnGold = "#1E1608";
    }

    public static string Describe(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "match the computer",
    };

    /// <summary>"#RRGGBB" → (r, g, b).</summary>
    public static (byte R, byte G, byte B) Rgb(string hex) =>
        (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));

    /// <summary>WCAG contrast ratio between two colours (1 to 21).</summary>
    public static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            var (r, g, b) = Rgb(hex);
            static double C(byte v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * C(r) + 0.7152 * C(g) + 0.0722 * C(b);
        }
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
