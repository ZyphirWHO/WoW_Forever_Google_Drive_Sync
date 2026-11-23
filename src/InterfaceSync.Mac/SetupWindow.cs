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
/// First run on a Mac: what was found (each with Change...), game versions to sync, the cloud app,
/// then each version's first sync with its result and a Finish button. Same rules as the Windows window.
/// </summary>
internal sealed class SetupWindow : Window
{
    private readonly IMacHost _host;
    private SyncService Service => _host.Service;

    private readonly TextBlock _wowPath = Dialogs.Text("");
    private readonly StackPanel _versions = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox _provider = new() { MinWidth = 260, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _cloudPath = Dialogs.Text("");
    private readonly TextBlock _notes = Dialogs.Text("", 12);
    private readonly IStartAtLogin? _startup;
    private readonly CheckBox _startAtLogin = new() { Content = new TextBlock { Text = $"Open {ProductInfo.Name} when I log in to this Mac (recommended)" }, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _startAtLoginNote = Dialogs.Text("", 12);
    private readonly Button _start;
    private readonly StackPanel _setupPage = new();
    private readonly StackPanel _progressPage = new() { IsVisible = false };
    private readonly TextBlock _progressHeading = Dialogs.Text("Running the first sync...", 15, FontWeight.SemiBold);
    private readonly StackPanel _results = new();
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _afterword = Dialogs.Text("");
    private readonly Button _finish;
    private bool _filling;
    private readonly StackPanel _choicesPanel = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly List<(string version, RadioButton useCloud, RadioButton useMine)> _choicesMade = [];

    internal static readonly string HowItWorks =
        "• When you quit the game, this Mac's interface settings (keybindings, macros, addon settings) are copied to your cloud folder.\n" +
        "• When you open another computer, it copies in the newer settings before you play.\n" +
        "• Quit the game on one computer before playing on another. Every change is backed up first, and nothing is synced while the game runs.";
    private bool _syncing;

    public SetupWindow(IMacHost host, IStartAtLogin? startAtLogin = null)
    {
        _startup = startAtLogin;
        _host = host;
        Title = $"{ProductInfo.Name} setup";
        Icon = MacIcons.App;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _start = Dialogs.Button("Start syncing", StartSyncing, isDefault: true);
        _finish = Dialogs.Button("Finish", () => { Started = true; Close(); }, isDefault: true);
        _finish.IsEnabled = false;

        _setupPage.Children.Add(Dialogs.Text($"Welcome to {ProductInfo.Name}", 17, FontWeight.SemiBold));
        _setupPage.Children.Add(Dialogs.Text(HowItWorks, 12));
        _setupPage.Children.Add(Dialogs.Text("It found these on this Mac. Check them, then start syncing.", 14, FontWeight.SemiBold));
        _setupPage.Children.Add(Row("World of Warcraft:", _wowPath, Dialogs.Button("Change...", () => _ = ChooseWow())));
        _setupPage.Children.Add(Dialogs.Text("Sync these game versions:"));
        _setupPage.Children.Add(_versions);
        _setupPage.Children.Add(Dialogs.Text("Cloud folder:"));
        _setupPage.Children.Add(_provider);
        _setupPage.Children.Add(Row("", _cloudPath, Dialogs.Button("Change...", () => _ = ChooseCloud())));
        _setupPage.Children.Add(_choicesPanel);
        _setupPage.Children.Add(_notes);
        if (_startup != null)
        {
            var why = _startup.CannotEnableReason;
            _startAtLogin.IsChecked = why == null;
            _startAtLogin.IsEnabled = why == null;
            _startAtLoginNote.Text = why ?? "";
            _startAtLoginNote.IsVisible = why != null;
            _startAtLoginNote.Foreground = MacTheme.Muted;
            _setupPage.Children.Add(_startAtLogin);
            _setupPage.Children.Add(_startAtLoginNote);
        }
        _setupPage.Children.Add(Dialogs.Buttons(Dialogs.Button("Cancel", Close), _start));

        _progressPage.Children.Add(_progressHeading);
        _progressPage.Children.Add(_results);
        _progressPage.Children.Add(_bar);
        _progressPage.Children.Add(_afterword);
        _progressPage.Children.Add(Dialogs.Buttons(_finish));

        var root = new StackPanel();
        root.Children.Add(_setupPage);
        root.Children.Add(_progressPage);
        Content = new Border { Padding = new Thickness(18), Child = root };

        _provider.SelectionChanged += (_, _) => { if (!_filling) ProviderChanged(); };
        Fill();
    }

    /// <summary>True once the player finished setup (Start syncing, then Finish).</summary>
    public bool Started { get; private set; }

    private static Grid Row(string caption, Control value, Control button)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var label = Dialogs.Text(caption);
        label.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        Grid.SetColumn(button, 2);
        g.Children.Add(label);
        g.Children.Add(value);
        g.Children.Add(button);
        return g;
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
        if (_choices.Count == 0) _versions.Children.Add(Dialogs.Text("No game version that has been played on this Mac was found.", 12));

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
        FillChoices();
        UpdateStart();
    }

    /// <summary>Per version whose cloud copy already has settings: download them, or upload this Mac's instead (newer preselected).</summary>
    private void FillChoices()
    {
        _choicesPanel.Children.Clear();
        _choicesMade.Clear();
        var previews = Service.PreviewFirstSync().Where(p => p.NeedsChoice).ToList();
        _choicesPanel.IsVisible = previews.Count > 0;
        if (previews.Count == 0) return;
        _choicesPanel.Children.Add(Dialogs.Text($"{previews[0].ProviderName} already has settings from another computer. For each game version, choose which to keep:", 13, FontWeight.SemiBold));
        foreach (var p in previews)
        {
            var group = $"first-sync-{p.Version}";
            var useCloud = new RadioButton { GroupName = group, Content = new TextBlock { Text = p.CloudOptionText("Mac"), TextWrapping = TextWrapping.Wrap }, IsChecked = p.Suggested == FirstSyncChoice.UseCloud };
            var useMine = new RadioButton { GroupName = group, Content = new TextBlock { Text = p.LocalOptionText("Mac"), TextWrapping = TextWrapping.Wrap }, IsChecked = p.Suggested == FirstSyncChoice.UseThisComputer };
            _choicesPanel.Children.Add(Dialogs.Text(p.Version, 12, FontWeight.SemiBold));
            _choicesPanel.Children.Add(useCloud);
            _choicesPanel.Children.Add(useMine);
            _choicesMade.Add((p.Version, useCloud, useMine));
        }
    }

    private void UpdateStart() =>
        _start.IsEnabled = Service.Wow is { Found: true } && Service.Drive is { Found: true } && _choices.Any(c => c.Box.IsChecked == true);

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
        _setupPage.IsVisible = false;
        _progressPage.IsVisible = true;
        var workers = Service.Workers;
        var rows = workers.Select(w => Dialogs.Text($"{w.Version.FolderName}: waiting...")).ToList();
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
        _progressHeading.Text = versions == 0 ? "Setup complete. No game version is switched on."
            : problems == 0 ? "Setup complete." : "Setup complete, but something needs a look (shown in red).";
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
        if (_progressPage.IsVisible) Started = true;       // closing after the sync counts as Finish
        base.OnClosing(e);
    }

    // For the headless UI tests.
    internal bool CanStart => _start.IsEnabled;
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

    internal void ClickStart() => StartSyncing();
    internal bool IsFinished => _finish.IsEnabled;
    internal IReadOnlyList<string> Results => _results.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
