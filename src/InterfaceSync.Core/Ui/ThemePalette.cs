using InterfaceSync.Core.Settings;

namespace InterfaceSync.Core;

/// <summary>
/// The colours both apps use, in one place. Dark is a warm charcoal with soft off-white text and a muted
/// clay accent: easy on the eyes, deliberately not high contrast (no pure black or pure white).
/// Light keeps each platform's normal look; only the status colours are shared.
/// </summary>
public static class ThemePalette
{
    public static class Dark
    {
        public const string Window = "#262624";   // window background
        public const string Surface = "#30302E";  // lists, text boxes, menus
        public const string Raised = "#3A3935";   // buttons
        public const string Hover = "#45443F";
        public const string Border = "#4A4943";
        public const string Selection = "#4A3F38"; // warm tint for the selected row
        public const string Text = "#DEDCD1";
        public const string Muted = "#A6A39A";
        public const string Accent = "#D97757";
        public const string Error = "#E8877C";
        public const string Warning = "#E3A869";
    }

    public static class Light
    {
        public const string Accent = "#C6613F";
        public const string Error = "#B41E1E";
        public const string Warning = "#8C4600";
        public const string Muted = "#6D6D6D";
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
