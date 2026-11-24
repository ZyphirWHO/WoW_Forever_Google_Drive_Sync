using Avalonia.Controls;
using InterfaceSync.Core.Service;

namespace InterfaceSync.Mac;

/// <summary>Menu-bar and window icons, generated from assets/icon/app.svg and embedded in the app.</summary>
internal static class MacIcons
{
    private static readonly Dictionary<OverallState, string> Names = new()
    {
        [OverallState.InSync] = "tray-insync.png",
        [OverallState.Syncing] = "tray-syncing.png",
        [OverallState.Waiting] = "tray-waiting.png",
        [OverallState.Paused] = "tray-paused.png",
        [OverallState.NeedsAttention] = "tray-attention.png",
    };

    private static readonly Dictionary<string, WindowIcon> Cache = new();

    public static WindowIcon For(OverallState state) => Load(Names[state]);
    public static WindowIcon App => Load("app.png");

    /// <summary>The syncing animation: the badge's three dots light up in turn.</summary>
    public static IReadOnlyList<WindowIcon> SyncingFrames { get; } = [Load("tray-syncing-1.png"), Load("tray-syncing-2.png"), Load("tray-syncing-3.png")];

    private static WindowIcon Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var icon)) return icon;
            using var stream = typeof(MacIcons).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded icon {name} is missing.");
            return Cache[name] = new WindowIcon(stream);
        }
    }
}
