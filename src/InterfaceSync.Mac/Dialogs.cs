using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Mac;

/// <summary>Small windows: messages, About, and the "which settings do you keep?" choice.</summary>
internal static class Dialogs
{
    internal static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight, Margin = new Thickness(0, 0, 0, 8),
    };

    internal static Button Button(string label, Action onClick, bool isDefault = false)
    {
        var b = new Button { Content = label, IsDefault = isDefault, Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    internal static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    private static Window Small(string title, Control content, double width = 480) => new()
    {
        Title = title,
        Icon = MacIcons.App,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = content,
    };

    /// <summary>A message in the app's look: an icon, an optional heading, the text and an OK button.</summary>
    public static Window Message(string message, string? heading = null, bool warning = false)
    {
        Window? w = null;
        var icon = Ui.Icon(warning ? Ui.Warning : Ui.Info, 26, warning ? "InterfaceSyncWarning" : "InterfaceSyncAccent");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 16, 0);
        var text = new StackPanel { Spacing = 6 };
        if (heading != null) text.Children.Add(Ui.Text(heading, 15, FontWeight.SemiBold));
        text.Children.Add(Ui.Text(message, 13));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { icon, text } };
        Grid.SetColumn(text, 1);
        var ok = Ui.Button("OK", () => w!.Close(), ButtonKind.Primary);
        ok.IsDefault = true;
        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 16), Children =
        {
            row,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok } },
        } };
        return w = Small(ProductInfo.Name, root);
    }

    internal static string AboutText =>
        $"{ProductInfo.Tagline}\n\n{ProductInfo.NotAffiliated}\n\n{ProductInfo.TrademarkNotice}\n\nVersion {ProductInfo.DisplayVersion}. {ProductInfo.Licence}.\n\n{ProductInfo.FontCredit}";

    public static Window About()
    {
        Window? w = null;
        var (banner, _) = MacBrand.Banner($"Version {ProductInfo.DisplayVersion}");
        var ok = Ui.Button("OK", () => w!.Close(), ButtonKind.Primary);
        ok.IsDefault = true;
        var root = new StackPanel { Children =
        {
            banner,
            new StackPanel { Margin = new Thickness(22, 16, 22, 16), Children =
            {
                Ui.Text(AboutText, 12.5),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok } },
            } },
        } };
        return w = Small($"About {ProductInfo.Name}", root, 560);
    }

    /// <summary>
    /// "Settings changed in two places": this Mac's copy and the cloud copy side by side (which computer, when each
    /// last changed, the newer one marked), each with its Keep button. Calls back with null on "Decide later".
    /// </summary>
    public static Window ChooseKeep(string version, ConflictInfo c, Action<KeepChoice?> answer)
    {
        Window? w = null;
        bool answered = false;
        void Done(KeepChoice? k) { answered = true; answer(k); w!.Close(); }

        Border Side(string icon, string title, DateTime? changed, bool newer, string keep, KeepChoice choice, string effect)
        {
            var button = Ui.Button(keep, () => Done(choice), newer ? ButtonKind.Primary : ButtonKind.Secondary);
            if (newer) button.IsDefault = true;
            var stack = new StackPanel { Spacing = 6, Children =
            {
                Ui.Icon(icon, 24),
                Ui.Text(title, 14, FontWeight.SemiBold),
            } };
            ((Control)stack.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
            if (newer) stack.Children.Add(Ui.Pill("Newer", "InterfaceSyncAccent"));
            ((Control)stack.Children[^1]).HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(Ui.Text(changed is { } t ? $"Last changed {t.ToLocalTime():ddd d MMM, HH:mm} ({StatusWords.Ago(t, DateTime.UtcNow)})" : "Last changed: unknown", 12.5));
            stack.Children.Add(Ui.Text(effect, 12, brush: "InterfaceSyncMuted", bottom: 6));
            stack.Children.Add(button);
            var card = Ui.CardOf(stack);
            if (newer) { Ui.Res(card, Border.BorderBrushProperty, "InterfaceSyncAccent"); card.BorderThickness = new Thickness(1.5); }
            return card;
        }

        var mine = Side(Ui.Game, c.LocalMachine != null ? $"This Mac ({c.LocalMachine})" : "This Mac", c.LocalNewestUtc, c.Suggested == KeepChoice.Local,
            "Keep this Mac's settings", KeepChoice.Local, "Uses the settings on this Mac and uploads them, replacing the cloud copy.");
        var cloud = Side(Ui.Cloud, c.CloudPushedBy != null ? $"Cloud copy (from {c.CloudPushedBy})" : "Cloud copy", c.CloudNewestUtc, c.Suggested == KeepChoice.Cloud,
            "Keep the cloud settings", KeepChoice.Cloud, "Downloads the cloud copy, replacing this Mac's settings.");
        mine.Margin = new Thickness(0, 0, 8, 0);
        cloud.Margin = new Thickness(8, 0, 0, 0);
        var sides = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { mine, cloud } };
        Grid.SetColumn(cloud, 1);

        var later = Ui.Button("Decide later", () => Done(null), ButtonKind.Ghost);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0), Children =
        {
            Ui.Text("The copy you don't keep is saved as a backup, so you can get it back any time with Restore… on this game version.", 12, brush: "InterfaceSyncMuted"),
            later,
        } };
        Grid.SetColumn(later, 1);
        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 16), Spacing = 4, Children =
        {
            Ui.Text($"{version}: settings changed in two places", 19, FontWeight.SemiBold),
            Ui.Text(c.Reason, 12.5, brush: "InterfaceSyncMuted", bottom: 10),
            sides,
            footer,
        } };
        w = Small($"{ProductInfo.Name}: choose which settings to keep", root, 760);
        w.Closed += (_, _) => { if (!answered) answer(null); };
        return w;
    }

    internal static string Format(DateTime? utc) => utc?.ToLocalTime().ToString("g") ?? "never";
}
