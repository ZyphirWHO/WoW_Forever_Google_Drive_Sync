using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using WoWSync.Core.Abstractions;
using Hive = WoWSync.Core.Abstractions.RegistryHive;

namespace WoWSync.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    private static RegistryKey Base(Hive hive) => RegistryKey.OpenBaseKey(
        hive == Hive.CurrentUser ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine,
        RegistryView.Registry64);

    public string? GetString(Hive hive, string keyPath, string valueName)
    {
        try
        {
            using var root = Base(hive);
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) switch
            {
                string s => s,
                string[] arr => string.Join("\n", arr),
                null => null,
                var other => other.ToString(),
            };
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(Hive hive, string keyPath)
    {
        try
        {
            using var root = Base(hive);
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> GetValueNames(Hive hive, string keyPath)
    {
        try
        {
            using var root = Base(hive);
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public void SetString(Hive hive, string keyPath, string valueName, string value)
    {
        if (hive != Hive.CurrentUser) throw new InvalidOperationException("WoWSync only writes to HKCU.");
        using var root = Base(hive);
        using var key = root.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(Hive hive, string keyPath, string valueName)
    {
        if (hive != Hive.CurrentUser) throw new InvalidOperationException("WoWSync only writes to HKCU.");
        using var root = Base(hive);
        using var key = root.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Lists processes and their full image paths without launching anything.
/// Uses QueryFullProcessImageName with limited-query rights, which works for most
/// processes even when the game runs under a different integrity level.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessList : IProcessList
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public IReadOnlyList<ProcessEntry> GetProcesses(params string[] namePrefixes)
    {
        var result = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; } catch (InvalidOperationException) { continue; }
                if (namePrefixes.Length > 0 &&
                    !namePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(new ProcessEntry(p.Id, name, TryGetImagePath(p.Id)));
            }
        }
        return result;
    }

    internal static string? TryGetImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUserEnvironment : IUserEnvironment
{
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string? Variable(string name) => Environment.GetEnvironmentVariable(name);
    public string UserName => Environment.UserName;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
