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
/// settings, recent activity and the rare tools. Closing hides it; Quit is in the menu.
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
    private readonly TextBlock _cloudPath = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly ComboBox _moveTo = new() { MinWidth = 150, PlaceholderText = "Move to..." };
    private readonly Updater? _updater;
    private readonly Border _updateBar;
    private readonly TextBlock _updateTitle = Ui.Text("", 14, FontWeight.SemiBold);
    private readonly TextBlock _updateDetail = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly Button _updateNow;
    private readonly ToggleSwitch _autoUpdate = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly TextBlock _updateStatus = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private bool _refreshingUpdate, _revealUpdate;
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
    private readonly ToggleSwitch _startAtLogin = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly TextBlock _lastLine = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 220, FontFamily = new FontFamily("Menlo, monospace"), FontSize = 11, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _toggleLog;
    private bool _allowClose, _refreshingSwitch;

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
        _pause = Ui.Button("Pause", () => _host.Run(() => Service.SetPaused(!Service.Settings.Paused), Refresh), icon: Ui.Pause);
        var syncNow = Ui.Button("Sync now", () => _host.Run(Service.SyncNow, Refresh), ButtonKind.Primary, Ui.Sync);
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _headline, _detail } };
        var heroButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _pause, syncNow } };
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { _heroIcon, heroText, heroButtons } };
        Grid.SetColumn(heroText, 1);
        Grid.SetColumn(heroButtons, 2);
        var heroCard = Ui.CardOf(hero);
        heroCard.Padding = new Thickness(18, 16);

        // settings
        _moveTo.ItemsSource = CloudProviders.All.Select(p => p.DisplayName()).ToList();
        var move = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _moveTo, Ui.Button("Move", Move) } };
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
            Ui.Button("Show folder", () => { if (Service.Duplicates.FirstOrDefault() is { } d) _ = Open(d.Path); }, ButtonKind.Secondary, Ui.Folder),
        } };
        var dupText = new StackPanel { Spacing = 4, Children = { _dupTitle, _dupDetail } };
        var dupGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { dupIcon, dupText, dupButtons } };
        Grid.SetColumn(dupText, 1);
        Grid.SetColumn(dupButtons, 2);
        _dupBar = Ui.CardOf(dupGrid);
        Ui.Res(_dupBar, Border.BorderBrushProperty, "InterfaceSyncWarning");
        _dupBar.BorderThickness = new Thickness(1.5);
        _dupBar.IsVisible = false;

        _autoUpdate.IsCheckedChanged += (_, _) =>
        {
            if (_refreshingUpdate || _updater == null) return;
            var on = _autoUpdate.IsChecked == true;
            Service.SavePreference(s => s.CheckForUpdates = on);
            if (on) _ = _updater.CheckAsync();
            Refresh();
        };
        _startAtLogin.IsVisible = _startup != null;
        _startAtLogin.IsCheckedChanged += (_, _) => { if (!_refreshingSwitch) ToggleStartAtLogin(); };
        var settings = Ui.Card(
            SettingRow(Ui.Cloud, new StackPanel { Children = { Ui.Text("Cloud folder", 13, FontWeight.SemiBold), _cloudPath } }, move),
            SettingRow(Ui.Power, new StackPanel { Children = { Ui.Text("Open at Login", 13, FontWeight.SemiBold), Ui.Text($"Start {ProductInfo.Name} when you log in to this Mac", 12, brush: "InterfaceSyncMuted") } }, _startAtLogin),
            SettingRow(Ui.Sync, new StackPanel { Children = { Ui.Text("Check for updates automatically", 13, FontWeight.SemiBold), _updateStatus } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Ui.Button("Check now", CheckForUpdates), _autoUpdate } }));
        if (_updater == null) ((StackPanel)settings.Child!).Children.RemoveAt(((StackPanel)settings.Child!).Children.Count - 1);
        if (_startup == null) ((StackPanel)settings.Child!).Children.RemoveAt(1);

        // activity
        _toggleLog = Ui.Button("Show all", () => { _log.IsVisible = !_log.IsVisible; _toggleLog!.Content = _log.IsVisible ? "Hide" : "Show all"; }, ButtonKind.Ghost);
        var activityHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _lastLine, _toggleLog } };
        _lastLine.VerticalAlignment = VerticalAlignment.Center;
        _lastLine.TextWrapping = TextWrapping.NoWrap;
        _lastLine.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_toggleLog, 1);
        var activity = Ui.Card(activityHead, _log);

        var tools = new WrapPanel { Margin = new Thickness(-6, 0, 0, 0) };
        tools.Children.Add(Ui.Button("Open backups", () => _ = Open(Service.Paths.Backups), ButtonKind.Ghost, Ui.Folder));
        tools.Children.Add(Ui.Button("Open log folder", () => _ = Open(Service.Paths.Logs), ButtonKind.Ghost, Ui.Folder));
        tools.Children.Add(Ui.Button("Copy diagnostics", () => _ = CopyDiagnostics(), ButtonKind.Ghost, Ui.Info));
        tools.Children.Add(Ui.Button("About", () => Dialogs.About().Show(), ButtonKind.Ghost, Ui.Info));

        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        root.Children.Add(_updateBar);
        root.Children.Add(_dupBar);
        root.Children.Add(heroCard);
        root.Children.Add(MacBrand.Section("Game versions"));
        root.Children.Add(_rows);
        root.Children.Add(MacBrand.Section("Settings"));
        root.Children.Add(settings);
        root.Children.Add(MacBrand.Section("Activity"));
        root.Children.Add(activity);
        root.Children.Add(tools);

        var (banner, _) = MacBrand.Banner("Your interface settings, on every computer", () => MacApp.ToggleTheme(_host));
        var dock = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        dock.Children.Add(banner);
        dock.Children.Add(_scroller = new ScrollViewer { Content = root });
        Content = dock;
        Refresh();
    }

    private static Grid SettingRow(string icon, Control middle, Control? right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 4) };
        var i = Ui.Icon(icon, 18);
        i.Margin = new Thickness(0, 0, 14, 0);
        middle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(middle, 1);
        g.Children.Add(i);
        g.Children.Add(middle);
        if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(right, 2); g.Children.Add(right); }
        return g;
    }

    private void ToggleStartAtLogin()
    {
        if (_startup == null) return;
        try
        {
            if (_startAtLogin.IsChecked == true)
            {
                if (_startup.CannotEnableReason is { } why) { Dialogs.Message(why).Show(); }
                else { _startup.Enable(); Service.Log.Info($"{_startup.Label}: on."); }
            }
            else if (_startup.IsEnabled) { _startup.Disable(); Service.Log.Info($"{_startup.Label}: off."); }
        }
        catch (Exception e) { Dialogs.Message($"Couldn't change {_startup.Label}: {e.Message}").Show(); }
        Refresh();
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
            _refreshingUpdate = true;
            _autoUpdate.IsChecked = Service.Settings.CheckForUpdates;
            _refreshingUpdate = false;
            _updateStatus.Text = _updater.Describe();
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
        _pause.Content = Service.Settings.Paused ? Ui.ButtonContent("Resume", Ui.Sync) : Ui.ButtonContent("Pause", Ui.Pause);

        _rows.Children.Clear();
        _rowTexts.Clear();
        foreach (var w in Service.Workers)
        {
            var status = string.IsNullOrEmpty(w.StatusText) ? w.Status.ToString() : w.StatusText;
            var wtone = StatusWords.Tone(w.Status);
            var details = (w.State.LastError is { Length: > 0 } err && wtone == StatusTone.Attention ? err : StatusWords.Details(w, now))
                          + (w.State.PendingPush ? " · upload pending" : "");
            Button? action = null;
            var worker = w;
            if (w.State.Conflict != null)
                action = Ui.Button("Choose which to keep...", () => Resolve(worker), ButtonKind.Primary);
            var restore = Ui.Button("Restore...", () => new RestoreWindow(_host, worker, Refresh).ShowDialog(this), ButtonKind.Ghost, Ui.Clock);
            _rows.Children.Add(VersionCard(w.Version, Ui.Pill(status, Ui.ToneBrush(wtone)), details, action, restore));
            _rowTexts.Add($"{w.Version.FolderName}: {status}");
        }
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v)))
        {
            _rows.Children.Add(VersionCard(v, Ui.Pill("Off", "InterfaceSyncMuted"),
                v.CanEnable ? "Not synced on this Mac." : "Not played on this Mac yet", null, null));
            _rowTexts.Add($"{v.FolderName}: off");
        }

        _cloudPath.Text = Service.Drive?.MyDrivePath is { } d ? $"{Service.ProviderName}: {PathUtil.Join(d, CloudLayout.AppFolderName)}" : $"{Service.ProviderName}: not found";
        _refreshingSwitch = true;
        _startAtLogin.IsChecked = _startup?.IsEnabled == true;
        _refreshingSwitch = false;
        var lines = Service.RecentLog(200).Reverse().Select(l => l.ToString()).ToList();
        _log.Text = string.Join("\n", lines);
        _lastLine.Text = lines.Count > 0 ? lines[0] : "Nothing yet.";
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

    private void Move()
    {
        if (_moveTo.SelectedIndex < 0) return;
        var target = CloudProviders.All[_moveTo.SelectedIndex];
        ProviderChangeResult? result = null;
        _host.Run(() => result = Service.ChangeProvider(target, null, keepOnDevice: false),
            () => { Dialogs.Message(result?.Message ?? "Nothing changed.").Show(); Refresh(); });
    }

    private async Task Open(string folder)
    {
        Directory.CreateDirectory(folder);
        if (GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    private async Task CopyDiagnostics()
    {
        if (GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(Service.Diagnostics());
            Dialogs.Message("Diagnostics copied. Paste them into your message or GitHub issue.").Show();
        }
    }

    // For the headless UI tests.
    internal IReadOnlyList<string> Rows => _rowTexts;
    internal string Overall => Service.StateText;
}
