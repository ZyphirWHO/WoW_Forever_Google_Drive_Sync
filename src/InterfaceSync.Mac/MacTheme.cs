using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using InterfaceSync.Core;
using InterfaceSync.Core.Settings;
using P = InterfaceSync.Core.ThemePalette;

namespace InterfaceSync.Mac;

/// <summary>
/// Night-and-gold (dark) or parchment (light) windows from <see cref="ThemePalette"/>. "Match macOS" follows
/// System Settings → Appearance, live.
/// </summary>
internal static class MacTheme
{
    public static FluentTheme Create() => new()
    {
        Palettes =
        {
            [ThemeVariant.Dark] = Palette(P.Dark.Window, P.Dark.Surface, P.Dark.Raised, P.Dark.Hover, P.Dark.Border,
                P.Dark.Text, P.Dark.Muted, P.Dark.Accent, P.Dark.Error, P.Dark.Selection),
            [ThemeVariant.Light] = Palette(P.Light.Window, P.Light.Surface, P.Light.Raised, P.Light.Hover, P.Light.Border,
                P.Light.Text, P.Light.Muted, P.Light.Accent, P.Light.Error, P.Light.Selection),
        },
    };

    /// <summary>Fluent's palette slots filled from our colours (Base = text shades, Alt = backgrounds, Chrome = control chrome).</summary>
    private static ColorPaletteResources Palette(string window, string surface, string raised, string hover, string border,
        string text, string muted, string accent, string error, string selection) => new()
    {
        Accent = C(accent),
        RegionColor = C(window),
        ErrorText = C(error),
        BaseHigh = C(text),
        BaseMediumHigh = Mix(text, muted, 0.5),
        BaseMedium = C(muted),
        BaseMediumLow = Mix(muted, border, 0.5),
        BaseLow = C(border),
        AltHigh = C(window),
        AltMediumHigh = Mix(window, surface, 0.5),
        AltMedium = C(surface),
        AltMediumLow = Mix(surface, raised, 0.5),
        AltLow = C(raised),
        ChromeLow = C(window),
        ChromeMediumLow = Mix(window, surface, 0.5),
        ChromeMedium = C(surface),
        ChromeHigh = C(border),
        ChromeGray = C(muted),
        ChromeWhite = C(text),
        ChromeAltLow = C(text),
        ChromeDisabledHigh = C(border),
        ChromeDisabledLow = Mix(muted, border, 0.5),
        ListLow = C(hover),
        ListMedium = C(selection),
    };

    /// <summary>Resources the brand controls bind to, so headings follow the theme.</summary>
    public static void AddResources(IResourceDictionary resources)
    {
        resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["InterfaceSyncAccent"] = new SolidColorBrush(C(P.Dark.Accent)) };
        resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary { ["InterfaceSyncAccent"] = new SolidColorBrush(C(P.Light.Accent)) };
    }

    private static Color Mix(string a, string b, double t)
    {
        var (ar, ag, ab) = P.Rgb(a); var (br, bg, bb) = P.Rgb(b);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(ar, br), L(ag, bg), L(ab, bb));
    }

    public static AppTheme Mode { get; private set; } = AppTheme.System;

    /// <summary>Raised after the choice changes, so menus and windows can update.</summary>
    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        Mode = theme;
        if (Application.Current is { } app) app.RequestedThemeVariant = Variant(theme);
        Changed?.Invoke();
    }

    public static ThemeVariant Variant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,   // follow macOS
    };

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static IBrush Error => new SolidColorBrush(C(IsDark ? P.Dark.Error : P.Light.Error));
    public static IBrush Muted => new SolidColorBrush(C(IsDark ? P.Dark.Muted : P.Light.Muted));

    private static Color C(string hex) { var (r, g, b) = P.Rgb(hex); return Color.FromRgb(r, g, b); }
}
