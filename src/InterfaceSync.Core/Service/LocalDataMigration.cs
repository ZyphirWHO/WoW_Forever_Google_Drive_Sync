using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Service;

/// <summary>
/// Moves this PC's own data (settings, sync state, backups, logs) from the folder earlier 2.0 builds used,
/// %LOCALAPPDATA%\WoWSync, to %LOCALAPPDATA%\InterfaceSync, once. Only a folder holding a 2.0 settings.json
/// is moved, so anything WoWSync 1.1 left there is never touched.
/// </summary>
public static class LocalDataMigration
{
    /// <returns>What happened, for the log; null when there was nothing to do.</returns>
    public static string? Run(IFileSystem fs, string localAppData)
    {
        var target = PathUtil.Join(localAppData, ProductInfo.Id);
        var legacy = PathUtil.Join(localAppData, ProductInfo.Legacy.LocalDataFolder);
        if (fs.DirectoryExists(target) || !fs.DirectoryExists(legacy)) return null;
        if (!fs.FileExists(PathUtil.Join(legacy, "settings.json"))) return null;
        try
        {
            fs.MoveDirectory(legacy, target);
            return $"Moved this PC's {ProductInfo.Name} data (settings, sync state, backups, logs) from {legacy} to {target}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not move {legacy} to {target} ({e.Message}). Settings start fresh in {target}; the old folder is untouched.";
        }
    }
}
