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

    private static Window Small(string title, Control content) => new()
    {
        Title = title,
        Icon = MacIcons.App,
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = new Border { Padding = new Thickness(18), Child = content },
    };

    public static Window Message(string message)
    {
        Window? w = null;
        var panel = new StackPanel();
        panel.Children.Add(Text(message));
        panel.Children.Add(Buttons(Button("OK", () => w!.Close(), isDefault: true)));
        return w = Small(ProductInfo.Name, panel);
    }

    internal static string AboutText =>
        $"{ProductInfo.Tagline}\n\n{ProductInfo.NotAffiliated}\n\n{ProductInfo.TrademarkNotice}\n\nVersion {ProductInfo.Version}. {ProductInfo.Licence}.\n\n{ProductInfo.FontCredit}";

    public static Window About()
    {
        Window? w = null;
        var panel = new StackPanel();
        panel.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(typeof(Dialogs).Assembly.GetManifestResourceStream("app.png")!), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(Text($"{ProductInfo.Name} {ProductInfo.Version}", 16, FontWeight.SemiBold));
        panel.Children.Add(Text(AboutText));
        panel.Children.Add(Buttons(Button("OK", () => w!.Close(), isDefault: true)));
        return w = Small($"About {ProductInfo.Name}", panel);
    }

    /// <summary>Asks which copy to keep; the newer one is the default. Calls back with null on cancel.</summary>
    public static Window ChooseKeep(string version, ConflictInfo c, Action<KeepChoice?> answer)
    {
        Window? w = null;
        bool answered = false;
        void Done(KeepChoice? k) { answered = true; answer(k); w!.Close(); }
        var panel = new StackPanel();
        panel.Children.Add(Text($"{version}: settings changed in two places", 15, FontWeight.SemiBold));
        panel.Children.Add(Text(c.Reason));
        panel.Children.Add(Text($"This Mac{(c.LocalMachine != null ? $" ({c.LocalMachine})" : "")}: last changed {Format(c.LocalNewestUtc)}\n" +
                                $"Cloud{(c.CloudPushedBy != null ? $" (from {c.CloudPushedBy})" : "")}: last changed {Format(c.CloudNewestUtc)}"));
        panel.Children.Add(Text("The copy you don't keep is saved as a backup."));
        panel.Children.Add(Buttons(
            Button("Cancel", () => Done(null)),
            Button("Keep the cloud settings", () => Done(KeepChoice.Cloud), c.Suggested == KeepChoice.Cloud),
            Button("Keep this Mac's settings", () => Done(KeepChoice.Local), c.Suggested == KeepChoice.Local)));
        w = Small(ProductInfo.Name, panel);
        w.Closed += (_, _) => { if (!answered) answer(null); };
        return w;
    }

    internal static string Format(DateTime? utc) => utc?.ToLocalTime().ToString("g") ?? "never";
}
