using Avalonia.Controls;
using InterfaceSync.Core.Service;

namespace InterfaceSync.Desktop;

/// <summary>
/// Tray, menu-bar and window icons, generated from assets/icon/app.svg and embedded in the app: .ico files with every
/// size on Windows (sharp at any display scaling), 44 px .png files on a Mac (22 pt on Retina menu bars).
/// </summary>
internal static class AppIcons
{
    private static readonly string Ext = OperatingSystem.IsWindows() ? ".ico" : ".png";

    private static readonly Dictionary<OverallState, string> Names = new()
    {
        [OverallState.InSync] = "tray-insync" + Ext,
        [OverallState.Syncing] = "tray-syncing" + Ext,
        [OverallState.Waiting] = "tray-waiting" + Ext,
        [OverallState.Paused] = "tray-paused" + Ext,
        [OverallState.NeedsAttention] = "tray-attention" + Ext,
    };

    private static readonly Dictionary<string, WindowIcon> Cache = new();

    public static WindowIcon For(OverallState state) => Load(Names[state]);
    public static WindowIcon App => Load("app" + Ext);

    /// <summary>The syncing animation: the badge's three dots light up in turn.</summary>
    public static IReadOnlyList<WindowIcon> SyncingFrames { get; } = [Load("tray-syncing-1" + Ext), Load("tray-syncing-2" + Ext), Load("tray-syncing-3" + Ext)];

    private static WindowIcon Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var icon)) return icon;
            using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded icon {name} is missing.");
            return Cache[name] = new WindowIcon(stream);
        }
    }
}
