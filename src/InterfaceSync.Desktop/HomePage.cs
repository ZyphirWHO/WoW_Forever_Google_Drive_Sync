using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Desktop;

/// <summary>
/// Home: the state in one glance (with progress while syncing), Sync now and Pause, a tile per game version with
/// what it needs, and the last few things that synced.
/// </summary>
internal sealed class HomePage : Page
{
    private readonly Border _heroIcon = new() { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 18, 0) };
    private readonly TextBlock _headline = Ui.Text("", 22, FontWeight.SemiBold);
    private readonly TextBlock _detail = Ui.Text("", 13, brush: "InterfaceSyncMuted");
    private readonly ProgressBar _progress = new() { Height = 4, MinHeight = 4, Margin = new Thickness(0, 14, 0, 0), IsVisible = false, CornerRadius = new CornerRadius(2) };
    private readonly Button _pause;
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _recent = new() { Spacing = 2 };
    private string _tilesKey = "", _recentKey = "";
    private readonly List<string> _tileTexts = [];

    public HomePage(MainWindow shell) : base(shell)
    {
        _pause = Ui.Button(L.T("Pause…"), () => Actions.Pause(_pause!, Host, Shell.Refresh), icon: Ui.Pause);
        var syncNow = Ui.Button(L.T("Sync now"), () => Host.Run(Service.SyncNow, Shell.Refresh), ButtonKind.Primary, Ui.Sync);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _headline, _detail } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _pause, syncNow } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { _heroIcon, text, buttons } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        var hero = Ui.CardOf(new StackPanel { Children = { row, _progress } });
        hero.Padding = new Thickness(22, 20);
        hero.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetLiveSetting(_detail, AutomationLiveSetting.Polite);

        var seeAll = Ui.Button(L.T("See all activity"), () => Shell.Navigate("Activity"), ButtonKind.Ghost, Ui.ChevronRight);
        var recentHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { Brand.Section(L.T("Recent activity")), seeAll } };
        Grid.SetColumn(seeAll, 1);
        seeAll.VerticalAlignment = VerticalAlignment.Center;

        Content = Scroll(new StackPanel { Children =
        {
            hero,
            Brand.Section(L.T("Game versions")),
            _tiles,
            recentHead,
            Ui.Card(_recent),
        } });
    }

    public override string Key => "Home";

    public override void Refresh()
    {
        var now = DateTime.UtcNow;
        var tone = StatusWords.Tone(Service.State);
        _headline.Text = StatusWords.Headline(Service.State);
        var last = StatusWords.LastSynced(Service.Workers);
        var busy = Service.Workers.FirstOrDefault(w => w.Progress != null);
        _detail.Text = busy != null ? $"{busy.Version.FolderName}: {StatusWords.ProgressText(busy)}"
            : Service.State == OverallState.InSync ? L.F("Last synced {0}", StatusWords.Ago(last, now))
            : $"{StatusWords.StateText(Service, now)}{(last != null ? " · " + L.F("last synced {0}", StatusWords.Ago(last, now)) : "")}";
        var icon = Ui.Icon(Ui.ToneIcon(tone), 26, Ui.ToneBrush(tone));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        _heroIcon.Child = icon;
        Ui.Res(_heroIcon, Border.BorderBrushProperty, Ui.ToneBrush(tone));
        _pause.Content = Service.Settings.Paused ? Ui.ButtonContent(L.T("Resume"), Ui.Sync) : Ui.ButtonContent(L.T("Pause…"), Ui.Pause);

        // progress: a real bar while files are copied, a moving one while syncing otherwise
        _progress.IsVisible = Service.State == OverallState.Syncing || busy != null;
        if (busy?.Progress is { } p)
        {
            _progress.IsIndeterminate = false;
            _progress.Maximum = p.Total;
            _progress.Value = Math.Min(p.Done + 1, p.Total);
        }
        else _progress.IsIndeterminate = true;

        RefreshTiles(now);
        RefreshRecent(now);
    }

    // ------------------------------------------------------------------ game versions

    private void RefreshTiles(DateTime now)
    {
        var key = string.Join("|", Service.Versions.Select(v =>
        {
            var w = Service.Workers.FirstOrDefault(x => PathUtil.SamePath(x.Version.FolderPath, v.FolderPath));
            return $"{v.FolderName}{v.ClientVersion}{w?.Status}{w?.StatusText}{w?.State.LastPushUtc}{w?.State.LastPullUtc}{w?.State.PendingPush}{w?.State.Conflict != null}{Service.RulesFor(v.FolderName).Describe()}{StatusWords.Ago(w?.State.LastPullUtc, now)}{StatusWords.Ago(w?.State.LastPushUtc, now)}";
        })) + Look.IsDark;
        if (key == _tilesKey) return;
        _tilesKey = key;
        _tiles.Children.Clear();
        _tileTexts.Clear();
        if (Service.Versions.Count == 0)
        {
            _tiles.Children.Add(EmptyState(Ui.Game, L.T("No game versions found"),
                Service.Wow is { Found: false }
                    ? L.T("World of Warcraft wasn't found. Open setup again from Settings → Advanced, or make sure the game is installed.")
                    : L.T("Play a game version once so it has settings to sync.")));
            return;
        }
        foreach (var v in Service.Versions)
        {
            var w = Service.Workers.FirstOrDefault(x => PathUtil.SamePath(x.Version.FolderPath, v.FolderPath));
            _tiles.Children.Add(Tile(v, w, now));
        }
    }

    private Control Tile(GameVersion v, VersionSync? w, DateTime now)
    {
        var title = Ui.Text(v.FolderName, 15, FontWeight.SemiBold);
        var version = Ui.Text(v.ClientVersion ?? "", 12, brush: "InterfaceSyncMuted");
        version.VerticalAlignment = VerticalAlignment.Center;
        string status;
        string brush;
        string details;
        if (w == null)
        {
            status = L.T("Off");
            brush = "InterfaceSyncMuted";
            details = v.CanEnable ? (Os.IsMac ? L.T("Not synced on this Mac.") : L.T("Not synced on this computer.")) : (Os.IsMac ? L.T("Not played on this Mac yet") : L.T("Not played on this computer yet"));
        }
        else
        {
            var tone = StatusWords.Tone(w.Status);
            status = StatusWords.StatusText(w);
            brush = Ui.ToneBrush(tone);
            details = (w.State.LastError is { Length: > 0 } err && tone == StatusTone.Attention ? err : StatusWords.Details(w, now))
                      + (w.State.PendingPush ? " · " + L.T("upload pending") : "");
        }
        var pill = Ui.Pill(status, brush);
        pill.VerticalAlignment = VerticalAlignment.Top;
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title, version } };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { titleRow, pill } };
        Grid.SetColumn(pill, 1);
        var body = new StackPanel { Spacing = 6, Children = { head, Ui.Text(details, 12, brush: "InterfaceSyncMuted") } };
        if (w != null && Service.RulesFor(v.FolderName).Describe() is { } rules)
            body.Children.Add(Ui.Text(rules, 12, brush: "InterfaceSyncInfo"));
        if (w != null && w.Rules.InstalledAddons != null && Service.UninstalledAddonDataFor(w) is { Addons.Count: > 0 } gone)
            body.Children.Add(Ui.Text(L.N(gone.Addons.Count, "{0} addon that isn't installed here is left out", "{0} addons that aren't installed here are left out"), 12, brush: "InterfaceSyncMuted"));

        var actions = new WrapPanel { Margin = new Thickness(-8, 6, 0, 0) };
        if (w != null)
        {
            var worker = w;
            if (w.State.Conflict != null) actions.Children.Add(Spaced(Ui.Button(L.T("Choose which to keep…"), () => Actions.Resolve(Shell, Host, worker), ButtonKind.Primary)));
            actions.Children.Add(Spaced(Ui.Button(L.T("Backups"), () => Shell.Navigate("Backups", worker.Version.FolderName), ButtonKind.Ghost, Ui.History)));
            actions.Children.Add(Spaced(Ui.Button(L.T("Choose what syncs"), () => Actions.ChooseWhatSyncs(Shell, worker), ButtonKind.Ghost, Ui.Settings)));
        }
        else if (v.CanEnable)
        {
            var version2 = v;
            actions.Children.Add(Spaced(Ui.Button(L.T("Turn on"), () => Actions.SetVersionEnabled(Host, version2, true, Shell.Refresh), ButtonKind.Ghost, Ui.Power)));
        }
        if (actions.Children.Count > 0) body.Children.Add(actions);

        var card = Ui.CardOf(body);
        card.Width = 330;
        card.Margin = new Thickness(0, 0, 12, 12);
        if (w?.State.Conflict != null) { Ui.Res(card, Border.BorderBrushProperty, "InterfaceSyncWarning"); card.BorderThickness = new Thickness(1.5); }
        if (w == null) card.Opacity = 0.8;
        AutomationProperties.SetName(card, $"{v.FolderName}: {status}. {details}");
        _tileTexts.Add($"{v.FolderName}: {status}");
        return card;
    }

    private static Control Spaced(Button b) { b.Margin = new Thickness(0, 0, 4, 0); return b; }

    internal static Control EmptyState(string icon, string title, string text)
    {
        var i = Ui.Icon(icon, 30, "InterfaceSyncMuted");
        i.HorizontalAlignment = HorizontalAlignment.Center;
        var t = Ui.Text(title, 15, FontWeight.SemiBold);
        t.TextAlignment = TextAlignment.Center;
        var d = Ui.Text(text, 12.5, brush: "InterfaceSyncMuted");
        d.TextAlignment = TextAlignment.Center;
        d.MaxWidth = 440;
        var card = Ui.CardOf(new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { i, t, d } });
        card.Padding = new Thickness(24, 28);
        card.MinWidth = 520;
        return card;
    }

    // ------------------------------------------------------------------ recent activity

    private void RefreshRecent(DateTime now)
    {
        var entries = Service.Activity.List(4);
        var key = string.Join("|", entries.Select(e => e.Utc.Ticks)) + StatusWords.Ago(entries.FirstOrDefault()?.Utc, now) + L.Language;
        if (key == _recentKey) return;
        _recentKey = key;
        _recent.Children.Clear();
        if (entries.Count == 0)
            _recent.Children.Add(Ui.Text(L.T("Nothing synced yet. Uploads, downloads and restores will appear here."), 12.5, brush: "InterfaceSyncMuted"));
        foreach (var e in entries)
            _recent.Children.Add(ActivityPage.Line(Shell, e, ActivityPage.Detail(e, now)));
    }

    /// <summary>For tests.</summary>
    internal string Headline => _headline.Text ?? "";
    internal string Detail => _detail.Text ?? "";
    internal IReadOnlyList<string> TileTexts => _tileTexts;
    internal bool ProgressShown => _progress.IsVisible;
}
