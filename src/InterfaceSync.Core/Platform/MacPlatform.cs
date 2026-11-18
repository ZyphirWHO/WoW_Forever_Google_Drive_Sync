using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Platform;

/// <summary>macOS folders: ~/Library/Application Support for app data, /Users/Shared for Battle.net's data.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacUserEnvironment : IUserEnvironment
{
    public HostOs Os => HostOs.MacOS;
    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LocalAppData => Path.Combine(UserProfile, "Library", "Application Support");
    public string RoamingAppData => LocalAppData;
    public string ProgramData => "/Users/Shared";
    public string UserName => Environment.UserName;
    public string? Variable(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Running as root would write into root's home, not the player's: refuse, like admin on Windows.</summary>
    public bool IsElevated => MacNative.geteuid() == 0;
}

/// <summary>Processes and their full executable paths (proc_pidpath), launching nothing.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacProcessList : IProcessList
{
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
                result.Add(new ProcessEntry(p.Id, name, MacNative.ExecutablePath(p.Id)));
            }
        }
        return result;
    }
}

/// <summary>macOS has no registry; every lookup finds nothing, and Interface Sync never writes one there.</summary>
public sealed class NoRegistry : IRegistry
{
    public string? GetString(RegistryHive hive, string keyPath, string valueName) => null;
    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string keyPath) => [];
    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string keyPath) => [];
    public void SetString(RegistryHive hive, string keyPath, string valueName, string value) =>
        throw new PlatformNotSupportedException("There is no registry on this OS.");
    public void DeleteValue(RegistryHive hive, string keyPath, string valueName) { }
}

[SupportedOSPlatform("macos")]
internal static class MacNative
{
    [DllImport("libc")]
    internal static extern uint geteuid();

    [DllImport("libproc", EntryPoint = "proc_pidpath")]
    private static extern int proc_pidpath(int pid, byte[] buffer, uint size);

    internal static string? ExecutablePath(int pid)
    {
        var buffer = new byte[4096]; // PROC_PIDPATHINFO_MAXSIZE
        try
        {
            int n = proc_pidpath(pid, buffer, (uint)buffer.Length);
            return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // struct stat (64-bit inodes), the layout on both arm64 and x86_64:
    //   0 st_dev (4)  4 st_mode (2)  6 st_nlink (2)  8 st_ino (8)  16 st_uid  20 st_gid  24 st_rdev
    //   32 atime  48 mtime  64 ctime  80 birthtime (16 each)  96 st_size (8)  104 st_blocks (8)
    //   112 st_blksize (4)  116 st_flags (4)  120 st_gen (4)  ... 144 bytes in all.
    internal const int StatSize = 144;
    internal const int ModeOffset = 4;
    internal const int SizeOffset = 96;
    internal const int FlagsOffset = 116;

    /// <summary>SF_DATALESS: a File Provider placeholder whose bytes are only in the cloud.</summary>
    internal const uint SfDataless = 0x40000000;

    // On Apple silicon "stat" uses 64-bit inodes; on Intel that variant is exported as "stat$INODE64".
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat_arm64(string path, byte[] buf);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int stat_x64(string path, byte[] buf);

    /// <summary>The raw struct stat, or null if the call failed.</summary>
    internal static byte[]? Stat(string path)
    {
        var buf = new byte[StatSize + 64];
        try
        {
            int rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? stat_x64(path, buf) : stat_arm64(path, buf);
            return rc == 0 ? buf : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    internal static uint Flags(byte[] st) => BitConverter.ToUInt32(st, FlagsOffset);
    internal static long Size(byte[] st) => BitConverter.ToInt64(st, SizeOffset);
    internal static ushort Mode(byte[] st) => BitConverter.ToUInt16(st, ModeOffset);

    /// <summary>True when the file is an online-only placeholder (Google Drive, OneDrive or Dropbox on macOS).</summary>
    internal static bool IsDataless(string path) => Stat(path) is { } st && (Flags(st) & SfDataless) != 0;
}
