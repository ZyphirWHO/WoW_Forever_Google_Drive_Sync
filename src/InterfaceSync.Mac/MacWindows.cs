using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Updates;

using InterfaceSync.Core.Localization;

namespace InterfaceSync.Mac;

/// <summary>Things the status window, the settings window and the menu can all do.</summary>
internal static class MacActions
{
    public static async void OpenFolder(TopLevel? from, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (from?.Launcher is { } launcher) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
            else System.Diagnostics.Process.Start("open", $"\"{folder}\"");
        }
        catch (Exception e) { Dialogs.Message(L.F("Couldn't open {0}: {1}", folder, e.Message), warning: true).Show(); }
    }

    public static async void OpenLink(TopLevel? from, Uri page)
    {
        try
        {
            if (from?.Launcher is { } launcher) await launcher.LaunchUriAsync(page);
            else System.Diagnostics.Process.Start("open", page.ToString());
        }
        catch (Exception) { }
    }

    /// <summary>A question with two buttons; <paramref name="yes"/> runs only when the main one is clicked.</summary>
    public static void Ask(string heading, string message, string yesLabel, Action yes)
    {
        Window? w = null;
        var go = Ui.Button(yesLabel, () => { w!.Close(); yes(); }, ButtonKind.Primary);
        go.IsDefault = true;
        var cancel = Ui.Button(L.T("Cancel"), () => w!.Close(), ButtonKind.Ghost);
        cancel.IsCancel = true;
        w = new Window
        {
            Title = ProductInfo.Name, Icon = MacIcons.App, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel { Margin = new Thickness(22, 20, 22, 16), Spacing = 8, Children =
            {
                Ui.Text(heading, 15, FontWeight.SemiBold),
                Ui.Text(message, 13),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, go } },
            } },
        };
        w.Show();
    }

    public static void ReportProblem(TopLevel? from, IMacHost host) =>
        Ask(L.T("Report a problem"),
            L.T("This opens a new issue on GitHub with the diagnostics filled in: versions, what was found and the recent log, with your folder paths, computer name and user name replaced by placeholders. Nothing is sent until you check it, describe what happened and click Submit. You need a free GitHub account."),
            L.T("Open GitHub"), () =>
            {
                string text = "";
                host.Run(() => text = host.Service.RedactedDiagnostics(), () => OpenLink(from, Feedback.IssueUrl(text)));
            });

    public static async void CopyDiagnostics(TopLevel? from, IMacHost host)
    {
        if (from?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(host.Service.Diagnostics());
        Dialogs.Message(L.T("Paste them into your message or GitHub issue. They include your folder paths."), L.T("Diagnostics copied")).Show();
    }

    public static void ShowWhatsNew(TopLevel? from, string? notes = null)
    {
        notes ??= ReleaseNotes.Current;
        if (notes == null) { OpenLink(from, Feedback.ReleasesPage); return; }
        new TextWindow(L.F("What's new in {0} {1}", ProductInfo.Name, ReleaseNotes.BaseVersion(ProductInfo.DisplayVersion)), ReleaseNotes.ToPlainText(notes), false,
            (L.T("All releases"), () => OpenLink(from, Feedback.ReleasesPage))).Show();
    }

    public static void ViewLog(IMacHost host)
    {
        var lines = host.Service.RecentLog(500).Reverse().Select(l => l.ToString());
        TextWindow? w = null;
        w = new TextWindow(L.T("Log (newest first)"), string.Join("\n", lines), true, ("Open log folder", () => OpenFolder(w, host.Service.Paths.Logs)));
        w.Show();
    }

    /// <summary>Pause choices as a menu under a button (or Resume straight away when paused).</summary>
    public static void Pause(Control anchor, IMacHost host, Action refresh)
    {
        if (host.Service.Settings.Paused) { host.Run(() => host.Service.SetPaused(false), refresh); return; }
        var menu = new MenuFlyout();
        foreach (var (label, until) in StatusWords.PauseChoices(DateTime.UtcNow))
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => host.Run(() => host.Service.SetPaused(true, until), refresh);
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
}

/// <summary>A read-only text window: What's new, the log, one activity entry's files.</summary>
internal sealed class TextWindow : Window
{
    public TextWindow(string title, string text, bool monospace, (string Label, Action Click)? extra = null)
    {
        Title = $"{ProductInfo.Name}: {title}";
        Icon = MacIcons.App;
        Width = 680;
        Height = 540;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = monospace ? TextWrapping.NoWrap : TextWrapping.Wrap,
            FontFamily = monospace ? new FontFamily("Menlo, monospace") : FontFamily.Default, FontSize = monospace ? 11 : 13,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(box, title);
        var close = Ui.Button(L.T("Close"), Close, ButtonKind.Primary);
        close.IsDefault = close.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        if (extra is { } e) buttons.Children.Add(Ui.Button(e.Label, e.Click, ButtonKind.Ghost));
        buttons.Children.Add(close);
        var heading = Ui.Text(title, 18, FontWeight.SemiBold, bottom: 10);
        var frame = Ui.CardOf(new ScrollViewer { Content = box });
        frame.Margin = new Thickness(0);
        var dock = new DockPanel { Margin = new Thickness(22, 18) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(heading);
        dock.Children.Add(buttons);
        dock.Children.Add(frame);
        Content = dock;
    }

    internal string Body => ((TextBox)((ScrollViewer)((Border)((DockPanel)Content!).Children[2]).Child!).Content!).Text ?? "";
}

/// <summary>Asks for a snapshot's name.</summary>
internal sealed class InputWindow : Window
{
    private readonly TextBox _box = new() { MaxLength = Core.Backup.BackupStore.MaxSnapshotNameLength };

    public InputWindow(string heading, string message, string ok, string initial, Action<string> done)
    {
        Title = ProductInfo.Name;
        Icon = MacIcons.App;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _box.Text = initial;
        AutomationProperties.SetName(_box, heading);
        var save = Ui.Button(ok, () => { var v = Value; if (v.Length == 0) return; Close(); done(v); }, ButtonKind.Primary);
        save.IsDefault = true;
        var cancel = Ui.Button(L.T("Cancel"), Close, ButtonKind.Ghost);
        cancel.IsCancel = true;
        _box.TextChanged += (_, _) => save.IsEnabled = Value.Length > 0;
        Content = new StackPanel { Margin = new Thickness(22, 20, 22, 16), Spacing = 8, Children =
        {
            Ui.Text(heading, 15, FontWeight.SemiBold),
            Ui.Text(message, 12.5, brush: "InterfaceSyncMuted"),
            _box,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, save } },
        } };
        Opened += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public string Value => (_box.Text ?? "").Trim();
}

/// <summary>Every setting in one place, one page per topic, like the Windows settings window.</summary>
internal sealed class SettingsWindow : Window
{
    public static readonly string[] PageNames = ["General", "Sync", "Cloud", "Backups", "Updates", "Advanced"];
    private static readonly string[] PageIcons = [Ui.Paint, Ui.Sync, Ui.Cloud, Ui.Clock, Ui.Sync, Ui.Info];

    private readonly IMacHost _host;
    private readonly IStartAtLogin? _startup;
    private readonly Updater? _updater;
    private readonly Action? _checkForUpdates;
    private readonly ListBox _nav = new() { Width = 190, Margin = new Thickness(14, 14, 6, 14), Background = Brushes.Transparent };
    private readonly StackPanel _page = new() { Margin = new Thickness(20, 6, 24, 20) };
    private readonly ScrollViewer _scroller;
    private string _current = "General";
    private readonly List<string> _titles = [];

    public SettingsWindow(IMacHost host, IStartAtLogin? startup = null, Updater? updater = null, Action? checkForUpdates = null, string page = "General")
    {
        _host = host;
        _startup = startup;
        _updater = updater;
        _checkForUpdates = checkForUpdates;
        Title = $"{ProductInfo.Name}: {L.T("Settings")}";
        Icon = MacIcons.App;
        Width = 860;
        Height = 640;
        MinWidth = 680;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _nav.ItemsSource = PageNames.Select((n, i) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 6), Children = { Ui.Icon(PageIcons[i], 16), Ui.Text(L.T(n), 13.5) } }).ToList();
        AutomationProperties.SetName(_nav, L.T("Settings pages"));
        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedIndex >= 0 && PageNames[_nav.SelectedIndex] != _current) ShowPage(PageNames[_nav.SelectedIndex]); };
        var (banner, _) = MacBrand.Banner(L.T("Settings"), () => { MacApp.ToggleTheme(_host); ShowPage(_current); });
        var divider = new Border { Width = 1 };
        Ui.Res(divider, Border.BackgroundProperty, "InterfaceSyncBorder");
        var dock = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        DockPanel.SetDock(_nav, Dock.Left);
        DockPanel.SetDock(divider, Dock.Left);
        dock.Children.Add(banner);
        dock.Children.Add(_nav);
        dock.Children.Add(divider);
        dock.Children.Add(_scroller = new ScrollViewer { Content = _page });
        Content = dock;
        if (_updater != null) _updater.Changed += OnUpdaterChanged;
        Closed += (_, _) => { if (_updater != null) _updater.Changed -= OnUpdaterChanged; };
        ShowPage(PageNames.Contains(page) ? page : "General");
    }

    private void OnUpdaterChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_current == "Updates") ShowPage("Updates"); });

    internal string CurrentPage => _current;
    internal IReadOnlyList<string> RowTitles => _titles;

    internal void ShowPage(string name)
    {
        _current = name;
        _nav.SelectedIndex = Array.IndexOf(PageNames, name);
        _page.Children.Clear();
        _titles.Clear();
        _page.Children.Add(MacBrand.Section(L.T(name)));
        foreach (var row in name switch
                 {
                     "General" => General(),
                     "Sync" => SyncPage(),
                     "Cloud" => CloudPage(),
                     "Backups" => BackupsPage(),
                     "Updates" => UpdatesPage(),
                     _ => AdvancedPage(),
                 })
            _page.Children.Add(row);
        _scroller.Offset = new Vector(0, 0);
    }

    /// <summary>One setting: icon, title and explanation on the left, controls on the right, on a card.</summary>
    private Border Row(string icon, string title, string? description, params Control[] controls)
    {
        _titles.Add(title);
        var i = Ui.Icon(icon, 18);
        i.VerticalAlignment = VerticalAlignment.Top;
        i.Margin = new Thickness(0, 1, 14, 0);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Text(title, 13.5, FontWeight.SemiBold) } };
        if (description != null) text.Children.Add(Ui.Text(description, 12, brush: "InterfaceSyncMuted"));
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        foreach (var c in controls) right.Children.Add(c);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { i, text, right } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
        var card = Ui.CardOf(g);
        card.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetName(card, title);
        return card;
    }

    /// <summary>Joined choice buttons; the chosen one is gold.</summary>
    private static Control Choice(string name, string[] options, int selected, Action<int> chose)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        AutomationProperties.SetName(panel, name);
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var b = Ui.Button(options[i], () => chose(index), i == selected ? ButtonKind.Primary : ButtonKind.Secondary);
            panel.Children.Add(b);
        }
        return panel;
    }

    private IEnumerable<Control> General()
    {
        yield return Row(Ui.Paint, L.T("Appearance"), L.T("Dark is easy on the eyes at night; light suits a bright room. The sun/moon switch at the top of every window does the same."),
            Choice(L.T("Appearance"), [L.T("Dark"), L.T("Light")], MacTheme.IsDark ? 0 : 1, i => { MacApp.ChooseTheme(_host, i == 0 ? AppTheme.Dark : AppTheme.Light); ShowPage("General"); }));
        if (_startup != null)
        {
            var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, IsChecked = _startup.IsEnabled };
            AutomationProperties.SetName(toggle, L.T(_startup.Label));
            toggle.IsCheckedChanged += (_, _) => { ToggleStartAtLogin(toggle.IsChecked == true); };
            yield return Row(Ui.Power, L.T(_startup.Label), L.F("Start {0} quietly in the menu bar when you log in, so your settings are up to date before you play.", ProductInfo.Name), toggle);
        }
        var languages = new ComboBox { MinWidth = 180, ItemsSource = new[] { L.T("Same as macOS") }.Concat(L.Languages.Select(l => l.Name)).ToList() };
        AutomationProperties.SetName(languages, L.T("Language"));
        var chosen = _host.Service.Settings.Language;
        languages.SelectedIndex = chosen == null ? 0 : 1 + Array.FindIndex(L.Languages, l => l.Code == chosen);
        languages.SelectionChanged += (_, _) =>
        {
            var code = languages.SelectedIndex <= 0 ? null : L.Languages[languages.SelectedIndex - 1].Code;
            if (code == _host.Service.Settings.Language) return;
            _host.Service.SavePreference(s => s.Language = code);
            L.Use(code);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ShowPage("General");
                Dialogs.Message(L.F("Restart {0} to see the new language everywhere (the menu and windows already open).", ProductInfo.Name), L.T("Language changed")).Show();
            });
        };
        yield return Row(Ui.Info, L.T("Language"), L.T("German, French, Spanish and Portuguese translations are new: if something reads oddly, please tell us with Report a problem."), languages);
        yield return Row(Ui.Info, L.T("Notifications"),
            L.T("\"Only when needed\": settings changed in two places, a sync problem, or an update (shown at the top of the menu, and the status window opens for a choice). \"All\" also notes downloads."),
            Choice(L.T("Notifications"), [L.T("All"), L.T("Only when needed"), L.T("None")], (int)_host.Service.Settings.Notifications,
                i => { _host.Service.SavePreference(s => s.Notifications = (NotificationLevel)i); ShowPage("General"); }));
    }

    internal void ToggleStartAtLogin(bool on)
    {
        if (_startup == null) return;
        try
        {
            if (on && !_startup.IsEnabled)
            {
                if (_startup.CannotEnableReason is { } why) Dialogs.Message(why).Show();
                else { _startup.Enable(); _host.Service.Log.Info($"{_startup.Label}: on."); }
            }
            else if (!on && _startup.IsEnabled) { _startup.Disable(); _host.Service.Log.Info($"{_startup.Label}: off."); }
        }
        catch (Exception e) { Dialogs.Message(L.F("Couldn't change {0}: {1}", _startup.Label, e.Message), warning: true).Show(); }
    }

    private IEnumerable<Control> SyncPage()
    {
        var s = _host.Service.Settings;
        var pauseText = s.Paused
            ? s.PausedUntilUtc is { } until ? L.F("Paused until {0}. Nothing is uploaded or downloaded until then.", StatusWords.Until(until, DateTime.UtcNow)) : L.T("Paused until you resume it.")
            : L.T("Syncing is on. Pause it for a while if you want to try something out without it reaching your other computers.");
        Button pause = null!;
        pause = Ui.Button(s.Paused ? L.T("Resume") : L.T("Pause…"), () => MacActions.Pause(pause, _host, () => ShowPage("Sync")), s.Paused ? ButtonKind.Primary : ButtonKind.Secondary, s.Paused ? Ui.Sync : Ui.Pause);
        yield return Row(Ui.Pause, L.T("Pause syncing"), pauseText, pause);

        foreach (var v in _host.Service.Versions)
        {
            var version = v;
            var worker = _host.Service.Workers.FirstOrDefault(w => PathUtil.SamePath(w.Version.FolderPath, v.FolderPath));
            bool on = _host.Service.IsEnabled(v);
            var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, IsChecked = on, IsEnabled = v.CanEnable };
            AutomationProperties.SetName(toggle, L.F("Sync {0}", v.FolderName));
            toggle.IsCheckedChanged += (_, _) => SetVersionEnabled(version, toggle.IsChecked == true);
            var controls = new List<Control>();
            if (worker != null) controls.Add(Ui.Button(L.T("Choose what syncs…"), () =>
            {
                var w = new SyncChoicesWindow(_host.Service, worker, rules => _host.Run(() => _host.Service.SetRules(worker, rules), () => ShowPage("Sync")));
                w.Show();
            }));
            controls.Add(toggle);
            var rules = _host.Service.RulesFor(v.FolderName).Describe();
            yield return Row(Ui.Game, v.FolderName + (v.ClientVersion != null ? $"   {v.ClientVersion}" : ""),
                !v.CanEnable ? L.T("Not played on this Mac yet. Launch it once to sync it.")
                : !on ? L.T("Not synced on this Mac.")
                : rules ?? L.T("Everything syncs: keybindings, macros, chat and addon settings of every character."), [.. controls]);
        }
        yield return Row(Ui.Shield, L.T("The one rule"),
            L.T("Quit the game on one computer before playing on another. Settings are synced only while the game is closed, so two computers playing at once can't both win; you'd be asked which to keep."));
    }

    private void SetVersionEnabled(GameVersion v, bool on)
    {
        if (_host.Service.IsEnabled(v) == on) return;
        _host.Run(() =>
        {
            var settings = _host.Service.Settings;
            settings.EnabledVersions[v.FolderName] = on;
            _host.Service.SaveSettings(settings);
            _host.Service.Log.Info($"{v.FolderName}: syncing switched {(on ? "on" : "off")} on this Mac.");
        }, () => ShowPage("Sync"));
    }

    private IEnumerable<Control> CloudPage()
    {
        var service = _host.Service;
        var folder = service.Drive?.MyDrivePath is { } d ? PathUtil.Join(d, CloudLayout.AppFolderName) : null;
        var move = new ComboBox { MinWidth = 150, PlaceholderText = L.T("Move to…"), ItemsSource = CloudProviders.All.Select(p => p.DisplayName()).ToList() };
        AutomationProperties.SetName(move, L.T("Move to another cloud app"));
        var controls = new List<Control>();
        if (folder != null) controls.Add(Ui.Button(L.T("Open"), () => MacActions.OpenFolder(this, folder), ButtonKind.Ghost, Ui.Folder));
        controls.Add(move);
        controls.Add(Ui.Button(L.T("Move"), () =>
        {
            if (move.SelectedIndex < 0) return;
            var target = CloudProviders.All[move.SelectedIndex];
            ProviderChangeResultHolder result = new();
            _host.Run(() => result.Value = service.ChangeProvider(target, null, keepOnDevice: false),
                () => { Dialogs.Message(result.Value?.Message ?? L.T("Nothing changed.")).Show(); ShowPage("Cloud"); });
        }));
        yield return Row(Ui.Cloud, service.ProviderName,
            folder != null ? L.F("Your settings are kept in {0}. Only this folder is ever written to.", folder) : L.F("{0} wasn't found. Is its app running and signed in?", service.ProviderName),
            [.. controls]);
        var provider = service.Drive?.Provider ?? service.EffectiveProvider();
        yield return Row(Ui.Folder, L.T("Keep it on this Mac"),
            L.F("Your cloud app must keep the {0} folder on this Mac, not online only.", CloudLayout.AppFolderName) + " " + provider.KeepOfflineHint(HostOs.MacOS));
    }

    private sealed class ProviderChangeResultHolder { public Core.Service.ProviderChangeResult? Value; }

    private IEnumerable<Control> BackupsPage()
    {
        var service = _host.Service;
        long bytes = 0;
        try { bytes = FolderScanner.AllFiles(new PhysicalFileSystem(), service.Paths.Backups).Sum(f => f.Size); } catch (Exception) { }
        yield return Row(Ui.Clock, L.T("How backups work"),
            L.F("Before anything on this Mac or in the cloud is replaced, the files are copied to a backup first. The newest {0} are kept per game version, plus your snapshots (kept until you delete them) and a copy made before each of the last {1} game updates. Backups use {2}.",
                Core.Backup.BackupStore.KeepPerVersion, Core.Backup.BackupStore.KeepGameUpdates, FormatSize(bytes)),
            Ui.Button(L.T("Open folder"), () => MacActions.OpenFolder(this, service.Paths.Backups), ButtonKind.Ghost, Ui.Folder));
        foreach (var w in service.Workers)
        {
            var worker = w;
            var list = service.Backups.List(w.Version.FolderName);
            int regular = list.Count(b => !b.Pinned && !b.GameUpdate), snaps = list.Count(b => b.Pinned);
            yield return Row(Ui.Game, w.Version.FolderName,
                StatusWords.BackupCounts(list),
                Ui.Button(L.T("Backups and snapshots…"), () => new RestoreWindow(_host, worker, () => ShowPage("Backups")).Show()));
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private IEnumerable<Control> UpdatesPage()
    {
        var service = _host.Service;
        if (_updater != null)
        {
            var auto = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, IsChecked = service.Settings.CheckForUpdates };
            AutomationProperties.SetName(auto, L.T("Check for updates automatically"));
            auto.IsCheckedChanged += (_, _) =>
            {
                var on = auto.IsChecked == true;
                service.SavePreference(s => s.CheckForUpdates = on);
                if (on) _ = _updater.CheckAsync();
            };
            var status = _updater.Describe() + (service.Settings.LastUpdateCheckUtc is { } at && _updater.State != UpdateState.Checking
                ? " " + L.F("Last checked {0}.", StatusWords.Ago(at, DateTime.UtcNow)) : "");
            yield return Row(Ui.Sync, L.T("Check for updates"), status + " " + L.T("Checks GitHub once a day. Your settings are kept when you update."),
                Ui.Button(L.T("Check now"), () => (_checkForUpdates ?? (() => _ = _updater.CheckAsync()))()), auto);
        }
        yield return Row(Ui.Info, L.F("Version {0}", ProductInfo.DisplayVersion),
            ProductInfo.DisplayVersion.Contains("-dev", StringComparison.OrdinalIgnoreCase)
                ? L.T("This is a test build: it updates to newer test builds and releases.") : L.T("See what changed in this version."),
            Ui.Button(L.T("What's new"), () => MacActions.ShowWhatsNew(this)), Ui.Button(L.T("All releases"), () => MacActions.OpenLink(this, Feedback.ReleasesPage), ButtonKind.Ghost));
    }

    private IEnumerable<Control> AdvancedPage()
    {
        var service = _host.Service;
        yield return Row(Ui.Info, L.T("Help and questions"), L.T("How syncing works, what to do when settings changed in two places, online-only files and more."),
            Ui.Button(L.T("Open help"), () => MacActions.OpenLink(this, Feedback.HelpPage)));
        yield return Row(Ui.Warning, L.T("Report a problem"), L.T("Opens a GitHub issue with the diagnostics filled in (folder names only). You check it before anything is sent."),
            Ui.Button(L.T("Report a problem…"), () => MacActions.ReportProblem(this, _host)));
        yield return Row(Ui.Clock, L.T("Log"), L.F("Everything {0} did, in plain words. Kept for {1} days.", ProductInfo.Name, FileLog.KeepDays),
            Ui.Button(L.T("View log"), () => MacActions.ViewLog(_host)), Ui.Button(L.T("Open folder"), () => MacActions.OpenFolder(this, service.Paths.Logs), ButtonKind.Ghost, Ui.Folder));
        yield return Row(Ui.Info, L.T("Diagnostics"), L.T("Paths, versions and the recent log, to paste into a message when asking for help."),
            Ui.Button(L.T("Copy diagnostics"), () => MacActions.CopyDiagnostics(this, _host)));
        yield return Row(Ui.Folder, L.T("Data folder"), L.F("Settings, sync state and backups: {0}", service.Paths.Data),
            Ui.Button(L.T("Open"), () => MacActions.OpenFolder(this, service.Paths.Data), ButtonKind.Ghost, Ui.Folder));
    }
}

/// <summary>"Choose what syncs" for one game version: tick the characters and addons this Mac syncs.</summary>
internal sealed class SyncChoicesWindow : Window
{
    private readonly List<(CheckBox Box, string Key)> _characters = [];
    private readonly List<(CheckBox Box, string Name)> _addons = [];

    public SyncChoicesWindow(Core.Service.SyncService service, VersionSync worker, Action<SyncRules> save)
    {
        Title = L.F("{0}: choose what syncs", worker.Version.FolderName);
        Icon = MacIcons.App;
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var current = service.RulesFor(worker.Version.FolderName);
        var choices = service.ChoicesFor(worker);

        Control Column(string heading, IEnumerable<(string key, string label, bool on)> items, bool characters, string empty)
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var (key, label, on) in items)
            {
                var box = new CheckBox { Content = label, IsChecked = on };
                AutomationProperties.SetName(box, label);
                list.Children.Add(box);
                if (characters) _characters.Add((box, key)); else _addons.Add((box, key));
            }
            if (list.Children.Count == 0) list.Children.Add(Ui.Text(empty, 12.5, brush: "InterfaceSyncMuted"));
            var card = Ui.CardOf(new ScrollViewer { Content = list });
            card.Margin = new Thickness(0, 6, 8, 0);
            var head = Ui.Text(heading, 14, FontWeight.SemiBold);
            var dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
            dock.Children.Add(card);
            return dock;
        }

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var left = Column(L.T("Characters"), choices.Characters.Select(c => (c.Key, c.Label, !current.Characters.Contains(c.Key, StringComparer.OrdinalIgnoreCase))), true, L.T("No characters yet: log in with one first."));
        var right = Column(L.T("Addons"), choices.Addons.Select(a => (a, a, !current.Addons.Contains(a, StringComparer.OrdinalIgnoreCase))), false, L.T("No addon settings yet."));
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);

        var ok = Ui.Button(L.T("Save"), () => { Close(); save(Rules); }, ButtonKind.Primary);
        ok.IsDefault = true;
        var cancel = Ui.Button(L.T("Cancel"), Close, ButtonKind.Ghost);
        cancel.IsCancel = true;
        var header = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10), Children =
        {
            Ui.Text(L.F("Choose what syncs: {0}", worker.Version.FolderName), 18, FontWeight.SemiBold),
            Ui.Text(L.T("Ticked characters and addons sync on this Mac. Untick one to keep this Mac's own settings for it: they aren't uploaded or replaced, and the cloud copy stays as it is for your other computers. Account-wide settings (keybindings, macros) always sync. Ticking one again downloads the cloud's copy, after backing up this Mac's."), 12.5, brush: "InterfaceSyncMuted"),
        } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, ok } };
        var dock = new DockPanel { Margin = new Thickness(22, 18) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(buttons);
        dock.Children.Add(columns);
        Content = dock;
    }

    public SyncRules Rules => new()
    {
        Characters = _characters.Where(c => c.Box.IsChecked != true).Select(c => c.Key).ToList(),
        Addons = _addons.Where(a => a.Box.IsChecked != true).Select(a => a.Name).ToList(),
    };

    internal IReadOnlyList<string> CharacterNames => _characters.Select(c => (string)c.Box.Content!).ToList();
    internal IReadOnlyList<string> AddonNames => _addons.Select(a => a.Name).ToList();
    internal void SetCharacter(int i, bool on) => _characters[i].Box.IsChecked = on;
    internal void SetAddon(int i, bool on) => _addons[i].Box.IsChecked = on;
}

/// <summary>Every recorded sync event, newest first; click one to see its files.</summary>
internal sealed class ActivityWindow : Window
{
    private readonly List<ActivityEntry> _entries;

    public ActivityWindow(IMacHost host)
    {
        Title = $"{ProductInfo.Name}: {L.T("Activity")}";
        Icon = MacIcons.App;
        Width = 760;
        Height = 560;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _entries = host.Service.Activity.List().ToList();
        var list = new StackPanel { Spacing = 2 };
        string? day = null;
        var now = DateTime.UtcNow;
        foreach (var e in _entries)
        {
            var heading = ActivityStore.DayHeading(e.Utc, now);
            if (heading != day)
            {
                day = heading;
                list.Children.Add(Ui.Text(heading, 12, FontWeight.SemiBold, "InterfaceSyncAccent", bottom: 2));
            }
            list.Children.Add(Line(e, $"{e.Utc.ToLocalTime().ToString("t", L.Culture)} · {e.Version}{(e.FileCount > 0 ? " · " + L.N(e.FileCount, "{0} file", "{0} files") : "")}"));
        }
        if (_entries.Count == 0) list.Children.Add(Ui.Text(L.T("Nothing synced yet."), 13, brush: "InterfaceSyncMuted"));
        var close = Ui.Button(L.T("Close"), Close, ButtonKind.Primary);
        close.IsDefault = close.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
            Children = { Ui.Button(L.T("Technical log"), () => MacActions.ViewLog(host), ButtonKind.Ghost, Ui.Clock), close } };
        var heading2 = Ui.Text(L.T("Activity"), 18, FontWeight.SemiBold, bottom: 10);
        var card = Ui.CardOf(new ScrollViewer { Content = list });
        card.Margin = new Thickness(0);
        var dock = new DockPanel { Margin = new Thickness(22, 18) };
        DockPanel.SetDock(heading2, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(heading2);
        dock.Children.Add(buttons);
        dock.Children.Add(card);
        Content = dock;
    }

    internal int EntryCount => _entries.Count;

    /// <summary>One activity line: icon, headline and detail; a button that shows the files when it has any.</summary>
    internal static Control Line(ActivityEntry e, string detail)
    {
        var (icon, brush) = e.Kind switch
        {
            ActivityKind.Uploaded => (Ui.Upload, "InterfaceSyncAccent"),
            ActivityKind.Downloaded => (Ui.Download, "InterfaceSyncGood"),
            ActivityKind.Conflict => (Ui.Warning, "InterfaceSyncWarning"),
            _ => (Ui.Clock, "InterfaceSyncAccent"),
        };
        var i = Ui.Icon(icon, 16, brush);
        i.Margin = new Thickness(0, 2, 12, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        var text = new StackPanel { Spacing = 1, Children = { Ui.Text(e.Headline, 13), Ui.Text(detail, 11.5, brush: "InterfaceSyncMuted") } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { i, text } };
        Grid.SetColumn(text, 1);
        if (e.Files.Count == 0) { g.Margin = new Thickness(6, 4); return g; }
        var b = new Button { Content = g, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4) };
        b.Resources["ButtonBackground"] = Brushes.Transparent;
        AutomationProperties.SetName(b, e.Headline);
        AutomationProperties.SetHelpText(b, $"{detail}. {L.T("Press Enter to see the files.")}");
        b.Click += (_, _) => new TextWindow(e.Headline, DetailsText(e), false).Show();
        return b;
    }

    internal static string DetailsText(ActivityEntry e)
    {
        var lines = new List<string> { e.Headline, $"{e.Version} · {e.Utc.ToLocalTime().ToString("f", L.Culture)}", "" };
        foreach (var group in e.Files.Select(SettingsItem.Parse).GroupBy(i => i.Area, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(L.T(group.Key));
            foreach (var item in group) lines.Add($"    {item.RelativePath.Replace('\\', '/')}{(item.CharacterLabel is { } c ? $"   ({c})" : "")}");
        }
        if (e.FileCount > e.Files.Count) lines.Add("…" + L.F("and {0} more", e.FileCount - e.Files.Count));
        return string.Join("\n", lines);
    }
}
