using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using InterfaceSync.Core;
using InterfaceSync.Core.Platform;

namespace InterfaceSync.Desktop;

internal static class Program
{
    /// <summary>Set when the player chose Update now (Windows): the checked installer to run after quitting.</summary>
    internal static string? PendingInstaller { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // An elevated process may belong to another (admin) account and can't see this user's cloud folder or
        // settings: never continue elevated (it happened with v1.1).
        if (Os.IsWindows && new WindowsUserEnvironment().IsElevated)
        {
            NativeMessage($"{ProductInfo.Name} is running as administrator, so it would write to the wrong place.\n\n" +
                          $"Close it and start {ProductInfo.Name} normally (double-click it, without \"Run as administrator\").");
            return 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, Os.MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask it to open its window instead of starting twice (Windows; on a Mac the menu-bar
            // icon of the running copy is already there).
            if (Os.IsWindows)
                try
                {
                    using var show = EventWaitHandle.OpenExisting(Os.ShowEventName);
                    show.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        bool asRoot = Os.IsMac && new MacUserEnvironment().IsElevated;
        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool updated = args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));
        BuildAvaloniaApp()
            .AfterSetup(b =>
            {
                var app = (DesktopApp)b.Instance!;
                app.StartAsRoot = asRoot;
                app.StartInTray = startInTray;
                app.JustUpdated = updated;
            })
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

        // An update was downloaded and checked: let go of the single-instance lock (the installer waits for it),
        // then hand over to the installer, which updates this copy and opens Interface Sync again.
        if (PendingInstaller is { } installer)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            try
            {
                Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                NativeMessage($"The update couldn't be started: {e.Message}\n\nThe installer is at {installer}.");
            }
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })   // menu-bar app: no Dock icon
            .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.RedirectionSurface] })
            .LogToTrace();

    /// <summary>A plain message before the app's own windows exist (Windows only; elsewhere it goes to the console).</summary>
    private static void NativeMessage(string text)
    {
        if (Os.IsWindows) MessageBoxW(IntPtr.Zero, text, ProductInfo.Name, 0x30);
        else Console.Error.WriteLine(text);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
