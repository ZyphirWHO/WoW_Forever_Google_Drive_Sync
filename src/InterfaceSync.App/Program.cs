using InterfaceSync.Core;
using InterfaceSync.Core.Platform;

namespace InterfaceSync.App;

internal static class Program
{
    /// <summary>Per-session names: one InterfaceSync per signed-in user.</summary>
    private static readonly string MutexName = $@"Local\{ProductInfo.Id}";
    internal static readonly string ShowEventName = $@"Local\{ProductInfo.Id}-ShowStatus";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Route UI-thread exceptions to TrayApp's handler (log + plain message) instead of the .NET crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // An elevated process may belong to a different admin account and can't see this user's
        // Google Drive drive letter (both happened with v1.1). Never continue elevated.
        var env = new WindowsUserEnvironment();
        if (env.IsElevated)
        {
            MessageBox.Show(
                $"{ProductInfo.Name} is running as administrator, so it would write to the wrong place.\n\n" +
                $"Close it and start {ProductInfo.Name} normally (double-click it, without \"Run as administrator\").",
                ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running in the tray: ask it to open its status window instead of starting twice.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var app = new TrayApp(env, startInTray);
        Application.Run(app);
        return 0;
    }
}
