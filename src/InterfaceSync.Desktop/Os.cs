using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Platform;

namespace InterfaceSync.Desktop;

/// <summary>What differs between Windows and macOS, in one place. Everything else is shared.</summary>
internal static class Os
{
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>"Mac" or "PC", in phrases such as "this PC's settings".</summary>
    public static string Machine => IsMac ? "Mac" : "PC";

    public static HostOs Host => IsMac ? HostOs.MacOS : HostOs.Windows;

    public static IUserEnvironment Environment() => IsWindows ? new WindowsUserEnvironment() : new MacUserEnvironment();

    public static IRegistry Registry() => IsWindows ? new WindowsRegistry() : new NoRegistry();

    public static IProcessList Processes() => IsWindows ? new WindowsProcessList() : new MacProcessList();

    /// <summary>Start with Windows (Run key) or Open at Login (LaunchAgent), for this copy of the app.</summary>
    public static IStartAtLogin StartAtLogin(IUserEnvironment env, IFileSystem fs, IRegistry registry)
    {
        var exe = System.Environment.ProcessPath ?? "";
        return IsWindows
            ? new WindowsStartAtLogin(registry, exe, Path.GetTempPath())
            : new MacStartAtLogin(fs, env.UserProfile, exe, ProductInfo.BundleId);
    }

    /// <summary>Per-session names: one Interface Sync per signed-in user.</summary>
    public static readonly string MutexName = IsWindows ? $@"Local\{ProductInfo.Id}" : $"{ProductInfo.Id}-single-instance";
    public static readonly string ShowEventName = $@"Local\{ProductInfo.Id}-ShowStatus";
}
