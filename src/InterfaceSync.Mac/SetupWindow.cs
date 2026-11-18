using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Service;
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
    private readonly Button _start;
    private readonly StackPanel _setupPage = new();
    private readonly StackPanel _progressPage = new() { IsVisible = false };
    private readonly TextBlock _progressHeading = Dialogs.Text("Running the first sync...", 15, FontWeight.SemiBold);
    private readonly StackPanel _results = new();
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _afterword = Dialogs.Text("");
    private readonly Button _finish;
    private bool _filling;
    private bool _syncing;

    public SetupWindow(IMacHost host)
    {
        _host = host;
        Title = $"{ProductInfo.Name} setup";
        Icon = MacIcons.App;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _start = Dialogs.Button("Start syncing", StartSyncing, isDefault: true);
        _finish = Dialogs.Button("Finish", () => { Started = true; Close(); }, isDefault: true);
        _finish.IsEnabled = false;

        _setupPage.Children.Add(Dialogs.Text($"{ProductInfo.Name} found these on this Mac. Check them, then start syncing.", 15, FontWeight.SemiBold));
        _setupPage.Children.Add(Row("World of Warcraft:", _wowPath, Dialogs.Button("Change...", () => _ = ChooseWow())));
        _setupPage.Children.Add(Dialogs.Text("Sync these game versions:"));
        _setupPage.Children.Add(_versions);
        _setupPage.Children.Add(Dialogs.Text("Cloud folder:"));
        _setupPage.Children.Add(_provider);
        _setupPage.Children.Add(Row("", _cloudPath, Dialogs.Button("Change...", () => _ = ChooseCloud())));
        _setupPage.Children.Add(_notes);
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
            var box = new CheckBox { Content = v.FolderName + (v.ClientVersion != null ? $"  ({v.ClientVersion})" : ""), IsChecked = on };
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
        notes.Add($"On this Mac's first sync, if {provider.DisplayName()} already has settings for a game version, they replace this Mac's. A backup of this Mac's settings is kept.");
        _notes.Text = string.Join("\n\n", notes);
        UpdateStart();
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
        settings.SetupComplete = true;
        settings.CloudProvider ??= Service.EffectiveProvider();
        Service.SaveSettings(settings);

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
                    if (problem) row.Foreground = Brushes.Firebrick;
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
        _afterword.Text = $"{ProductInfo.Name} keeps running in the menu bar (its icon at the top right of the screen). " +
                          "It syncs on its own whenever you quit the game. Click the icon to see its status at any time.";
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
    internal int VersionCount => _choices.Count;
    internal IReadOnlyList<string> ProviderChoices => (_provider.ItemsSource as IEnumerable<string>)?.ToList() ?? [];
    internal void ClickStart() => StartSyncing();
    internal bool IsFinished => _finish.IsEnabled;
    internal IReadOnlyList<string> Results => _results.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
