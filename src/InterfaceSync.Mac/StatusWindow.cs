using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterfaceSync.Core;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Settings;

namespace InterfaceSync.Mac;

/// <summary>Per-version state, recent activity and the rare manual actions. Closing hides it; Quit is in the menu.</summary>
internal sealed class StatusWindow : Window
{
    private readonly IMacHost _host;
    private SyncService Service => _host.Service;

    private readonly TextBlock _overall = Dialogs.Text("", 15, FontWeight.SemiBold);
    private readonly TextBlock _paths = Dialogs.Text("", 12);
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly Button _pause;
    private readonly Button _resolve;
    private readonly ComboBox _moveTo = new() { MinWidth = 180 };
    private readonly ComboBox _appearance = new() { MinWidth = 150 };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 220, FontFamily = new FontFamily("Menlo, monospace"), FontSize = 11 };
    private bool _allowClose;

    public StatusWindow(IMacHost host)
    {
        _host = host;
        Title = $"{ProductInfo.Name} {ProductInfo.Version}";
        Icon = MacIcons.App;
        Width = 760;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _pause = Dialogs.Button("Pause syncing", () => _host.Run(() => Service.SetPaused(!Service.Settings.Paused), Refresh));
        _resolve = Dialogs.Button("Choose settings to keep...", Resolve);
        var buttons = new WrapPanel { Margin = new Thickness(-6, 0, 0, 8) };
        buttons.Children.Add(MacBrand.Primary(Dialogs.Button("Sync now", () => _host.Run(Service.SyncNow, Refresh))));
        buttons.Children.Add(_pause);
        buttons.Children.Add(_resolve);
        buttons.Children.Add(Dialogs.Button("Open log folder", () => _ = Open(Service.Paths.Logs)));
        buttons.Children.Add(Dialogs.Button("Open backups", () => _ = Open(Service.Paths.Backups)));
        buttons.Children.Add(Dialogs.Button("Copy diagnostics", () => _ = CopyDiagnostics()));
        buttons.Children.Add(Dialogs.Button("About", () => Dialogs.About().Show()));

        var move = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        move.Children.Add(new TextBlock { Text = "Move my settings to:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        move.Children.Add(_moveTo);
        move.Children.Add(Dialogs.Button("Move", Move));
        move.Children.Add(new TextBlock { Text = "Appearance:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 8, 0) });
        move.Children.Add(_appearance);
        _appearance.ItemsSource = MacApp.ThemeChoices.Select(c => c.Label).ToList();
        _appearance.SelectedIndex = Array.FindIndex(MacApp.ThemeChoices, c => c.Theme == MacTheme.Mode);
        _appearance.SelectionChanged += (_, _) =>
        {
            if (_appearance.SelectedIndex < 0) return;
            var chosen = MacApp.ThemeChoices[_appearance.SelectedIndex].Theme;
            if (chosen != MacTheme.Mode) MacApp.ChooseTheme(_host, chosen);
        };

        var root = new StackPanel();
        root.Children.Add(_overall);
        root.Children.Add(_paths);
        root.Children.Add(MacBrand.Section("Game versions"));
        root.Children.Add(_rows);
        root.Children.Add(buttons);
        root.Children.Add(move);
        root.Children.Add(MacBrand.Section("Recent activity"));
        root.Children.Add(_log);
        var (banner, _) = MacBrand.Banner("Your interface settings, on every computer");
        Content = new ScrollViewer { Content = new StackPanel { Children = { banner, new Border { Padding = new Thickness(20, 12, 20, 18), Child = root } } } };
        _moveTo.ItemsSource = CloudProviders.All.Select(p => p.DisplayName()).ToList();
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
        var index = Array.FindIndex(MacApp.ThemeChoices, c => c.Theme == MacTheme.Mode);
        if (_appearance.SelectedIndex != index) _appearance.SelectedIndex = index;
        _overall.Text = Service.StateText;
        _overall.Foreground = Service.State == OverallState.NeedsAttention ? MacTheme.Error : null;
        var wow = Service.Wow?.Found == true ? string.Join("; ", Service.Wow.Installs.Select(i => i.Root)) : "not found";
        _paths.Text = $"World of Warcraft: {wow}\n{Service.ProviderName}: {Service.Drive?.MyDrivePath ?? "not found"}\nLog: {Service.Paths.Logs}";

        _rows.Children.Clear();
        foreach (var w in Service.Workers)
        {
            var s = w.State;
            var note = s.Conflict != null ? " · choose which settings to keep" : s.PendingPush ? " · upload pending" : s.LastError is { } err ? $" · {err}" : "";
            var row = Dialogs.Text($"{w.Version.FolderName}: {(string.IsNullOrEmpty(w.StatusText) ? w.Status.ToString() : w.StatusText)}" +
                                   $"   (last upload {Dialogs.Format(s.LastPushUtc)}, last download {Dialogs.Format(s.LastPullUtc)}){note}", 12);
            if (w.Status is SyncStatus.NeedsAttention or SyncStatus.Error) row.Foreground = MacTheme.Error;
            _rows.Children.Add(row);
        }
        foreach (var v in Service.Versions.Where(v => !Service.IsEnabled(v)))
            _rows.Children.Add(new TextBlock { Text = $"{v.FolderName}: {(v.CanEnable ? "off" : "not played on this Mac yet")}", Foreground = MacTheme.Muted, FontSize = 12 });

        _pause.Content = Service.Settings.Paused ? "Resume syncing" : "Pause syncing";
        _resolve.IsEnabled = Service.Workers.Any(w => w.State.Conflict != null);
        _log.Text = string.Join("\n", Service.RecentLog(200).Reverse().Select(l => l.ToString()));
    }

    private void Resolve()
    {
        var w = Service.Workers.FirstOrDefault(x => x.State.Conflict != null);
        if (w?.State.Conflict is not { } c) return;
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
    internal IReadOnlyList<string> Rows => _rows.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
    internal string Overall => _overall.Text ?? "";
}
