using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.Desktop;

/// <summary>
/// Every setting on one scrolling page, in sections (General, Sync, Cloud, Updates, Advanced) with links at the top
/// that jump to each. Each setting is a card: what it is and why, with its control on the right.
/// </summary>
internal sealed class SettingsPage : Page
{
    public static readonly string[] SectionNames = ["General", "Sync", "Cloud", "Updates", "Advanced"];

    private readonly StackPanel _body = new();
    private readonly Dictionary<string, Control> _sections = new();
    private readonly List<string> _titles = [];
    private readonly ScrollViewer _scroller;
    private readonly Dictionary<string, Button> _links = new();
    private readonly Border _endSpace = new();     // lets the last sections scroll up to the top
    private string _key = "";
    private string? _jumpedTo;

    public SettingsPage(MainWindow shell) : base(shell)
    {
        var jump = new WrapPanel { Margin = new Thickness(0, -6, 0, 6) };
        foreach (var name in SectionNames)
        {
            var section = name;
            var b = Ui.Button(L.T(section), () => JumpTo(section), ButtonKind.Ghost);
            b.Margin = new Thickness(0, 0, 4, 4);
            jump.Children.Add(b);
            _links[section] = b;
        }
        AutomationProperties.SetName(jump, L.T("Settings sections"));
        // The heading and section links stay put; the sections scroll below them.
        var top = new Border
        {
            Padding = new Thickness(32, 26, 32, 4), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel { Children = { Heading(L.T("Settings")), jump } },
        };
        _scroller = Scroll(new StackPanel { Children = { _body, _endSpace } });
        ((Border)_scroller.Content!).Padding = new Thickness(32, 4, 32, 32);
        _scroller.ScrollChanged += (_, _) => MarkCurrent();
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_scroller);
        Content = dock;
    }

    public override string Key => "Settings";

    public override void Arrive(object? argument)
    {
        _key = "";
        // after the page is laid out (its size can still be changing), or the scroll position is put back
        if (argument is string section) Avalonia.Threading.Dispatcher.UIThread.Post(() => JumpTo(section), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls so the section's heading is at the top, right under the section links.</summary>
    private void JumpTo(string section)
    {
        if (!_sections.TryGetValue(section, out var header)) return;
        _scroller.UpdateLayout();
        if (header.TranslatePoint(new Point(0, 0), _body) is not { } at) { header.BringIntoView(); return; }
        // Room below the last sections so their heading can reach the top too.
        var bodyTop = _body.TranslatePoint(new Point(0, 0), (Visual)_scroller.Content!)?.Y ?? 0;
        var y = Math.Max(0, bodyTop + at.Y - 4);
        _endSpace.Height = Math.Max(0, y + _scroller.Viewport.Height - (bodyTop + _body.Bounds.Height + 32));
        _scroller.UpdateLayout();
        _jumpedTo = section;
        _scroller.Offset = new Vector(0, y);
        MarkCurrent();
    }

    /// <summary>Highlights the link of the section being read.</summary>
    private void MarkCurrent()
    {
        string? current = null;
        var y = _scroller.Offset.Y;
        foreach (var name in SectionNames)
            if (_sections.TryGetValue(name, out var h) && h.TranslatePoint(new Point(0, 0), _body) is { } p && p.Y <= y + 24) current = name;
        // At the very bottom the last short sections can't reach the top: keep the one that was clicked.
        if (_jumpedTo != null && y >= _scroller.Extent.Height - _scroller.Viewport.Height - 1) current = _jumpedTo;
        else if (_jumpedTo != null && current != _jumpedTo) _jumpedTo = null;
        current ??= SectionNames[0];
        foreach (var (name, b) in _links)
            if (name == current) Ui.Res(b, Button.BackgroundProperty, "InterfaceSyncSelection");
            else b.ClearValue(Button.BackgroundProperty);
        CurrentSection = current;
    }

    /// <summary>For tests: the section whose link is highlighted.</summary>
    internal string CurrentSection { get; private set; } = "General";

    /// <summary>For tests: how far the section's heading is from the top of the visible area (0 = at the top).</summary>
    internal double SectionDistanceFromTop(string section) =>
        _sections[section].TranslatePoint(new Point(0, 0), _scroller)?.Y ?? double.NaN;

    internal void Jump(string section) => JumpTo(section);

    public override void Refresh()
    {
        var s = Service.Settings;
        var u = Host.Updater;
        var key = string.Join("|", s.Theme, Look.IsDark, s.Notifications, s.Language, s.Paused, s.PausedUntilUtc, s.CheckForUpdates, s.LastUpdateCheckUtc,
            Host.StartAtLogin?.IsEnabled, Host.StartAtLogin?.SwitchedOffNote, u?.State, u?.Available?.Version, Service.Drive?.MyDrivePath, Service.ProviderName,
            string.Join(",", Service.Versions.Select(v => $"{v.FolderName}{Service.IsEnabled(v)}{Service.RulesFor(v.FolderName).Describe()}")), L.Language);
        if (key == _key) return;
        _key = key;
        var offset = _scroller.Offset;
        _body.Children.Clear();
        _sections.Clear();
        _titles.Clear();
        Section("General", General());
        Section("Sync", SyncSection());
        Section("Cloud", CloudSection());
        Section("Updates", UpdatesSection());
        Section("Advanced", AdvancedSection());
        _scroller.Offset = offset;
    }

    private void Section(string name, IEnumerable<Control> rows)
    {
        var header = Brand.Section(L.T(name));
        _sections[name] = header;
        _body.Children.Add(header);
        foreach (var r in rows) _body.Children.Add(r);
    }

    /// <summary>For tests: the setting titles shown.</summary>
    internal IReadOnlyList<string> RowTitles => _titles;

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
            AutomationProperties.SetItemStatus(b, i == selected ? "selected" : "");
            panel.Children.Add(b);
        }
        return panel;
    }

    private static ToggleSwitch Switch(string name, bool on, bool enabled = true)
    {
        var t = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, IsChecked = on, IsEnabled = enabled };
        AutomationProperties.SetName(t, name);
        return t;
    }

    // ------------------------------------------------------------------ sections

    private IEnumerable<Control> General()
    {
        yield return Row(Ui.Paint, L.T("Appearance"), L.T("Dark is easy on the eyes at night; light suits a bright room. The sun/moon switch in the sidebar does the same."),
            Choice(L.T("Appearance"), [L.T("Dark"), L.T("Light")], Look.IsDark ? 0 : 1, i => DesktopApp.ChooseTheme(Host, i == 0 ? AppTheme.Dark : AppTheme.Light)));

        if (Host.StartAtLogin is { } startup)
        {
            var toggle = Switch(L.T(startup.Label), startup.IsEnabled);
            toggle.IsCheckedChanged += (_, _) => Actions.SetStartAtLogin(Host, toggle.IsChecked == true);
            var text = startup.SwitchedOffNote is { } off ? L.T(off)
                : Os.IsMac ? L.F("Start {0} quietly in the menu bar when you log in, so your settings are up to date before you play.", ProductInfo.Name)
                : L.T("Starts quietly in the tray when you sign in, so your settings are always up to date before you play.");
            yield return Row(Ui.Power, L.T(startup.Label), text, toggle);
        }

        var languages = new ComboBox { MinWidth = 180, ItemsSource = new[] { Os.IsMac ? L.T("Same as macOS") : L.T("Same as Windows") }.Concat(L.Languages.Select(l => l.Name)).ToList() };
        AutomationProperties.SetName(languages, L.T("Language"));
        var chosen = Service.Settings.Language;
        languages.SelectedIndex = chosen == null ? 0 : 1 + Array.FindIndex(L.Languages, l => l.Code == chosen);
        languages.SelectionChanged += (_, _) =>
        {
            var code = languages.SelectedIndex <= 0 ? null : L.Languages[languages.SelectedIndex - 1].Code;
            if (code == Service.Settings.Language) return;
            Service.SavePreference(s => s.Language = code);
            L.Use(code);
            Shell.ShowToast(L.F("Restart {0} to see the new language everywhere.", ProductInfo.Name), seconds: 10);
        };
        yield return Row(Ui.Info, L.T("Language"), L.T("German, French, Spanish and Portuguese translations are new: if something reads oddly, please tell us with Report a problem."), languages);

        yield return Row(Ui.Info, L.T("Notifications"),
            Os.IsMac
                ? L.T("\"Only when needed\": settings changed in two places, a sync problem, or an update (shown at the top of the menu, and the status window opens for a choice). \"All\" also notes downloads.")
                : L.T("\"Only when needed\": settings changed in two places, a sync problem, or an update. \"All\" also tells you when settings arrive from another computer. The tray icon always shows the state."),
            Choice(L.T("Notifications"), [L.T("All"), L.T("Only when needed"), L.T("None")], (int)Service.Settings.Notifications,
                i => { Service.SavePreference(s => s.Notifications = (NotificationLevel)i); _key = ""; Refresh(); }));
    }

    private IEnumerable<Control> SyncSection()
    {
        var s = Service.Settings;
        var pauseText = s.Paused
            ? s.PausedUntilUtc is { } until ? L.F("Paused until {0}. Nothing is uploaded or downloaded until then.", StatusWords.Until(until, DateTime.UtcNow)) : L.T("Paused until you resume it.")
            : L.T("Syncing is on. Pause it for a while if you want to try something out without it reaching your other computers.");
        Button pause = null!;
        pause = Ui.Button(s.Paused ? L.T("Resume") : L.T("Pause…"), () => Actions.Pause(pause, Host, Shell.Refresh), s.Paused ? ButtonKind.Primary : ButtonKind.Secondary, s.Paused ? Ui.Sync : Ui.Pause);
        yield return Row(Ui.Pause, L.T("Pause syncing"), pauseText, pause);

        foreach (var v in Service.Versions)
        {
            var version = v;
            var worker = Service.Workers.FirstOrDefault(w => PathUtil.SamePath(w.Version.FolderPath, v.FolderPath));
            bool on = Service.IsEnabled(v);
            var toggle = Switch(L.F("Sync {0}", v.FolderName), on, v.CanEnable);
            toggle.IsCheckedChanged += (_, _) => Actions.SetVersionEnabled(Host, version, toggle.IsChecked == true, Shell.Refresh);
            var controls = new List<Control>();
            if (worker != null) controls.Add(Ui.Button(L.T("Choose what syncs…"), () => Actions.ChooseWhatSyncs(Shell, worker)));
            controls.Add(toggle);
            var rules = Service.RulesFor(v.FolderName).Describe();
            yield return Row(Ui.Game, v.FolderName + (v.ClientVersion != null ? $"   {v.ClientVersion}" : ""),
                !v.CanEnable ? (Os.IsMac ? L.T("Not played on this Mac yet. Launch it once to sync it.") : L.T("Not played on this computer yet. Launch it once to sync it."))
                : !on ? (Os.IsMac ? L.T("Not synced on this Mac.") : L.T("Not synced on this computer."))
                : rules ?? L.T("Everything syncs: keybindings, macros, chat and addon settings of every character."), [.. controls]);
        }
        yield return Row(Ui.Shield, L.T("The one rule"),
            L.T("Quit the game on one computer before playing on another. Settings are synced only while the game is closed, so two computers playing at once can't both win; you'd be asked which to keep."));
    }

    private IEnumerable<Control> CloudSection()
    {
        var service = Service;
        var folder = service.Drive?.MyDrivePath is { } d ? PathUtil.Join(d, CloudLayout.AppFolderName) : null;
        var move = new ComboBox { MinWidth = 150, PlaceholderText = L.T("Move to…"), ItemsSource = CloudProviders.All.Select(p => p.DisplayName()).ToList() };
        AutomationProperties.SetName(move, L.T("Move to another cloud app"));
        var controls = new List<Control>();
        if (folder != null) controls.Add(Ui.Button(L.T("Open"), () => Actions.OpenFolder(Shell, folder), ButtonKind.Ghost, Ui.Folder));
        controls.Add(move);
        controls.Add(Ui.Button(L.T("Move"), () =>
        {
            if (move.SelectedIndex < 0) return;
            var target = CloudProviders.All[move.SelectedIndex];
            Core.Service.ProviderChangeResult? result = null;
            Host.Run(() => result = service.ChangeProvider(target, null, keepOnDevice: false),
                () => { _key = ""; Shell.Refresh(); Shell.ShowToast(result?.Message ?? L.T("Nothing changed."), seconds: 12); });
        }));
        yield return Row(Ui.Cloud, service.ProviderName,
            folder != null ? L.F("Your settings are kept in {0}. Only this folder is ever written to.", folder) : L.F("{0} wasn't found. Is its app running and signed in?", service.ProviderName),
            [.. controls]);
        var provider = service.Drive?.Provider ?? service.EffectiveProvider();
        var keep = new List<Control>();
        if (service.CanKeepOnDevice)
            keep.Add(Ui.Button(L.T("Keep on this device"), () =>
            {
                string? done = null;
                Host.Run(() => done = service.KeepCloudFolderOnDevice(), () => Shell.ShowToast(done != null ? L.F("{0} now always stays on this device.", done) : L.T("Nothing changed.")));
            }));
        yield return Row(Ui.Folder, Os.IsMac ? L.T("Keep it on this Mac") : L.T("Keep it on this computer"),
            (Os.IsMac ? L.F("Your cloud app must keep the {0} folder on this Mac, not online only.", CloudLayout.AppFolderName)
                      : L.F("Your cloud app must keep the {0} folder on this computer, not online only.", CloudLayout.AppFolderName))
            + " " + provider.KeepOfflineHint(Os.Host), [.. keep]);
    }

    private IEnumerable<Control> UpdatesSection()
    {
        var service = Service;
        if (Host.Updater is { } updater)
        {
            yield return UpdateCard(updater);
            var auto = Switch(L.T("Check for updates automatically"), service.Settings.CheckForUpdates);
            auto.IsCheckedChanged += (_, _) =>
            {
                var on = auto.IsChecked == true;
                service.SavePreference(s => s.CheckForUpdates = on);
                if (on) _ = updater.CheckAsync();
            };
            yield return Row(Ui.Sync, L.T("Check for updates automatically"),
                Os.IsWindows ? L.T("Checks GitHub once a day; updating takes a few seconds and keeps all your settings.") : L.T("Checks GitHub once a day. Your settings are kept when you update."),
                auto);
            var beta = Switch(L.T("Get beta versions"), updater.GetsBetas);
            beta.IsCheckedChanged += (_, _) =>
            {
                var on = beta.IsChecked == true;
                service.SavePreference(s => s.BetaUpdates = on);
                _ = updater.CheckAsync();
            };
            yield return Row(Ui.Flask, L.T("Get beta versions"),
                L.T("Try new versions before they're published. Betas are tested but may still have rough edges. Switch this off to get only published versions; you stay on your beta until the next one is published."),
                beta);
        }
        yield return Row(Ui.Info, L.F("Version {0}", ProductInfo.DisplayVersion),
            Updater.IsBetaVersion(ProductInfo.DisplayVersion) ? L.T("This is a beta version.") : L.T("See what changed in this version."),
            Ui.Button(L.T("What's new"), () => Actions.ShowWhatsNew(Shell)), Ui.Button(L.T("All releases"), () => Actions.OpenLink(Shell, Feedback.ReleasesPage), ButtonKind.Ghost));
    }

    /// <summary>
    /// The update, right here: checking, a newer version with What's new and Update now, the download in progress,
    /// up to date, or a check that failed (with Try again). It changes as the check and download go on.
    /// </summary>
    private Border UpdateCard(Updater updater)
    {
        var service = Service;
        var now = DateTime.UtcNow;
        var checkedAt = service.Settings.LastUpdateCheckUtc is { } at ? " " + L.F("Last checked {0}.", StatusWords.Ago(at, now)) : "";
        var current = updater.CurrentVersion;
        string icon, tone, title, detail;
        var buttons = new List<Control>();
        bool busy = false;
        switch (updater.State)
        {
            case UpdateState.Checking:
                (icon, tone, title, detail) = (Ui.Sync, "InterfaceSyncInfo", L.T("Checking for updates…"), L.F("You have {0}.", current));
                busy = true;
                break;
            case UpdateState.Available when updater.Available is { } r:
                (icon, tone) = (Ui.Download, "InterfaceSyncAccent");
                title = r.IsPrerelease ? L.F("Beta {0} is available", r.Version) : L.F("{0} {1} is available", ProductInfo.Name, r.Version);
                detail = Os.IsWindows ? L.F("You have {0}. Updating takes a few seconds and keeps all your settings.", current)
                                      : L.F("You have {0}. Your settings are kept when you update.", current);
                buttons.Add(Ui.Button(L.T("What's new"), () => Actions.OpenLink(Shell, r.Page), ButtonKind.Ghost));
                buttons.Add(Ui.Button(Os.IsWindows ? L.T("Update now") : L.T("Download update"), Host.InstallUpdate, ButtonKind.Primary, Ui.Download));
                break;
            case UpdateState.Downloading:
                (icon, tone, title, detail) = (Ui.Download, "InterfaceSyncAccent",
                    L.F("Downloading version {0}…", updater.Available?.Version ?? ""),
                    L.F("{0} closes and reopens by itself when the update is ready. Your settings are kept.", ProductInfo.Name));
                busy = true;
                break;
            case UpdateState.Failed:
                (icon, tone, title, detail) = (Ui.Warning, "InterfaceSyncWarning", L.T("Couldn't check for updates"), (updater.Problem ?? "") + checkedAt);
                buttons.Add(Ui.Button(L.T("Try again"), Host.CheckForUpdates));
                break;
            case UpdateState.UpToDate:
                (icon, tone) = (Ui.Check, "InterfaceSyncGood");
                title = updater.GetsBetas ? L.F("You have the newest version, betas included ({0})", current)
                    : updater.LatestPublished == null ? L.T("No release has been published yet")
                    : L.F("You have the latest version ({0})", current);
                detail = checkedAt.Trim();
                buttons.Add(Ui.Button(L.T("Check now"), Host.CheckForUpdates));
                break;
            default:
                (icon, tone, title, detail) = (Ui.Download, "InterfaceSyncMuted", L.F("Version {0}", current),
                    service.Settings.CheckForUpdates ? L.T("Checks GitHub once a day.") : L.T("Automatic checks are off."));
                buttons.Add(Ui.Button(L.T("Check now"), Host.CheckForUpdates));
                break;
        }
        _titles.Add(title);
        var bar = Ui.MessageBar(tone, icon, title, detail.Length > 0 ? detail : null, [.. buttons]);
        if (busy && bar.Child is Grid g && g.Children[1] is StackPanel text)
            text.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3, Margin = new Thickness(0, 6, 0, 0) });
        bar.Margin = new Thickness(0, 0, 0, 8);
        UpdateCardTitle = title;
        return bar;
    }

    /// <summary>For tests: what the update card says.</summary>
    internal string UpdateCardTitle { get; private set; } = "";

    private IEnumerable<Control> AdvancedSection()
    {
        var service = Service;
        yield return Row(Ui.Info, L.T("Help and questions"), L.T("How syncing works, what to do when settings changed in two places, online-only files and more."),
            Ui.Button(L.T("Open help"), () => Actions.OpenLink(Shell, Feedback.HelpPage)));
        yield return Row(Ui.Warning, L.T("Report a problem"), L.T("Opens a GitHub issue with the diagnostics filled in (folder names only). You check it before anything is sent."),
            Ui.Button(L.T("Report a problem…"), () => Actions.ReportProblem(Shell, Host)));
        yield return Row(Ui.Clock, L.T("Log"), L.F("Everything {0} did, in plain words. Kept for {1} days.", ProductInfo.Name, FileLog.KeepDays),
            Ui.Button(L.T("View log"), () => Actions.ViewLog(Shell, Host)), Ui.Button(L.T("Open folder"), () => Actions.OpenFolder(Shell, service.Paths.Logs), ButtonKind.Ghost, Ui.Folder));
        yield return Row(Ui.Info, L.T("Diagnostics"), L.T("Paths, versions and the recent log, to paste into a message when asking for help."),
            Ui.Button(L.T("Copy diagnostics"), () => Actions.CopyDiagnostics(Shell, Host)));
        yield return Row(Ui.Folder, L.T("Data folder"), L.F("Settings, sync state and backups: {0}", service.Paths.Data),
            Ui.Button(L.T("Open"), () => Actions.OpenFolder(Shell, service.Paths.Data), ButtonKind.Ghost, Ui.Folder));
        yield return Row(Ui.Sync, L.T("Run setup again"), L.T("Choose the game versions and cloud folder again, step by step. Nothing is synced until you finish."),
            Ui.Button(L.T("Run setup…"), () => Actions.RunSetup(Shell, Host)));
    }
}
