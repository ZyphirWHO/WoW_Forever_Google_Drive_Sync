namespace WoWSync.Core.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public enum RegistryHive { CurrentUser, LocalMachine }

/// <summary>Read-mostly registry access. Views: 64-bit view first; the WOW6432Node path is passed explicitly.</summary>
public interface IRegistry
{
    /// <summary>Returns the value as string (REG_SZ / REG_EXPAND_SZ), or null when missing.</summary>
    string? GetString(RegistryHive hive, string keyPath, string valueName);

    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string keyPath);

    IReadOnlyList<string> GetValueNames(RegistryHive hive, string keyPath);

    void SetString(RegistryHive hive, string keyPath, string valueName, string value);

    void DeleteValue(RegistryHive hive, string keyPath, string valueName);
}

public sealed record ProcessEntry(int Id, string Name, string? ExecutablePath, string? CommandLine = null);

public interface IProcessList
{
    /// <summary>
    /// Processes whose image name starts with one of the given prefixes (case-insensitive),
    /// e.g. "wow" for Wow.exe / WowClassic.exe / WowT.exe.
    /// </summary>
    IReadOnlyList<ProcessEntry> GetProcesses(params string[] namePrefixes);
}

/// <summary>Per-user folders and identity. The app always runs as the signed-in user.</summary>
public interface IUserEnvironment
{
    string LocalAppData { get; }
    string UserProfile { get; }
    string UserName { get; }
    bool IsElevated { get; }
}
