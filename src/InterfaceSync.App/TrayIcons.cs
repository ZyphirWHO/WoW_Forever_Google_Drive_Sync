using InterfaceSync.Core.Service;

namespace InterfaceSync.App;

/// <summary>
/// The app and tray icons, generated from assets/icon/app.svg by assets/icon/generate.py and embedded in the exe.
/// Tray states differ by badge shape (check, dots, clock, pause, warning triangle), not by colour alone.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<OverallState, Icon> _icons = new();

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize;
        _icons[OverallState.InSync] = Load("tray-insync.ico", size);
        _icons[OverallState.Syncing] = Load("tray-syncing.ico", size);
        _icons[OverallState.Waiting] = Load("tray-waiting.ico", size);
        _icons[OverallState.Paused] = Load("tray-paused.ico", size);
        _icons[OverallState.NeedsAttention] = Load("tray-attention.ico", size);
    }

    public Icon For(OverallState state) => _icons[state];

    /// <summary>The app icon, for window title bars and the taskbar.</summary>
    public static Icon AppIcon { get; } = Load("app.ico", new Size(32, 32));

    private static Icon Load(string name, Size size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded icon {name} is missing.");
        return new Icon(stream, size);
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
