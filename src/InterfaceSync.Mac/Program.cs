using Avalonia;
using InterfaceSync.Core;
using InterfaceSync.Core.Platform;

namespace InterfaceSync.Mac;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One Interface Sync per user. (Named mutexes work on macOS; a second launch simply exits,
        // the menu-bar icon of the running one is already there.)
        using var mutex = new Mutex(initiallyOwned: true, $"{ProductInfo.Id}-single-instance", out bool firstInstance);
        if (!firstInstance) return 0;

        bool asRoot = OperatingSystem.IsMacOS() && new MacUserEnvironment().IsElevated;
        BuildAvaloniaApp()
            .AfterSetup(b => ((MacApp)b.Instance!).StartAsRoot = asRoot)
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MacApp>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false }) // menu-bar app: no Dock icon
            .LogToTrace();
}
