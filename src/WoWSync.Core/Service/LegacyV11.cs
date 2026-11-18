using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Service;

/// <summary>Finds leftovers of WoWSync v1.1 (read-only here; removal is the migration step).</summary>
public static class LegacyV11
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AgentRunPrefix = "WoWSyncAgent-";

    /// <summary>Names of v1.1 startup entries (HKCU\...\Run\WoWSyncAgent-*).</summary>
    public static IReadOnlyList<string> FindRunEntries(IRegistry registry) =>
        registry.GetValueNames(RegistryHive.CurrentUser, RunKey)
            .Where(n => n.StartsWith(AgentRunPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
