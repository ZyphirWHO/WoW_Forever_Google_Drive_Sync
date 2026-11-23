using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Mac;

/// <summary>
/// First run on a Mac, as a short wizard like the Windows one: Welcome, Your setup, Your settings (only when the
/// cloud already has settings), Finish, then each version's first sync with its result and a Finish button.
/// </summary>
internal sealed class SetupWindow : Window
{
    private readonly IMacHost _host;
    private SyncService Service => _host.Service;

    private readonly TextBlock _wowPath = Ui.Text("", 12.5, brush: "InterfaceSyncMuted");
    private readonly StackPanel _versions = new() { Spacing = 2 };
    private readonly ComboBox _provider = new() { MinWidth = 260 };
    private readonly TextBlock _cloudPath = Ui.Text("", 12.5, brush: "InterfaceSyncMuted");
    private readonly TextBlock _notes = Ui.Text("", 12, brush: "InterfaceSyncWarning", bottom: 8);
    private readonly IStartAtLogin? _startup;
    private readonly ToggleSwitch _startAtLogin = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly TextBlock _startAtLoginNote = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly TextBlock _summary = Ui.Text("", 13);
    private readonly TextBlock _choicesIntro = Ui.Text("", 13, brush: "InterfaceSyncMuted", bottom: 12);
    private readonly StackPanel _choicesPanel = new();
    private readonly List<(string version, RadioButton useCloud, RadioButton useMine)> _choicesMade = [];

    private readonly StackPanel _welcomePage = new();
    private readonly StackPanel _setupPage = new();
    private readonly StackPanel _choicesPage = new();
    private readonly StackPanel _finalPage = new();
    private readonly StackPanel _progressPage = new() { IsVisible = false };
    private readonly TextBlock _progressHeading = Ui.Text("Running the first sync...", 20, FontWeight.SemiBold, bottom: 10);
    private readonly StackPanel _results = new() { Spacing = 4 };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock _afterword = Ui.Text("", 13, brush: "InterfaceSyncMuted");

    private readonly Border _stepsHost = new();
    private readonly Button _back, _cancel, _next, _finish;
    private int _pageIndex = -1;
    private bool _canStart, _syncing, _syncStarted, _filling;
    private readonly TextBlock _bannerSubtitle;

    internal static readonly string HowItWorks =
        "• When you quit the game, this Mac's interface settings (keybindings, macros, addon settings) are copied to your cloud folder.\n" +
        "• When you open another computer, it copies in the newer settings before you play.\n" +
        "• Quit the game on one computer before playing on another. Every change is backed up first, and nothing is synced while the game runs.";

    public SetupWindow(IMacHost host, IStartAtLogin? startAtLogin = null)
    {
        _startup = startAtLogin;
        _host = host;
        Title = $"{ProductInfo.Name} setup";
        Icon = MacIcons.App;
        Width = 700;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _back = Ui.Button("Back", () => ShowPage(_pageIndex - 1));
        _cancel = Ui.Button("Cancel", Close, ButtonKind.Ghost);
        _next = Ui.Button("Next", Next, ButtonKind.Primary);
        _next.IsDefault = true;
        _finish = Ui.Button("Finish", () => { Started = true; Close(); }, ButtonKind.Primary);
        _finish.IsVisible = false;
        _finish.IsEnabled = false;
        (var banner, _bannerSubtitle) = MacBrand.Banner($"Welcome! Let's set up {ProductInfo.Name} on this Mac.", () => MacApp.ToggleTheme(_host));

        BuildPages();
        var pages = new StackPanel { Margin = new Thickness(28, 8, 28, 16) };
        foreach (var page in AllPages) pages.Children.Add(page);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _next, _finish } };
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _back, right } };
        Grid.SetColumn(right, 1);
        var footer = new Border { Padding = new Thickness(28, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = footerGrid };
        Ui.Res(footer, Border.BorderBrushProperty, "InterfaceSyncBorder");

        var dock = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        DockPanel.SetDock(_stepsHost, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(banner);
        dock.Children.Add(_stepsHost);
        dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = pages });
        Content = dock;

        _provider.SelectionChanged += (_, _) => { if (!_filling) ProviderChanged(); };
        Fill();
        ShowPage(0);
    }

    /// <summary>True once the player finished setup (Start syncing, then Finish).</summary>
    public bool Started { get; private set; }

    private StackPanel[] AllPages => [_welcomePage, _setupPage, _choicesPage, _finalPage, _progressPage];

    private StackPanel[] Pages => _choicesMade.Count > 0
        ? [_welcomePage, _setupPage, _choicesPage, _finalPage]
        : [_welcomePage, _setupPage, _finalPage];

    private string[] StepNames => _choicesMade.Count > 0
        ? ["Welcome", "Your setup", "Your settings", "Finish"]
        : ["Welcome", "Your setup", "Finish"];

    private static TextBlock PageTitle(string text) => Ui.Text(text, 22, FontWeight.SemiBold, bottom: 2);
    private static TextBlock Lead(string text) => Ui.Text(text, 13, brush: "InterfaceSyncMuted", bottom: 14);

    private static Control Feature(string icon, string title, string text)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var i = Ui.Icon(icon, 24);
        i.VerticalAlignment = VerticalAlignment.Top;
        i.Margin = new Thickness(2, 2, 14, 0);
        var body = new StackPanel { Spacing = 2, Children = { Ui.Text(title, 14, FontWeight.SemiBold), Ui.Text(text, 12.5, brush: "InterfaceSyncMuted") } };
        Grid.SetColumn(body, 1);
        g.Children.Add(i);
        g.Children.Add(body);
        return Ui.CardOf(g);
    }

    private void BuildPages()
    {
        _welcomePage.Children.Add(PageTitle($"Welcome to {ProductInfo.Name}"));
        _welcomePage.Children.Add(Lead("Keep your keybindings, macros and addon settings the same on every computer you play on. Setup takes about a minute."));
        _welcomePage.Children.Add(Feature(Ui.Cloud, "Saved when you quit",
            "When you quit the game, this Mac's interface settings are copied to your own cloud folder (Google Drive, OneDrive, Dropbox or iCloud Drive)."));
        _welcomePage.Children.Add(Feature(Ui.Sync, "Ready on every computer", "When you open another computer, it copies in the newer settings before you play."));
        _welcomePage.Children.Add(Feature(Ui.Shield, "Safe by design",
            "Quit the game on one computer before playing on another. Every change is backed up first, and nothing is synced while the game runs. " +
            "Your game files and addons themselves are never touched."));

        _setupPage.Children.Add(PageTitle("Check what was found"));
        _setupPage.Children.Add(Lead($"{ProductInfo.Name} looked for World of Warcraft and your cloud folder on this Mac. If anything is wrong, change it here."));
        _setupPage.Children.Add(Ui.Card(
            Ui.Header(Ui.Game, "World of Warcraft", Ui.Button("Change...", () => _ = ChooseWow())),
            _wowPath, Ui.Text("Game versions to sync", 13, FontWeight.SemiBold), _versions));
        _setupPage.Children.Add(Ui.Card(
            Ui.Header(Ui.Cloud, "Cloud folder", Ui.Button("Change...", () => _ = ChooseCloud())),
            _provider, Ui.Text("Your settings are kept in", 13, FontWeight.SemiBold), _cloudPath));
        _setupPage.Children.Add(_notes);

        _choicesPage.Children.Add(PageTitle("Which settings should this Mac use?"));
        _choicesPage.Children.Add(_choicesIntro);
        _choicesPage.Children.Add(_choicesPanel);

        _finalPage.Children.Add(PageTitle("Almost done"));
        _finalPage.Children.Add(Lead("A couple of preferences, then the first sync runs."));
        if (_startup != null)
        {
            var why = _startup.CannotEnableReason;
            _startAtLogin.IsChecked = why == null;
            _startAtLogin.IsEnabled = why == null;
            _startAtLoginNote.Text = why ?? $"Open {ProductInfo.Name} when you log in (recommended), so your settings are always synced when you quit the game.";
            _finalPage.Children.Add(Ui.Card(Ui.Header(Ui.Power, "Start automatically", _startAtLogin), _startAtLoginNote));
        }
        _finalPage.Children.Add(Ui.Card(Ui.Header(Ui.Check, "Ready to sync"), _summary));

        _progressPage.Children.Add(_progressHeading);
        _progressPage.Children.Add(Ui.CardOf(_results));
        _progressPage.Children.Add(_bar);
        _progressPage.Children.Add(_afterword);
    }

    private void ShowPage(int index)
    {
        if (_syncStarted) return;
        _pageIndex = Math.Clamp(index, 0, Pages.Length - 1);
        var current = Pages[_pageIndex];
        foreach (var page in AllPages) page.IsVisible = page == current;
        _stepsHost.Child = Ui.Steps(StepNames, _pageIndex);
        if (current == _finalPage) UpdateSummary();
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        if (_syncStarted || _pageIndex < 0) return;
        bool last = _pageIndex == Pages.Length - 1;
        _back.IsVisible = _pageIndex > 0;
        _next.Content = last ? "Start syncing" : "Next";
        _next.IsEnabled = _pageIndex == 0 || _canStart;
    }

    private void Next()
    {
        if (_pageIndex == Pages.Length - 1) { if (_canStart) StartSyncing(); }
        else ShowPage(_pageIndex + 1);
    }

    private void UpdateSummary()
    {
        var versions = _choices.Where(c => c.Box.IsChecked == true).Select(c => c.Version.FolderName).ToList();
        var provider = Service.Drive?.Provider.DisplayName() ?? Service.EffectiveProvider().DisplayName();
        _summary.Text = versions.Count == 0
            ? "No game version is ticked. Go back and tick at least one."
            : $"{string.Join(", ", versions)} will sync through {provider}. Click Start syncing to run the first sync now.";
    }

    private sealed record VersionChoice(GameVersion Version, CheckBox Box);
    private readonly List<VersionChoice> _choices = [];

    private void Fill()
    {
        var wow = Service.Wow;
        var drive = Service.Drive;
        _wowPath.Text = wow is { Found: true } ? string.Join("\n", wow.Installs.Select(i => i.Root)) : "Not found: choose its folder";

        _choices.Clear();
        _versions.Children.Clear();
        var notLaunched = new List<string>();
        foreach (var v in Service.Versions)
        {
            if (!v.CanEnable) { notLaunched.Add(v.FolderName); continue; }
            bool on = !Service.Settings.EnabledVersions.TryGetValue(v.FolderName, out var saved) || saved;
            var box = new CheckBox { Content = new TextBlock { Text = v.FolderName + (v.ClientVersion != null ? $"  ({v.ClientVersion})" : "") }, IsChecked = on };   // TextBlock: a string would turn "_retail_" into a shortcut, showing "retail_"
            box.IsCheckedChanged += (_, _) => UpdateStart();
            _choices.Add(new VersionChoice(v, box));
            _versions.Children.Add(box);
        }
        if (_choices.Count == 0) _versions.Children.Add(Ui.Text("No game version that has been played on this Mac was found.", 12, brush: "InterfaceSyncMuted"));

        var provider = drive?.Provider ?? Service.EffectiveProvider();
        var all = Service.DetectAllProviders();
        _filling = true;
        _provider.ItemsSource = CloudProviders.All.Select(p => all[p].Found ? p.DisplayName() : $"{p.DisplayName()} (not found on this Mac)").ToList();
        _provider.SelectedIndex = Array.IndexOf(CloudProviders.All, provider);
        _filling = false;
        _cloudPath.Text = drive is { Found: true }
            ? PathUtil.Join(drive.MyDrivePath!, CloudLayout.AppFolderName)
            : $"Not found: choose your {provider.DisplayName()} folder";

        var notes = new List<string>();
        if (notLaunched.Count > 0)
            notes.Add($"Not shown because they haven't been played on this Mac yet: {string.Join(", ", notLaunched)}.");
        if (drive is { Found: true })
            notes.Add($"{drive.Provider.DisplayName()} can keep files online-only. {drive.Provider.KeepOfflineHint(Core.Abstractions.HostOs.MacOS)}");
        _notes.Text = string.Join("\n\n", notes);
        _notes.IsVisible = notes.Count > 0;
        FillChoices();
        UpdateStart();
        if (_pageIndex >= 0) ShowPage(Math.Min(_pageIndex, Pages.Length - 1));
    }

    /// <summary>Per version whose cloud copy already has settings: download them, or upload this Mac's instead (newer preselected).</summary>
    private void FillChoices()
    {
        _choicesPanel.Children.Clear();
        _choicesMade.Clear();
        var previews = Service.PreviewFirstSync().Where(p => p.NeedsChoice).ToList();
        _choicesPanel.IsVisible = previews.Count > 0;
        if (previews.Count == 0) return;
        _choicesIntro.Text = $"{previews[0].ProviderName} already has settings from another computer. For each game version, pick which settings " +
                             "this Mac should use. The newer copy is picked for you, and the other is backed up either way.";
        foreach (var p in previews)
        {
            var group = $"first-sync-{p.Version}";
            var useCloud = new RadioButton { GroupName = group, Content = new TextBlock { Text = p.CloudOptionText("Mac"), TextWrapping = TextWrapping.Wrap }, IsChecked = p.Suggested == FirstSyncChoice.UseCloud };
            var useMine = new RadioButton { GroupName = group, Content = new TextBlock { Text = p.LocalOptionText("Mac"), TextWrapping = TextWrapping.Wrap }, IsChecked = p.Suggested == FirstSyncChoice.UseThisComputer };
            _choicesPanel.Children.Add(Ui.Card(Ui.Text(p.Version, 14, FontWeight.SemiBold, bottom: 2), useCloud, useMine));
            _choicesMade.Add((p.Version, useCloud, useMine));
        }
    }

    private void UpdateStart()
    {
        _canStart = Service.Wow is { Found: true } && Service.Drive is { Found: true } && _choices.Any(c => c.Box.IsChecked == true);
        UpdateFooter();
    }

    private void ProviderChanged()
    {
        if (_provider.SelectedIndex < 0) return;
        Service.Settings.CloudProvider = CloudProviders.All[_provider.SelectedIndex];
        Service.Settings.DriveOverride = null;
        Service.SaveSettings(Service.Settings);
        Fill();
    }

    private async Task ChooseWow()
    {
        var path = await PickFolder("Choose your World of Warcraft folder (the one that contains _retail_)");
        if (path == null) return;
        Service.Settings.WowInstallOverride = path;
        Service.SaveSettings(Service.Settings);
        Fill();
    }

    private async Task ChooseCloud()
    {
        var provider = Service.EffectiveProvider();
        var path = await PickFolder(provider == CloudProvider.GoogleDrive ? "Choose your Google Drive \"My Drive\" folder" : $"Choose your {provider.DisplayName()} folder");
        if (path == null) return;
        Service.Settings.CloudProvider ??= provider;
        Service.Settings.DriveOverride = path;
        Service.SaveSettings(Service.Settings);
        Fill();
    }

    private async Task<string?> PickFolder(string title)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private void StartSyncing()
    {
        var settings = Service.Settings;
        foreach (var c in _choices) settings.EnabledVersions[c.Version.FolderName] = c.Box.IsChecked == true;
        foreach (var (version, _, useMine) in _choicesMade)
            if (settings.EnabledVersions.TryGetValue(version, out var on) && on)
                settings.FirstSyncChoices[version] = useMine.IsChecked == true ? FirstSyncChoice.UseThisComputer : FirstSyncChoice.UseCloud;
        settings.SetupComplete = true;
        settings.CloudProvider ??= Service.EffectiveProvider();
        Service.SaveSettings(settings);
        ApplyStartAtLogin();

        _syncing = true;
        _syncStarted = true;
        foreach (var page in AllPages) page.IsVisible = page == _progressPage;
        _stepsHost.Child = Ui.Steps(StepNames, StepNames.Length);
        _back.IsVisible = _next.IsVisible = _cancel.IsVisible = false;
        _finish.IsVisible = true;
        _bannerSubtitle.Text = "Running the first sync";
        var workers = Service.Workers;
        var rows = workers.Select(w => Ui.Text($"{w.Version.FolderName}: waiting...", 13)).ToList();
        foreach (var r in rows) _results.Children.Add(r);

        _host.Run(() =>
        {
            int problems = 0;
            for (int i = 0; i < workers.Count; i++)
            {
                var w = workers[i];
                var row = rows[i];
                Dispatcher.UIThread.Post(() => row.Text = $"{w.Version.FolderName}: syncing...");
                bool first = !w.State.Joined;
                SyncOutcome outcome;
                try { outcome = Service.SyncNow(w); }
                catch (Exception e) { outcome = SyncOutcome.Failed; w.State.LastError = e.Message; }
                var (text, problem) = FirstSyncReport.Describe(w, outcome, first, "Mac");
                if (problem) problems++;
                Dispatcher.UIThread.Post(() =>
                {
                    row.Text = $"{w.Version.FolderName}: {text}";
                    if (problem) row.Foreground = MacTheme.Error;
                });
            }
            Dispatcher.UIThread.Post(() => FinishProgress(workers.Count, problems));
        });
    }

    private void FinishProgress(int versions, int problems)
    {
        _syncing = false;
        _bar.IsVisible = false;
        _bannerSubtitle.Text = problems == 0 ? "All set" : "Almost there";
        _progressHeading.Text = versions == 0 ? "Setup complete. No game version is switched on."
            : problems == 0 ? "You're all set." : "Setup complete, but something needs a look (shown in red).";
        _afterword.Text = $"{ProductInfo.Name} keeps running in the menu bar: its icon at the top right of the screen.\n\n" +
                          "What to do now: just play. When you quit the game, your settings are copied to the cloud within a minute. " +
                          $"On your other computer, open {ProductInfo.Name} with the game closed and it copies them in. " +
                          "Click the menu-bar icon any time to see what it did or change cloud folder.\n\n" +
                          RestartLine;
        _finish.IsEnabled = true;
        _finish.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_syncing) { e.Cancel = true; return; }        // never close mid-copy
        if (_syncStarted) Started = true;                  // closing after the sync counts as Finish
        base.OnClosing(e);
    }

    // For the headless UI tests.
    internal bool CanStart => _canStart;
    internal string CurrentStep => _syncStarted ? "Syncing" : StepNames[_pageIndex];
    internal void ClickNext() => Next();
    internal IReadOnlyList<string> ChoiceVersions => _choicesMade.Select(c => c.version).ToList();
    internal void ChooseThisComputer(string version) => _choicesMade.First(c => c.version == version).useMine.IsChecked = true;
    internal int VersionCount => _choices.Count;
    internal IReadOnlyList<string> ProviderChoices => (_provider.ItemsSource as IEnumerable<string>)?.ToList() ?? [];
    private void ApplyStartAtLogin()
    {
        if (_startup == null || !_startAtLogin.IsEnabled) return;
        try
        {
            if (_startAtLogin.IsChecked == true) _startup.Enable();
            else if (_startup.IsEnabled) _startup.Disable();
            Service.Log.Info($"{_startup.Label}: {(_startAtLogin.IsChecked == true ? "on" : "off")}.");
        }
        catch (Exception e) { Service.Log.Warn($"Couldn't change {_startup.Label}: {e.Message}"); }
    }

    private string RestartLine => _startup?.IsEnabled == true
        ? $"It opens by itself when you log in. macOS may show \"Background Items Added\": that's this. Switch it off in the menu-bar menu ({_startup.Label})."
        : $"Open {ProductInfo.Name} again after restarting this Mac, or switch on Open at Login in the menu-bar menu.";

    internal bool StartAtLoginChecked { get => _startAtLogin.IsChecked == true; set => _startAtLogin.IsChecked = value; }

    internal void ClickStart() { if (_canStart) StartSyncing(); }
    internal bool IsFinished => _finish.IsEnabled;
    internal IReadOnlyList<string> Results => _results.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
