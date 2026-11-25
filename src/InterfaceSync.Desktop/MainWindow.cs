using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using InterfaceSync.Core;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.Desktop;

/// <summary>A page of the main window: Home, Activity, Backups, Computers or Settings.</summary>
internal abstract class Page : UserControl
{
    protected Page(MainWindow shell) { Shell = shell; }
    protected MainWindow Shell { get; }
    protected IAppHost Host => Shell.Host;
    protected SyncService Service => Shell.Host.Service;

    public abstract string Key { get; }

    /// <summary>Brings the page up to date (called on every status change while it shows).</summary>
    public virtual void Refresh() { }

    /// <summary>Called when the page is navigated to, with an optional argument (e.g. a game version).</summary>
    public virtual void Arrive(object? argument) { }

    /// <summary>A page's heading: its title in the brand font, and a line under it.</summary>
    protected static Control Heading(string title, string? subtitle = null)
    {
        var t = new TextBlock { Text = title, FontFamily = Brand.Heading, FontWeight = FontWeight.Bold, FontSize = 24 };
        Ui.Res(t, TextBlock.ForegroundProperty, "InterfaceSyncAccent");
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 16), Children = { t } };
        if (subtitle != null) stack.Children.Add(Ui.Text(subtitle, 13, brush: "InterfaceSyncMuted"));
        return stack;
    }

    /// <summary>A page's scrolling body with the standard margins.</summary>
    protected static ScrollViewer Scroll(Control content) => new()
    {
        Content = new Border { Padding = new Thickness(32, 26, 32, 32), Child = content, MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Stretch },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
}

/// <summary>
/// The main window: a sidebar (Home, Activity, Backups, Computers, Settings) and the current page, which fades in
/// when you move between pages. Messages that need the player (an update, settings changed on two computers, a
/// copy of the cloud folder) appear as bars above every page. Short notes with Undo appear at the bottom, and
/// details open in a side panel, so nothing pops up a separate window for everyday work.
/// Closing the window keeps Interface Sync running by the clock.
/// </summary>
internal sealed class MainWindow : Window
{
    public static readonly string[] PageKeys = ["Home", "Activity", "Backups", "Computers", "Settings"];
    private static readonly string[] PageIcons = [Ui.Home, Ui.Clock, Ui.History, Ui.Computer, Ui.Settings];

    private readonly Dictionary<string, Page> _pages = new();
    private readonly Dictionary<string, NavButton> _nav = new();
    private readonly TransitioningContentControl _content = new()
    {
        PageTransition = new CrossFade(TimeSpan.FromMilliseconds(140)),
    };
    private readonly StackPanel _bars = new() { Spacing = 8, Margin = new Thickness(24, 16, 24, 0) };
    private readonly Border _sidebar;
    private readonly Border _contentArea;
    private readonly TextBlock _sideStatus = Ui.Text("", 12, brush: "InterfaceSyncMuted");
    private readonly Ellipse _sideDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };

    // overlays
    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly Button _toastAction;
    private Action? _toastUndo, _toastExpired;
    private DispatcherTimer? _toastTimer;
    private readonly Grid _drawerLayer;
    private readonly Border _drawer;
    private readonly ContentControl _drawerBody = new();
    private readonly TextBlock _drawerTitle = Ui.Text("", 17, FontWeight.SemiBold);
    private bool _allowClose, _updatedNote;
    private readonly HashSet<string> _dismissed = [];

    public MainWindow(IAppHost host)
    {
        Host = host;
        Title = ProductInfo.Name;
        Icon = AppIcons.App;
        Width = 1000;
        Height = 720;
        MinWidth = 780;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.Res(this, BackgroundProperty, "InterfaceSyncWindow");

        // ---- sidebar
        var crest = new Image { Source = Brand.Crest, Width = 38, Height = 38, Margin = new Thickness(0, 0, 10, 0) };
        var name = new TextBlock { Text = ProductInfo.Name, FontFamily = Brand.Heading, FontWeight = FontWeight.Bold, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Ui.Res(name, TextBlock.ForegroundProperty, "InterfaceSyncAccent");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 20, 12, 6), Children = { crest, name } };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(22, 0, 12, 18), Children = { _sideDot, _sideStatus } };
        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 10, 0) };
        for (int i = 0; i < PageKeys.Length; i++)
        {
            var key = PageKeys[i];
            var b = new NavButton(L.T(key), PageIcons[i]);
            b.Click += (_, _) => Navigate(key);
            _nav[key] = b;
            nav.Children.Add(b);
        }
        AutomationProperties.SetName(nav, L.T("Pages"));
        var toggle = Brand.ThemeToggle(() => DesktopApp.ToggleTheme(Host), onDarkSurface: false);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Margin = new Thickness(0);
        var help = Ui.IconButton(Ui.Info, L.T("Help"), () => Actions.OpenLink(this, Feedback.HelpPage));
        var bottom = new StackPanel { Margin = new Thickness(18, 0, 14, 16), Spacing = 10, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { toggle, help } },
            Ui.Text(L.F("Version {0}", ProductInfo.DisplayVersion), 11, brush: "InterfaceSyncMuted"),
        } };
        var side = new DockPanel();
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        side.Children.Add(brand);
        side.Children.Add(status);
        side.Children.Add(bottom);
        side.Children.Add(nav);
        _sidebar = new Border { Width = 232, Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
        Ui.Res(_sidebar, Border.BackgroundProperty, "InterfaceSyncSidebar");
        Ui.Res(_sidebar, Border.BorderBrushProperty, "InterfaceSyncBorder");

        // ---- content: message bars, then the page
        var contentDock = new DockPanel();
        DockPanel.SetDock(_bars, Dock.Top);
        contentDock.Children.Add(_bars);
        contentDock.Children.Add(_content);
        _contentArea = new Border { Child = contentDock };
        Ui.Res(_contentArea, Border.BackgroundProperty, "InterfaceSyncWindow");

        // ---- toast (bottom centre), fades in and out
        _toastAction = Ui.Button(L.T("Undo"), () => { var undo = _toastUndo; HideToast(runExpired: false); undo?.Invoke(); }, ButtonKind.Ghost);
        var toastClose = Ui.IconButton(Ui.Close, L.T("Dismiss"), () => HideToast(runExpired: true));
        _toast = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 8, 8, 8), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24),
            Opacity = 0, IsHitTestVisible = false,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #66000000"),
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _toastText, _toastAction, toastClose } },
        };
        Ui.Res(_toast, Border.BackgroundProperty, "InterfaceSyncRaised");
        Ui.Res(_toast, Border.BorderBrushProperty, "InterfaceSyncBorder");
        AutomationProperties.SetLiveSetting(_toast, AutomationLiveSetting.Polite);

        // ---- side panel (drawer) over a dimmed page
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) };
        shade.PointerPressed += (_, _) => CloseDrawer();
        var drawerHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12), Children = { _drawerTitle } };
        var closeDrawer = Ui.IconButton(Ui.Close, L.T("Close"), CloseDrawer);
        Grid.SetColumn(closeDrawer, 1);
        drawerHeader.Children.Add(closeDrawer);
        var drawerDock = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        DockPanel.SetDock(drawerHeader, Dock.Top);
        drawerDock.Children.Add(drawerHeader);
        drawerDock.Children.Add(_drawerBody);
        _drawer = new Border
        {
            Width = 460, HorizontalAlignment = HorizontalAlignment.Right, BorderThickness = new Thickness(1, 0, 0, 0), Child = drawerDock,
            BoxShadow = BoxShadows.Parse("-8 0 24 0 #55000000"),
            RenderTransform = TransformOperations.Parse("translateX(480px)"),
            Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new CubicEaseOut() }],
        };
        Ui.Res(_drawer, Border.BackgroundProperty, "InterfaceSyncWindow");
        Ui.Res(_drawer, Border.BorderBrushProperty, "InterfaceSyncBorder");
        _drawerLayer = new Grid { IsVisible = false, Children = { shade, _drawer } };

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(_sidebar);
        var right = new Grid { Children = { _contentArea, _toast, _drawerLayer } };
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        _pages["Home"] = new HomePage(this);
        _pages["Activity"] = new ActivityPage(this);
        _pages["Backups"] = new BackupsPage(this);
        _pages["Computers"] = new ComputersPage(this);
        _pages["Settings"] = new SettingsPage(this);

        KeyDown += OnKeyDown;
        Opened += (_, _) => UseMicaIfAvailable();
        if (Host.Updater is { } u) u.Changed += OnUpdaterChanged;
        Look.Changed += OnThemeChanged;
        Navigate("Home");
    }

    public IAppHost Host { get; }
    public string CurrentPage { get; private set; } = "Home";
    internal Page PageFor(string key) => _pages[key];

    // ------------------------------------------------------------------ navigation

    public void Navigate(string key, object? argument = null)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        CurrentPage = key;
        foreach (var (k, b) in _nav) b.IsCurrent = k == key;
        page.Arrive(argument);
        page.Refresh();
        if (_content.Content != page) _content.Content = page;
        RefreshBars();
    }

    /// <summary>Ctrl+1..5 (⌘ on a Mac) jump to a page; Escape closes the side panel.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drawerLayer.IsVisible) { CloseDrawer(); e.Handled = true; return; }
        var mod = Os.IsMac ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == mod && e.Key >= Key.D1 && e.Key <= Key.D5)
        {
            Navigate(PageKeys[e.Key - Key.D1]);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>Called on every status change: the sidebar status, the message bars and the current page.</summary>
    public void Refresh()
    {
        if (!IsVisible) return;
        var tone = StatusWords.Tone(Host.Service.State);
        Ui.Res(_sideDot, Shape.FillProperty, Ui.ToneBrush(tone));
        _sideStatus.Text = StatusWords.StateText(Host.Service, DateTime.UtcNow);
        RefreshBars();
        _pages[CurrentPage].Refresh();
    }

    private void OnUpdaterChanged() => Dispatcher.UIThread.Post(Refresh);
    private void OnThemeChanged() => Dispatcher.UIThread.Post(() => { _barsKey = ""; Refresh(); });

    /// <summary>After an in-app update: a short note at the top (until dismissed).</summary>
    public void ShowUpdated() { _updatedNote = true; Refresh(); }

    private string _barsKey = "";

    private void RefreshBars()
    {
        var service = Host.Service;
        var now = DateTime.UtcNow;
        var bars = new List<(string Id, string Tone, string Icon, string Title, string? Detail, Control[] Buttons)>();

        foreach (var w in service.Workers.Where(w => w.State.Conflict != null))
        {
            var worker = w;
            bars.Add(($"conflict-{w.Version.FolderName}", "InterfaceSyncWarning", Ui.Warning,
                L.F("{0}: settings changed on two computers", w.Version.FolderName), L.T("Choose which copy to keep. The other is saved as a backup."),
                [Ui.Button(L.T("Choose which to keep…"), () => Actions.Resolve(this, Host, worker), ButtonKind.Primary)]));
        }
        if (Host.Updater is { Available: { } r } u && u.State is UpdateState.Available or UpdateState.Downloading)
            bars.Add(("update", "InterfaceSyncAccent", Ui.Download,
                r.IsPrerelease ? L.F("Test build {0} is available", r.Version) : L.F("{0} {1} is available", ProductInfo.Name, r.Version),
                u.State == UpdateState.Downloading ? L.T("Downloading and checking the installer…")
                    : Os.IsWindows ? L.F("You have {0}. Updating takes a few seconds and keeps all your settings.", u.CurrentVersion)
                    : L.F("You have {0}. Your settings are kept when you update.", u.CurrentVersion),
                [Ui.Button(L.T("What's new"), () => Actions.OpenLink(this, r.Page), ButtonKind.Ghost),
                 Ui.Button(Os.IsWindows ? L.T("Update now") : L.T("Download update"), Host.InstallUpdate, ButtonKind.Primary, Ui.Download)]));
        if (_updatedNote && !_dismissed.Contains("updated"))
            bars.Add(("updated", "InterfaceSyncGood", Ui.Check, L.F("Updated to version {0}", ProductInfo.DisplayVersion), null,
                [Ui.Button(L.T("What's new"), () => Actions.ShowWhatsNew(this), ButtonKind.Ghost)]));
        var dups = service.Duplicates;
        if (dups.Count > 0)
            bars.Add(("dups", "InterfaceSyncWarning", Ui.Warning,
                dups.Count == 1 ? L.F("{0} made a copy of an {1} folder", service.ProviderName, ProductInfo.Name)
                                : L.F("{0} made {1} copies of {2} folders", service.ProviderName, dups.Count, ProductInfo.Name),
                L.F("This happens when two computers save at the same moment. {0} only uses \"{1}\" and hasn't moved or deleted anything. Look inside, and delete the copy in {2} when you're sure you don't need it.",
                    ProductInfo.Name, dups[0].CopyOf, service.ProviderName) + "\n" +
                string.Join("\n", dups.Take(3).Select(d => $"• \"{d.Name}\" ({L.N(d.FileCount, "{0} file", "{0} files")}, {L.F("last changed {0}", StatusWords.Ago(d.NewestUtc, now))})")),
                [Ui.Button(L.T("Hide"), () => { foreach (var d in service.Duplicates.ToList()) service.HideDuplicate(d); Refresh(); }, ButtonKind.Ghost),
                 Ui.Button(L.T("Show folder"), () => Actions.OpenFolder(this, service.Duplicates.FirstOrDefault()?.Path ?? ""), ButtonKind.Secondary, Ui.Folder)]));
        if (service.State == OverallState.NeedsAttention && !service.Workers.Any(w => w.State.Conflict != null))
            bars.Add(("attention", "InterfaceSyncError", Ui.Warning, StatusWords.StateText(service, now), null, []));

        bars.RemoveAll(b => _dismissed.Contains(b.Id) && b.Id is "updated");
        var key = string.Join("|", bars.Select(b => b.Id + b.Title + b.Detail));
        if (key == _barsKey) return;
        _barsKey = key;
        _bars.Children.Clear();
        foreach (var b in bars)
        {
            var id = b.Id;
            Action? dismiss = id == "updated" ? () => { _dismissed.Add(id); _barsKey = ""; RefreshBars(); } : null;
            _bars.Children.Add(Ui.MessageBar(b.Tone, b.Icon, b.Title, b.Detail, b.Buttons, dismiss));
        }
        _bars.IsVisible = bars.Count > 0;
    }

    /// <summary>For tests: the message bars' titles.</summary>
    internal IReadOnlyList<string> BarTitles => _bars.Children.OfType<Border>().Select(b => (string?)b.Tag ?? "").ToList();

    // ------------------------------------------------------------------ toast with Undo

    /// <summary>
    /// A short note at the bottom ("Snapshot deleted"), with an optional Undo. <paramref name="expired"/> runs when the
    /// note goes away without Undo (e.g. the delete then really happens).
    /// </summary>
    public void ShowToast(string text, Action? undo = null, Action? expired = null, double seconds = 6)
    {
        if (_toastExpired != null) { var previous = _toastExpired; _toastExpired = null; previous(); }
        _toastText.Text = text;
        _toastUndo = undo;
        _toastExpired = expired;
        _toastAction.IsVisible = undo != null;
        _toast.IsHitTestVisible = true;
        _toast.Opacity = 1;
        _toastShown = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _toastTimer.Tick += (_, _) =>
        {
            if (_toast.IsPointerOver) return;   // keep it while the pointer rests on it
            HideToast(runExpired: true);
        };
        _toastTimer.Start();
    }

    private void HideToast(bool runExpired)
    {
        _toastTimer?.Stop();
        _toast.Opacity = 0;
        _toast.IsHitTestVisible = false;
        _toastShown = false;
        var expired = _toastExpired;
        _toastExpired = null;
        _toastUndo = null;
        if (runExpired) expired?.Invoke();
    }

    /// <summary>For tests: the note showing, and its Undo.</summary>
    internal string? ToastText => _toastShown ? _toastText.Text : null;
    private bool _toastShown;
    internal void ClickUndo() { var undo = _toastUndo; HideToast(runExpired: false); undo?.Invoke(); }
    internal void ExpireToast() => HideToast(runExpired: true);

    // ------------------------------------------------------------------ side panel

    public void OpenDrawer(string title, Control body)
    {
        _drawerTitle.Text = title;
        _drawerBody.Content = body;
        _drawerLayer.IsVisible = true;
        _drawer.RenderTransform = TransformOperations.Parse("translateX(0px)");
    }

    public void CloseDrawer()
    {
        _drawer.RenderTransform = TransformOperations.Parse("translateX(480px)");
        DispatcherTimer.RunOnce(() => { _drawerLayer.IsVisible = false; _drawerBody.Content = null; }, TimeSpan.FromMilliseconds(210));
    }

    /// <summary>For screenshots: no fades or slides, so a picture shows the finished state.</summary>
    internal void AnimationsOff()
    {
        _content.PageTransition = null;
        _drawer.Transitions = null;
        _toast.Transitions = null;
    }

    internal bool DrawerOpen => _drawerLayer.IsVisible;
    internal string DrawerTitle => _drawerTitle.Text ?? "";
    internal object? DrawerContent => _drawerBody.Content;

    // ------------------------------------------------------------------ window

    /// <summary>Windows 11: the sidebar shows the frosted Mica backdrop, like Windows' own apps. Elsewhere it stays solid.</summary>
    private void UseMicaIfAvailable()
    {
        if (!Os.IsWindows) return;
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
        {
            Background = Brushes.Transparent;
            Ui.Res(_sidebar, Border.BackgroundProperty, "InterfaceSyncSidebarMica");
        }
    }

    public void AllowClose() { _allowClose = true; Close(); }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing keeps Interface Sync running by the clock; Quit is in its menu.
        if (!_allowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Host.Updater is { } u) u.Changed -= OnUpdaterChanged;
        Look.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }
}

/// <summary>A sidebar entry: icon and name; the current page has a gold bar and a tinted background.</summary>
internal sealed class NavButton : Button
{
    private readonly Border _bar = new() { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Avalonia.Controls.Shapes.Path _icon;
    private readonly TextBlock _text;

    public NavButton(string text, string icon)
    {
        _icon = Ui.Icon(icon, 17, "InterfaceSyncMuted");
        _icon.Margin = new Thickness(0, 0, 12, 0);
        _text = Ui.Text(text, 14);
        _text.VerticalAlignment = VerticalAlignment.Center;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _bar, _icon, _text } };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Padding = new Thickness(6, 9, 10, 9);
        BorderThickness = new Thickness(0);
        Resources["ButtonBackground"] = Brushes.Transparent;
        Resources["ButtonBorderBrush"] = Brushes.Transparent;
        Resources["ButtonBorderBrushPointerOver"] = Brushes.Transparent;
        AutomationProperties.SetName(this, text);
        IsCurrent = false;
    }

    private bool _current;
    public bool IsCurrent
    {
        get => _current;
        set
        {
            _current = value;
            if (value)
            {
                Ui.Res(this, BackgroundProperty, "InterfaceSyncSelection");
                Ui.Res(_bar, Border.BackgroundProperty, "InterfaceSyncAccent");
                Ui.Res(_icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "InterfaceSyncAccent");
                _text.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                Background = Brushes.Transparent;
                _bar.Background = Brushes.Transparent;
                Ui.Res(_icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "InterfaceSyncMuted");
                _text.FontWeight = FontWeight.Normal;
            }
            AutomationProperties.SetItemStatus(this, value ? "current page" : "");
        }
    }
}
