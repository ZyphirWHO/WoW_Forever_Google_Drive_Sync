using InterfaceSync.Core.Logging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.Mac;

/// <summary>
/// The status dashboard, like the Windows one: headline card with Sync now and Pause, a card per game version,
/// recent activity in plain words, your computers, and the tools (settings, help, what's new, report a problem).
/// Closing hides it; Quit is in the menu.
/// </summary>
internal sealed class StatusWindow : Window
{
    private readonly IMacHost _host;
    private readonly IStartAtLogin? _startup;
    private SyncService Service => _host.Service;

    private readonly Border _heroIcon = new() { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _headline = Ui.Text("", 20, FontWeight.SemiBold);
    private readonly TextBlock _detail = Ui.Text("", 12.5, brush: "InterfaceSyncMuted");
    private readonly Button _pause;
    private readonly StackPanel _rows = new();
    private readonly List<string> _rowTexts = [];
    private readonly StackPanel _activityRows = new() { Spacing = 2 };
    private readonly StackPanel _computerRows = new() { Spacing = 2 };
    private readonly List<string> _activityTexts = [], _computerTexts = [];
    private string _activityKey = "", _computersKey = "";
    private DateTime _computersReadUtc = DateTime.MinValue;
    private IReadOnlyList<DeviceInfo> _computerList = [];
    private readonly Updater? _updater;
    private readonly Border _updateBar;
    private readonly TextBlock _updateTitle = Ui.Text("", 14, FontWeight.SemiBold);
    private readonly TextBlock _updateDetail = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly Button _updateNow;
    private bool _revealUpdate;
    private ScrollViewer _scroller = null!;

    /// <summary>"Check now" / "Check for Updates…": checks, then brings the top (the update card) into view.</summary>
    internal void CheckForUpdates()
    {
        if (_updater == null) return;
        _revealUpdate = true;
        _ = _updater.CheckAsync();
    }
    private readonly Border _dupBar;
    private readonly TextBlock _dupTitle = Ui.Text("", 14, FontWeight.SemiBold);
    private readonly TextBlock _dupDetail = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private bool _allowClose;

    public StatusWindow(IMacHost host, IStartAtLogin? startAtLogin = null, Updater? updater = null)
    {
        _host = host;
        _startup = startAtLogin;
        _updater = updater;
        Title = $"{ProductInfo.Name} {ProductInfo.DisplayVersion}";
        Icon = MacIcons.App;
        Width = 780;
        Height = 760;
        MinWidth = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // headline
        _pause = Ui.Button("Pause", () => MacActions.Pause(_pause!, _host, Refresh), icon: Ui.Pause);
        var syncNow = Ui.Button("Sync now", () => _host.Run(Service.SyncNow, Refresh), ButtonKind.Primary, Ui.Sync);
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _headline, _detail } };
        var heroButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _pause, syncNow } };
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { _heroIcon, heroText, heroButtons } };
        Grid.SetColumn(heroText, 1);
        Grid.SetColumn(heroButtons, 2);
        var heroCard = Ui.CardOf(hero);
        heroCard.Padding = new Thickness(18, 16);

        // updates: a gold-edged card when a newer release is out, and the automatic-check switch in Settings
        _updateNow = Ui.Button("Download update", () => _ = DownloadUpdate(), ButtonKind.Primary, Ui.Sync);
        var whatsNew = Ui.Button("What's new", () => { if (_updater?.Available is { } r) _ = OpenUri(r.Page); }, ButtonKind.Ghost);
        var updateText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _updateTitle, _updateDetail } };
        var updateButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { whatsNew, _updateNow } };
        var updateIcon = Ui.Icon(Ui.Sync, 22);
        updateIcon.Margin = new Thickness(0, 0, 14, 0);
        var updateGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { updateIcon, updateText, updateButtons } };
        Grid.SetColumn(updateText, 1);
        Grid.SetColumn(updateButtons, 2);
        _updateBar = Ui.CardOf(updateGrid);
        Ui.Res(_updateBar, Border.BorderBrushProperty, "InterfaceSyncAccent");
        _updateBar.BorderThickness = new Thickness(1.5);
        _updateBar.IsVisible = false;
        // copies of the cloud folder made by the cloud app: pointed out, never touched
        var dupIcon = Ui.Icon(Ui.Warning, 22, "InterfaceSyncWarning");
        dupIcon.VerticalAlignment = VerticalAlignment.Top;
        dupIcon.Margin = new Thickness(0, 2, 14, 0);
        var dupButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top, Children =
        {
            Ui.Button("Hide", () => { foreach (var d in Service.Duplicates.ToList()) Service.HideDuplicate(d); Refresh(); }, ButtonKind.Ghost),
            Ui.Button("Show folder", () => { if (Service.Duplicates.FirstOrDefault() is { } d) MacActions.OpenFolder(this, d.Path); }, ButtonKind.Secondary, Ui.Folder),
        } };
        var dupText = new StackPanel { Spacing = 4, Children = { _dupTitle, _dupDetail } };
        var dupGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { dupIcon, dupText, dupButtons } };
        Grid.SetColumn(dupText, 1);
        Grid.SetColumn(dupButtons, 2);
        _dupBar = Ui.CardOf(dupGrid);
        Ui.Res(_dupBar, Border.BorderBrushProperty, "InterfaceSyncWarning");
        _dupBar.BorderThickness = new Thickness(1.5);
        _dupBar.IsVisible = false;

        var tools = new WrapPanel { Margin = new Thickness(-6, 0, 0, 0) };
        tools.Children.Add(Ui.Button("Settings", () => OpenSettings(), ButtonKind.Ghost, Ui.Settings));
        tools.Children.Add(Ui.Button("Help", () => MacActions.OpenLink(this, Feedback.HelpPage), ButtonKind.Ghost, Ui.Info));
        tools.Children.Add(Ui.Button("What's new", () => MacActions.ShowWhatsNew(this), ButtonKind.Ghost, Ui.Download));
        tools.Children.Add(Ui.Button("Report a problem", () => MacActions.ReportProblem(this, _host), ButtonKind.Ghost, Ui.Warning));
        tools.Children.Add(Ui.Button("About", () => Dialogs.About().Show(), ButtonKind.Ghost, Ui.Info));

        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        root.Children.Add(_updateBar);
        root.Children.Add(_dupBar);
        root.Children.Add(heroCard);
        root.Children.Add(MacBrand.Section("Game versions"));
        root.Children.Add(_rows);
        root.Children.Add(MacBrand.Section("Activity"));
        root.Children.Add(Ui.Card(_activityRows));
        root.Children.Add(MacBrand.Section("Your computers"));
        root.Children.Add(Ui.Card(_computerRows));
        root.Children.Add(tools);

        var (banner, _) = MacBrand.Banner("Your interface settings, on every computer", () => MacApp.ToggleTheme(_host));
        var dock = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        dock.Children.Add(banner);
        dock.Children.Add(_scroller = new ScrollViewer { Content = root });
        Content = dock;
        Refresh();
    }

    /// <summary>Opens the settings window on a page; the status refreshes when it closes.</summary>
    internal SettingsWindow OpenSettings(string page = "General")
    {
        var w = new SettingsWindow(_host, _startup, _updater, CheckForUpdates, page);
        w.Closed += (_, _) => Refresh();
        w.Show();
        return w;
    }

    public void AllowClose() { _allowClose = true; Close(); }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    /// <summary>For tests: what the sun/moon switch does.</summary>
    internal void ChooseAppearance(AppTheme theme) => MacApp.ChooseTheme(_host, theme);
    internal bool UpdateOffered => _updateBar.IsVisible;
    internal bool DuplicateShown => _dupBar.IsVisible;

    /// <summary>Opens this Mac's disk image (or the release page) in the browser: an unsigned app can't replace itself.</summary>
    private async Task DownloadUpdate()
    {
        if (_updater?.Available is not { } r) return;
        var dmg = r.MacImage(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64);
        await OpenUri(dmg?.Url ?? r.Page);
        Dialogs.Message($"Your browser is downloading {ProductInfo.Name} {r.Version}. When it's done: quit {ProductInfo.Name} from the menu bar, " +
                        "open the downloaded disk image and drag the app onto Applications (choose Replace). Your settings are kept.").Show();
    }

    private async Task OpenUri(Uri uri)
    {
        if (GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(uri);
    }

    public void Refresh()
    {
        var now = DateTime.UtcNow;
        var dups = Service.Duplicates;
        _dupBar.IsVisible = dups.Count > 0;
        if (dups.Count > 0)
        {
            _dupTitle.Text = dups.Count == 1
                ? $"{Service.ProviderName} made a copy of an {ProductInfo.Name} folder"
                : $"{Service.ProviderName} made {dups.Count} copies of {ProductInfo.Name} folders";
            _dupDetail.Text = string.Join("\n", dups.Take(3).Select(d => $"• \"{d.Name}\" ({d.FileCount} file{(d.FileCount == 1 ? "" : "s")}, last changed {StatusWords.Ago(d.NewestUtc, now)})")) +
                (dups.Count > 3 ? $"\n• and {dups.Count - 3} more (see the log)" : "") +
                $"\nThis happens when two computers save at the same moment. {ProductInfo.Name} only uses \"{dups[0].CopyOf}\" and hasn't moved or deleted anything. " +
                $"Look inside, and delete the copy in {Service.ProviderName} when you're sure you don't need it.";
        }

        if (_updater != null)
        {
            var release = _updater.Available;
            bool show = release != null && _updater.State is UpdateState.Available or UpdateState.Downloading;
            bool appeared = show && !_updateBar.IsVisible;
            _updateBar.IsVisible = show;
            if (appeared || (_revealUpdate && _updater.State != UpdateState.Checking))
            {
                _revealUpdate = false;
                _scroller.Offset = new Vector(0, 0);
            }
            if (release != null)
            {
                _updateTitle.Text = release.IsPrerelease ? $"Test build {release.Version} is available" : $"{ProductInfo.Name} {release.Version} is available";
                _updateDetail.Text = $"You have {_updater.CurrentVersion}. Your settings are kept when you update.";
            }
        }

        var tone = StatusWords.Tone(Service.State);
        _headline.Text = StatusWords.Headline(Service.State);
        var last = StatusWords.LastSynced(Service.Workers);
        _detail.Text = Service.State == OverallState.InSync
            ? $"Last synced {StatusWords.Ago(last, now)}"
            : $"{Service.StateText}{(last != null ? $" · last synced {StatusWords.Ago(last, now)}" : "")}";
        var icon = Ui.Icon(Ui.ToneIcon(tone), 24, Ui.ToneBrush(tone));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        _heroIcon.Child = icon;
        Ui.Res(_heroIcon, Border.BorderBrushProperty, Ui.ToneBrush(tone));
        _heroIcon.BorderThickness = new Thickness(1.5);
        _pause.Content = Service.Settings.Paused ? Ui.ButtonContent("Resume", Ui.Sync) : Ui.ButtonContent("Pause…", Ui.Pause);

        _rows.Children.Clear();
        _rowTexts.Clear();
        foreach (var w in Service.Workers)
        {
            var status = string.IsNullOrEmpty(w.StatusText) ? w.Status.ToString() : w.StatusText;
            var wtone = StatusWords.Tone(w.Status);
            var details = (w.State.LastError is { Length: > 0 } err && wtone == StatusTone.Attention ? err : StatusWords.Details(w, now))
                          + (w.State.PendingPush ? " · upload pending" : "")
                          + (Service.RulesFor(w.Version.FolderName).Describe() is { } rules ? $" · {rules}" : "");
            Button? action = null;
            var worker = w;
            if (w.State.Conflict != null)
                action = Ui.Button("Choose which to keep...", () => Resolve(worker), ButtonKind.Primary);
            var restore = Ui.Button("Backups…", () => new RestoreWindow(_host, worker, Refresh).ShowDialog(this), ButtonKind.Ghost, Ui.Clock);
            _rows.Children.Add(VersionCard(w.Version, Ui.Pill(status, Ui.ToneBrush(wtone)), details, action, restore));
            _rowTexts.Add($"{w.Version.FolderName}: {status}");
        }
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v)))
        {
            _rows.Children.Add(VersionCard(v, Ui.Pill("Off", "InterfaceSyncMuted"),
                v.CanEnable ? "Not synced on this Mac." : "Not played on this Mac yet", null, null));
            _rowTexts.Add($"{v.FolderName}: off");
        }

        RefreshActivity(now);
        RefreshComputers(now);
    }

    private void RefreshActivity(DateTime now)
    {
        var entries = Service.Activity.List(6);
        var key = string.Join("|", entries.Select(e => e.Utc.Ticks)) + StatusWords.Ago(entries.FirstOrDefault()?.Utc, now);
        if (key == _activityKey) return;
        _activityKey = key;
        _activityRows.Children.Clear();
        _activityTexts.Clear();
        if (entries.Count == 0)
            _activityRows.Children.Add(Ui.Text("Nothing synced yet. Uploads, downloads and restores will appear here.", 12.5, brush: "InterfaceSyncMuted"));
        foreach (var e in entries)
        {
            _activityRows.Children.Add(ActivityWindow.Line(e, $"{e.Version} · {StatusWords.Ago(e.Utc, now)}{(e.FileCount > 0 ? $" · {e.FileCount} file{(e.FileCount == 1 ? "" : "s")}" : "")}"));
            _activityTexts.Add(e.Headline);
        }
        var all = Ui.Button("Show all activity", () => new ActivityWindow(_host).Show(), ButtonKind.Ghost, Ui.Clock);
        all.HorizontalAlignment = HorizontalAlignment.Left;
        _activityRows.Children.Add(all);
    }

    private void RefreshComputers(DateTime now)
    {
        if (now - _computersReadUtc > TimeSpan.FromSeconds(60))
        {
            _computersReadUtc = now;
            try { _computerList = Service.Computers(); } catch (Exception) { }
        }
        var key = string.Join("|", _computerList.Select(c => c.Name + c.LastSyncUtc.Ticks)) + StatusWords.Ago(_computerList.FirstOrDefault()?.LastSyncUtc, now);
        if (key == _computersKey) return;
        _computersKey = key;
        _computerRows.Children.Clear();
        _computerTexts.Clear();
        if (_computerList.Count == 0)
            _computerRows.Children.Add(Ui.Text($"Each computer appears here after it syncs with {ProductInfo.Name} 3.1 or later.", 12.5, brush: "InterfaceSyncMuted"));
        foreach (var c in _computerList)
        {
            var name = c.IsThisComputer ? $"{c.Name} (this computer)" : c.Name;
            var action = c.LastAction.Length > 0 ? $"{c.LastAction.ToLowerInvariant()} {StatusWords.Ago(c.LastSyncUtc, now)}" : $"synced {StatusWords.Ago(c.LastSyncUtc, now)}";
            var detail = string.Join(" · ", new[] { c.System, c.AppVersion.Length > 0 ? $"version {c.AppVersion}" : "", action, string.Join(", ", c.Versions) }.Where(x => x.Length > 0));
            var icon = Ui.Icon(Ui.Computer, 16, c.IsThisComputer ? "InterfaceSyncAccent" : "InterfaceSyncMuted");
            icon.Margin = new Thickness(0, 2, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var text = new StackPanel { Spacing = 1, Children = { Ui.Text(name, 13), Ui.Text(detail, 11.5, brush: "InterfaceSyncMuted") } };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(6, 4), Children = { icon, text } };
            Grid.SetColumn(text, 1);
            _computerRows.Children.Add(g);
            _computerTexts.Add(name);
        }
    }

    private static Border VersionCard(GameVersion version, Control pill, string details, Button? action, Button? restore)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Text(version.FolderName + (version.ClientVersion != null ? $"   {version.ClientVersion}" : ""), 14, FontWeight.SemiBold),
            Ui.Text(details, 12, brush: "InterfaceSyncMuted"),
        } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Children = { text, pill } };
        if (restore != null) { restore.Margin = new Thickness(6, 0, 0, 0); restore.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(restore, 3); g.Children.Add(restore); }
        pill.VerticalAlignment = VerticalAlignment.Center;
        pill.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(pill, 1);
        if (action != null) { action.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(action, 2); g.Children.Add(action); }
        var card = Ui.CardOf(g);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private void Resolve(VersionSync w)
    {
        if (w.State.Conflict is not { } c) return;
        Dialogs.ChooseKeep(w.Version.FolderName, c, keep =>
        {
            if (keep is { } k) _host.Run(() => Service.Resolve(w, k), Refresh);
        }).Show();
    }

    // For the headless UI tests.
    internal IReadOnlyList<string> Rows => _rowTexts;
    internal IReadOnlyList<string> ActivityLines => _activityTexts;
    internal IReadOnlyList<string> ComputerLines => _computerTexts;
    internal string Overall => Service.StateText;
}
