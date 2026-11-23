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
    private readonly ComboBox _appearance = new() { MinWidth = 150 };
    private readonly ToggleSwitch _startAtLogin = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly TextBlock _lastLine = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 220, FontFamily = new FontFamily("Menlo, monospace"), FontSize = 11, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _toggleLog;
    private bool _allowClose, _refreshingSwitch;

    public StatusWindow(IMacHost host, IStartAtLogin? startAtLogin = null)
    {
        _host = host;
        _startup = startAtLogin;
        Title = $"{ProductInfo.Name} {ProductInfo.Version}";
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
        _appearance.ItemsSource = MacApp.ThemeChoices.Select(c => c.Label).ToList();
        _appearance.SelectionChanged += (_, _) =>
        {
            if (_appearance.SelectedIndex < 0) return;
            var chosen = MacApp.ThemeChoices[_appearance.SelectedIndex].Theme;
            if (chosen != MacTheme.Mode) MacApp.ChooseTheme(_host, chosen);
        };
        _startAtLogin.IsVisible = _startup != null;
        _startAtLogin.IsCheckedChanged += (_, _) => { if (!_refreshingSwitch) ToggleStartAtLogin(); };
        var settings = Ui.Card(
            SettingRow(Ui.Cloud, new StackPanel { Children = { Ui.Text("Cloud folder", 13, FontWeight.SemiBold), _cloudPath } }, move),
            SettingRow(Ui.Power, new StackPanel { Children = { Ui.Text("Open at Login", 13, FontWeight.SemiBold), Ui.Text($"Start {ProductInfo.Name} when you log in to this Mac", 12, brush: "InterfaceSyncMuted") } }, _startAtLogin),
            SettingRow(Ui.Paint, Ui.Text("Appearance", 13, FontWeight.SemiBold), _appearance));
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
        root.Children.Add(heroCard);
        root.Children.Add(MacBrand.Section("Game versions"));
        root.Children.Add(_rows);
        root.Children.Add(MacBrand.Section("Settings"));
        root.Children.Add(settings);
        root.Children.Add(MacBrand.Section("Activity"));
        root.Children.Add(activity);
        root.Children.Add(tools);

        var (banner, _) = MacBrand.Banner("Your interface settings, on every computer");
        var dock = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        dock.Children.Add(banner);
        dock.Children.Add(new ScrollViewer { Content = root });
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

    /// <summary>For tests: pick an appearance as the player would.</summary>
    internal void ChooseAppearance(AppTheme theme) => _appearance.SelectedIndex = Array.FindIndex(MacApp.ThemeChoices, c => c.Theme == theme);

    public void Refresh()
    {
        var now = DateTime.UtcNow;
        var index = Array.FindIndex(MacApp.ThemeChoices, c => c.Theme == MacTheme.Mode);
        if (_appearance.SelectedIndex != index) _appearance.SelectedIndex = index;

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
            if (w.State.Conflict != null)
            {
                var worker = w;
                action = Ui.Button("Choose which to keep...", () => Resolve(worker), ButtonKind.Primary);
            }
            _rows.Children.Add(VersionCard(w.Version, Ui.Pill(status, Ui.ToneBrush(wtone)), details, action));
            _rowTexts.Add($"{w.Version.FolderName}: {status}");
        }
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v)))
        {
            _rows.Children.Add(VersionCard(v, Ui.Pill("Off", "InterfaceSyncMuted"),
                v.CanEnable ? "Not synced on this Mac." : "Not played on this Mac yet", null));
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

    private static Border VersionCard(GameVersion version, Control pill, string details, Button? action)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Text(version.FolderName + (version.ClientVersion != null ? $"   {version.ClientVersion}" : ""), 14, FontWeight.SemiBold),
            Ui.Text(details, 12, brush: "InterfaceSyncMuted"),
        } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { text, pill } };
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
