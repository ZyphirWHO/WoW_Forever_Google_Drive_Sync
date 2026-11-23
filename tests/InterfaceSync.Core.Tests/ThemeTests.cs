using InterfaceSync.Core;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void Dark_text_is_readable_but_not_harsh()
    {
        // Readable (WCAG AA is 4.5) but softer than white on black (21).
        var text = ThemePalette.Contrast(ThemePalette.Dark.Text, ThemePalette.Dark.Window);
        Assert.InRange(text, 9, 14);
        Assert.InRange(ThemePalette.Contrast(ThemePalette.Dark.Text, ThemePalette.Dark.Surface), 7, 13);
        Assert.True(ThemePalette.Contrast(ThemePalette.Dark.Muted, ThemePalette.Dark.Window) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Dark.Error, ThemePalette.Dark.Window) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Dark.Warning, ThemePalette.Dark.Window) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Dark.Text, ThemePalette.Dark.Selection) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Dark.Accent, ThemePalette.Dark.Window) >= 4.5);
    }

    [Fact]
    public void Light_parchment_and_banner_are_readable()
    {
        Assert.InRange(ThemePalette.Contrast(ThemePalette.Light.Text, ThemePalette.Light.Window), 9, 16);
        foreach (var c in new[] { ThemePalette.Light.Muted, ThemePalette.Light.Accent, ThemePalette.Light.Error, ThemePalette.Light.Warning })
        {
            Assert.True(ThemePalette.Contrast(c, ThemePalette.Light.Window) >= 4.5, c);
            Assert.True(ThemePalette.Contrast(c, ThemePalette.Light.Surface) >= 4.5, c);
        }
        Assert.True(ThemePalette.Contrast(ThemePalette.Light.Text, ThemePalette.Light.Selection) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Banner.Title, ThemePalette.Banner.Top) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Banner.Subtitle, ThemePalette.Banner.Top) >= 4.5);
        Assert.True(ThemePalette.Contrast(ThemePalette.Banner.OnGold, ThemePalette.Banner.Gold) >= 4.5);
    }

    [Fact]
    public void Theme_defaults_to_matching_the_computer_and_is_saved()
    {
        var fs = new InMemoryFileSystem();
        var store = new SettingsStore(fs, "/data");
        Assert.Equal(AppTheme.System, store.Load().Theme);
        var s = store.Load();
        s.Theme = AppTheme.Dark;
        store.Save(s);
        Assert.Equal(AppTheme.Dark, store.Load().Theme);
    }
}
