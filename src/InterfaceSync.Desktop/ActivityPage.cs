using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Desktop;

/// <summary>Everything that synced, newest first and grouped by day; pick a game version to see only its activity.</summary>
internal sealed class ActivityPage : Page
{
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly ComboBox _filter = new() { MinWidth = 200 };
    private string _key = "";
    private List<string> _versions = [];
    private int _count;

    public ActivityPage(MainWindow shell) : base(shell)
    {
        AutomationProperties.SetName(_filter, L.T("Show activity for"));
        _filter.SelectionChanged += (_, _) => { _key = ""; Refresh(); };
        var log = Ui.Button(L.T("Technical log"), () => Actions.ViewLog(Shell, Host), ButtonKind.Ghost, Ui.Clock);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12), Children = { _filter, log } };
        Grid.SetColumn(log, 2);
        Content = Scroll(new StackPanel { Children =
        {
            Heading(L.T("Activity"), L.T("What synced and when. Click an entry to see its files.")),
            bar,
            Ui.Card(_list),
        } });
    }

    public override string Key => "Activity";

    public override void Refresh()
    {
        var now = DateTime.UtcNow;
        var versions = Service.Versions.Select(v => v.FolderName).ToList();
        if (!versions.SequenceEqual(_versions))
        {
            _versions = versions;
            _filter.ItemsSource = new[] { L.T("All game versions") }.Concat(versions).ToList();
            _filter.SelectedIndex = 0;
        }
        var only = _filter.SelectedIndex > 0 ? _versions[_filter.SelectedIndex - 1] : null;
        var entries = Service.Activity.List().Where(e => only == null || e.Version.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
        var key = $"{only}|{entries.Count}|{entries.FirstOrDefault()?.Utc.Ticks}|{StatusWords.Ago(entries.FirstOrDefault()?.Utc, now)}|{L.Language}";
        if (key == _key) return;
        _key = key;
        _list.Children.Clear();
        _count = entries.Count;
        if (entries.Count == 0)
        {
            _list.Children.Add(Ui.Text(L.T("Nothing synced yet. Uploads, downloads and restores will appear here."), 12.5, brush: "InterfaceSyncMuted"));
            return;
        }
        string? day = null;
        foreach (var e in entries)
        {
            var heading = ActivityStore.DayHeading(e.Utc, now);
            if (heading != day)
            {
                day = heading;
                var h = Ui.Text(heading, 12, FontWeight.SemiBold, "InterfaceSyncAccent");
                h.Margin = new Thickness(6, _list.Children.Count == 0 ? 2 : 12, 0, 4);
                _list.Children.Add(h);
            }
            _list.Children.Add(Line(Shell, e, $"{e.Utc.ToLocalTime().ToString("t", L.Culture)} · {e.Version}{(e.FileCount > 0 ? " · " + L.N(e.FileCount, "{0} file", "{0} files") : "")}"));
        }
    }

    internal int EntryCount => _count;

    /// <summary>"_retail_ · 5 minutes ago · 3 files" under a headline.</summary>
    internal static string Detail(ActivityEntry e, DateTime now) =>
        $"{e.Version} · {StatusWords.Ago(e.Utc, now)}{(e.FileCount > 0 ? " · " + L.N(e.FileCount, "{0} file", "{0} files") : "")}";

    /// <summary>One activity line: icon, headline and detail. With files, it's a button that shows them in the side panel.</summary>
    internal static Control Line(MainWindow shell, ActivityEntry e, string detail)
    {
        var (icon, brush) = e.Kind switch
        {
            ActivityKind.Uploaded => (Ui.Upload, "InterfaceSyncAccent"),
            ActivityKind.Downloaded => (Ui.Download, "InterfaceSyncGood"),
            ActivityKind.Conflict => (Ui.Warning, "InterfaceSyncWarning"),
            _ => (Ui.History, "InterfaceSyncAccent"),
        };
        var i = Ui.Icon(icon, 16, brush);
        i.Margin = new Thickness(0, 2, 12, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        var text = new StackPanel { Spacing = 1, Children = { Ui.Text(e.Headline, 13), Ui.Text(detail, 11.5, brush: "InterfaceSyncMuted") } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { i, text } };
        Grid.SetColumn(text, 1);
        if (e.Files.Count == 0) { g.Margin = new Thickness(8, 6); return g; }
        var chevron = Ui.Icon(Ui.ChevronRight, 14, "InterfaceSyncMuted");
        Grid.SetColumn(chevron, 2);
        g.Children.Add(chevron);
        var b = new Button
        {
            Content = g, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6),
        };
        b.Resources["ButtonBackground"] = Brushes.Transparent;
        AutomationProperties.SetName(b, e.Headline);
        AutomationProperties.SetHelpText(b, $"{detail}. {L.T("Press Enter to see the files.")}");
        b.Click += (_, _) => shell.OpenDrawer(e.Headline, FilesView(e));
        return b;
    }

    /// <summary>The files an entry changed, grouped by what they are ("Details", "Key bindings"…).</summary>
    internal static Control FilesView(ActivityEntry e)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Ui.Text($"{e.Version} · {e.Utc.ToLocalTime().ToString("f", L.Culture)}", 12.5, brush: "InterfaceSyncMuted", bottom: 8));
        foreach (var group in e.Files.Select(SettingsItem.Parse).GroupBy(i => i.Area, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            stack.Children.Add(Ui.Text(L.T(group.Key), 13.5, FontWeight.SemiBold));
            foreach (var item in group)
            {
                var line = Ui.Text(item.RelativePath.Replace('\\', '/') + (item.CharacterLabel is { } c ? $"   ({c})" : ""), 12, brush: "InterfaceSyncMuted");
                line.Margin = new Thickness(12, 0, 0, 0);
                stack.Children.Add(line);
            }
        }
        if (e.FileCount > e.Files.Count) stack.Children.Add(Ui.Text("…" + L.F("and {0} more", e.FileCount - e.Files.Count), 12, brush: "InterfaceSyncMuted"));
        return new ScrollViewer { Content = stack };
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
