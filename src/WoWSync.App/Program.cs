using WoWSync.Core.Platform;

namespace WoWSync.App;

internal static class Program
{
    /// <summary>Per-session names: one WoWSync per signed-in user.</summary>
    private const string MutexName = @"Local\WoWSync-2";
    internal const string ShowEventName = @"Local\WoWSync-2-ShowStatus";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // An elevated process may belong to a different admin account and can't see this user's
        // Google Drive drive letter (both happened with v1.1). Never continue elevated.
        var env = new WindowsUserEnvironment();
        if (env.IsElevated)
        {
            MessageBox.Show(
                "WoWSync is running as administrator, so it would write to the wrong place.\n\n" +
                "Close it and start WoWSync normally (double-click it, without \"Run as administrator\").",
                "WoWSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
