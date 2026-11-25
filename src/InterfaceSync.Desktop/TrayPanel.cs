using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.Desktop;

/// <summary>
/// The panel by the clock (Windows: click the tray icon; Mac: click the menu-bar icon), like the ones cloud apps
/// show: the state, Sync now and Pause, the last few things that synced, and links into the main window. It closes
/// when you click elsewhere or press Escape.
/// </summary>
internal sealed class TrayPanel : Window
{
    private readonly IAppHost _host;
    private readonly Border _icon = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _headline = Ui.Text("", 15.5, FontWeight.SemiBold);
    private readonly TextBlock _detail = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly ProgressBar _progress = new() { Height = 3, MinHeight = 3, Margin = new Thickness(0, 10, 0, 0), IsVisible = false };
    private readonly StackPanel _notice = new() { Spacing = 6 };
    private readonly StackPanel _recent = new() { Spacing = 0 };
    private readonly Button _pause;
    private readonly List<string> _recentTexts = [];

    public TrayPanel(IAppHost host)
    {
        _host = host;
        Title = ProductInfo.Name;
        Icon = AppIcons.App;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var crest = new Image { Source = Brand.Crest, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
        var name = new TextBlock { Text = ProductInfo.Name, FontFamily = Brand.Heading, FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Ui.Res(name, TextBlock.ForegroundProperty, "InterfaceSyncAccent");
        var settings = Ui.IconButton(Ui.Settings, L.T("Settings"), () => Open("Settings"));
        var open = Ui.IconButton(Ui.Home, L.F("Open {0}", ProductInfo.Name), () => Open("Home"));
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Children = { crest, name, open, settings } };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(open, 2);
        Grid.SetColumn(settings, 3);

        var statusText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _headline, _detail } };
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _icon, statusText } };
        Grid.SetColumn(statusText, 1);
        AutomationProperties.SetLiveSetting(_detail, AutomationLiveSetting.Polite);

        _pause = Ui.Button(L.T("Pause…"), () => Actions.Pause(_pause!, _host, Refresh), icon: Ui.Pause);
        var sync = Ui.Button(L.T("Sync now"), () => _host.Run(_host.Service.SyncNow, Refresh), ButtonKind.Primary, Ui.Sync);
        _pause.HorizontalAlignment = sync.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pause.HorizontalContentAlignment = sync.HorizontalContentAlignment = HorizontalAlignment.Center;
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Margin = new Thickness(0, 14, 0, 0), Children = { _pause, sync } };
        Grid.SetColumn(sync, 2);

        var seeAll = Ui.Button(L.T("See all activity"), () => Open("Activity"), ButtonKind.Ghost, Ui.ChevronRight);
        var recentHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 2), Children = { Ui.Text(L.T("Recent activity"), 12, FontWeight.SemiBold, "InterfaceSyncMuted"), seeAll } };
        ((Control)recentHead.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(seeAll, 1);

        var quit = Ui.Button(L.F("Quit {0}", ProductInfo.Name), () => (Application.Current as DesktopApp)?.Quit(), ButtonKind.Ghost);
        quit.IsVisible = Os.IsMac;   // on a Mac the panel replaces the menu, so Quit lives here (Windows: right-click the icon)
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0), Children =
        {
            Ui.Text(L.F("Version {0}", ProductInfo.DisplayVersion), 11, brush: "InterfaceSyncMuted"),
            quit,
        } };
        ((Control)footer.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(quit, 1);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14, 18, 12), Margin = new Thickness(10),
            BoxShadow = BoxShadows.Parse("0 10 30 0 #70000000"),
            Child = new StackPanel { Children = { head, new Border { Height = 12 }, _notice, status, _progress, buttons, recentHead, _recent, footer } },
        };
        Ui.Res(card, Border.BackgroundProperty, "InterfaceSyncWindow");
        Ui.Res(card, Border.BorderBrushProperty, "InterfaceSyncBorder");
        Content = card;

        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
    }

    private void Open(string page)
    {
        Hide();
        _host.ShowMain(page);
    }

    /// <summary>Shows the panel next to the clock: bottom right above the taskbar on Windows, top right under the menu bar on a Mac.</summary>
    public void ShowNearClock()
    {
        Refresh();
        Show();
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            int w = (int)(Bounds.Width * scale), h = (int)(Bounds.Height * scale);
            bool taskbarOnTop = area.Y > screen.Bounds.Y;
            int x = area.Right - w - (int)(4 * scale);
            int y = Os.IsMac || taskbarOnTop ? area.Y + (int)(4 * scale) : area.Bottom - h - (int)(4 * scale);
            Position = new PixelPoint(x, y);
        }
        Activate();
    }

    public void Refresh()
    {
        var service = _host.Service;
        var now = DateTime.UtcNow;
        var tone = StatusWords.Tone(service.State);
        _headline.Text = StatusWords.Headline(service.State);
        var last = StatusWords.LastSynced(service.Workers);
        var busy = service.Workers.FirstOrDefault(w => w.Progress != null);
        _detail.Text = busy != null ? $"{busy.Version.FolderName}: {StatusWords.ProgressText(busy)}"
            : service.State == OverallState.InSync ? L.F("Last synced {0}", StatusWords.Ago(last, now))
            : StatusWords.StateText(service, now);
        var icon = Ui.Icon(Ui.ToneIcon(tone), 20, Ui.ToneBrush(tone));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.Child = icon;
        Ui.Res(_icon, Border.BorderBrushProperty, Ui.ToneBrush(tone));
        _progress.IsVisible = service.State == OverallState.Syncing || busy != null;
        if (busy?.Progress is { } p) { _progress.IsIndeterminate = false; _progress.Maximum = p.Total; _progress.Value = Math.Min(p.Done + 1, p.Total); }
        else _progress.IsIndeterminate = true;
        _pause.Content = service.Settings.Paused ? Ui.ButtonContent(L.T("Resume"), Ui.Sync) : Ui.ButtonContent(L.T("Pause…"), Ui.Pause);

        // what needs the player, at the top
        _notice.Children.Clear();
        foreach (var w in service.Workers.Where(w => w.State.Conflict != null))
        {
            var worker = w;
            _notice.Children.Add(Ui.MessageBar("InterfaceSyncWarning", Ui.Warning, L.F("{0}: settings changed on two computers", w.Version.FolderName), null,
                [Ui.Button(L.T("Choose…"), () => { Hide(); Actions.Resolve(null, _host, worker); }, ButtonKind.Primary)], stacked: true));
        }
        if (_host.Updater is { Available: { } r } u && u.State == UpdateState.Available)
            _notice.Children.Add(Ui.MessageBar("InterfaceSyncAccent", Ui.Download,
                r.IsPrerelease ? L.F("Beta {0} is available", r.Version) : L.F("{0} {1} is available", ProductInfo.Name, r.Version), null,
                [Ui.Button(Os.IsWindows ? L.T("Update now") : L.T("Download update"), () => { Hide(); _host.InstallUpdate(); }, ButtonKind.Primary)], stacked: true));
        _notice.Margin = new Thickness(0, 0, 0, _notice.Children.Count > 0 ? 12 : 0);

        _recent.Children.Clear();
        _recentTexts.Clear();
        var entries = service.Activity.List(4);
        if (entries.Count == 0) _recent.Children.Add(Ui.Text(L.T("Nothing synced yet."), 12, brush: "InterfaceSyncMuted"));
        foreach (var e in entries)
        {
            var (glyph, brush) = e.Kind switch
            {
                Core.Sync.ActivityKind.Uploaded => (Ui.Upload, "InterfaceSyncAccent"),
                Core.Sync.ActivityKind.Downloaded => (Ui.Download, "InterfaceSyncGood"),
                Core.Sync.ActivityKind.Conflict => (Ui.Warning, "InterfaceSyncWarning"),
                _ => (Ui.History, "InterfaceSyncAccent"),
            };
            var i = Ui.Icon(glyph, 14, brush);
            i.Margin = new Thickness(0, 2, 10, 0);
            i.VerticalAlignment = VerticalAlignment.Top;
            var text = new StackPanel { Children = { Ui.Text(e.Headline, 12.5), Ui.Text(ActivityPage.Detail(e, now), 11, brush: "InterfaceSyncMuted") } };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4), Children = { i, text } };
            Grid.SetColumn(text, 1);
            _recent.Children.Add(g);
            _recentTexts.Add(e.Headline);
        }
    }

    /// <summary>For tests.</summary>
    internal string Headline => _headline.Text ?? "";
    internal IReadOnlyList<string> RecentTexts => _recentTexts;
}
