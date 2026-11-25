using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InterfaceSync.Core;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Desktop;

/// <summary>Every computer that syncs these settings: its system, app version, what it did last and when.</summary>
internal sealed class ComputersPage : Page
{
    private readonly WrapPanel _list = new() { Orientation = Orientation.Horizontal };
    private IReadOnlyList<DeviceInfo> _computers = [];
    private DateTime _readUtc = DateTime.MinValue;
    private string _key = "";
    private readonly List<string> _names = [];

    public ComputersPage(MainWindow shell) : base(shell)
    {
        Content = Scroll(new StackPanel { Children =
        {
            Heading(L.T("Your computers"), L.T("Every computer that syncs these settings. Each one appears after it syncs with version 3.1 or later.")),
            _list,
            Ui.Text(L.T("Each computer leaves a small file in your cloud folder when it syncs, so the others can list it. Nothing is written while it's idle."), 12, brush: "InterfaceSyncMuted"),
        } });
    }

    public override string Key => "Computers";

    public override void Arrive(object? argument) => _readUtc = DateTime.MinValue;   // fresh on every visit

    public override void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - _readUtc > TimeSpan.FromSeconds(60))
        {
            _readUtc = now;
            try { _computers = Service.Computers(); } catch (Exception) { }
        }
        var key = string.Join("|", _computers.Select(c => c.Name + c.LastSyncUtc.Ticks)) + StatusWords.Ago(_computers.FirstOrDefault()?.LastSyncUtc, now) + Look.IsDark;
        if (key == _key) return;
        _key = key;
        _list.Children.Clear();
        _names.Clear();
        if (_computers.Count == 0)
        {
            _list.Children.Add(HomePage.EmptyState(Ui.Computer, L.T("No computers yet"),
                L.F("Each computer appears here after it syncs with {0} 3.1 or later.", ProductInfo.Name)));
            return;
        }
        foreach (var c in _computers)
        {
            var icon = Ui.Icon(Ui.Computer, 26, c.IsThisComputer ? "InterfaceSyncAccent" : "InterfaceSyncMuted");
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            var stack = new StackPanel { Spacing = 6, Children = { icon, Ui.Text(c.Name, 15, FontWeight.SemiBold) } };
            if (c.IsThisComputer) { var p = Ui.Pill(L.T("This computer"), "InterfaceSyncAccent"); p.HorizontalAlignment = HorizontalAlignment.Left; stack.Children.Add(p); }
            stack.Children.Add(Ui.Text(StatusWords.ComputerDetail(c, now), 12, brush: "InterfaceSyncMuted"));
            var card = Ui.CardOf(stack);
            card.Width = 300;
            card.Margin = new Thickness(0, 0, 12, 12);
            if (c.IsThisComputer) { Ui.Res(card, Border.BorderBrushProperty, "InterfaceSyncAccent"); }
            AutomationProperties.SetName(card, $"{c.Name}. {StatusWords.ComputerDetail(c, now)}");
            _list.Children.Add(card);
            _names.Add(c.IsThisComputer ? L.F("{0} (this computer)", c.Name) : c.Name);
        }
    }

    /// <summary>For tests.</summary>
    internal IReadOnlyList<string> Names => _names;
}
