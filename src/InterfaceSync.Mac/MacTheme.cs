using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using InterfaceSync.Core;
using InterfaceSync.Core.Settings;
using P = InterfaceSync.Core.ThemePalette;

namespace InterfaceSync.Mac;

/// <summary>
/// Light or dark windows. "Match macOS" follows System Settings → Appearance, live. Dark uses the warm,
/// soft palette in <see cref="ThemePalette.Dark"/> instead of Fluent's near-black.
/// </summary>
internal static class MacTheme
{
    public static FluentTheme Create() => new()
    {
        Palettes =
        {
            [ThemeVariant.Dark] = new ColorPaletteResources
            {
                Accent = C(P.Dark.Accent),
                RegionColor = C(P.Dark.Window),
                ErrorText = C(P.Dark.Error),
                BaseHigh = C(P.Dark.Text),
                BaseMediumHigh = C("#C9C6BB"),
                BaseMedium = C(P.Dark.Muted),
                BaseMediumLow = C("#77756E"),
                BaseLow = C(P.Dark.Border),
                AltHigh = C(P.Dark.Window),
                AltMediumHigh = C("#2B2B29"),
                AltMedium = C(P.Dark.Surface),
                AltMediumLow = C("#353533"),
                AltLow = C(P.Dark.Raised),
                ChromeLow = C("#1F1E1D"),
                ChromeMediumLow = C("#2B2B29"),
                ChromeMedium = C(P.Dark.Surface),
                ChromeHigh = C(P.Dark.Border),
                ChromeGray = C(P.Dark.Muted),
                ChromeWhite = C(P.Dark.Text),
                ChromeAltLow = C(P.Dark.Text),
                ChromeDisabledHigh = C(P.Dark.Border),
                ChromeDisabledLow = C("#77756E"),
                ListLow = C(P.Dark.Surface),
                ListMedium = C(P.Dark.Raised),
            },
            [ThemeVariant.Light] = new ColorPaletteResources { Accent = C(P.Light.Accent) },
        },
    };

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
