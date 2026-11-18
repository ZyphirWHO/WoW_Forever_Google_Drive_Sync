using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;

namespace InterfaceSync.Core.Sync;

/// <summary>
/// Tells which game versions are running, by executable path: retail and Forever both run
/// Wow.exe, so the folder the exe lives in is what tells them apart.
/// </summary>
public sealed class GameWatcher
{
    private readonly IProcessList _processes;

    public GameWatcher(IProcessList processes) => _processes = processes;

    public IReadOnlyList<ProcessEntry> Snapshot() => _processes.GetProcesses("wow");

    public static bool IsRunning(GameVersion version, IReadOnlyList<ProcessEntry> snapshot)
    {
        foreach (var p in snapshot)
        {
            if (p.ExecutablePath is { Length: > 0 } path)
            {
                if (PathUtil.IsUnder(path, version.FolderPath)) return true;
                continue;
            }
            // Path unreadable (e.g. access denied): if the name matches this version's exe, assume it is
            // running. Never writing to a running game matters more than an occasional delayed sync.
            if (version.Executables.Any(exe =>
                    PathUtil.Name(exe).Equals(p.Name + ".exe", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public bool IsRunning(GameVersion version) => IsRunning(version, Snapshot());
}
