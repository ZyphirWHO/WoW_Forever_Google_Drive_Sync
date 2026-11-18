using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Service;

/// <summary>
/// Once, on start: if the cloud folder has no "InterfaceSync" folder but has one an earlier build or
/// v1.1 used ("WoWSync" or "WoW Sync"), rename it to "InterfaceSync". Each version's commit file is
/// backed up first. Every PC is updated before its first run, so no dual-folder support is kept.
/// </summary>
public static class CloudFolderMigration
{
    public static void Run(IFileSystem fs, string cloudRoot, BackupStore backups, ILog log)
    {
        var target = PathUtil.Join(cloudRoot, CloudLayout.AppFolderName);
        var legacy = ProductInfo.Legacy.CloudFolderNames
            .Select(n => PathUtil.Join(cloudRoot, n))
            .Where(fs.DirectoryExists)
            .ToList();
        if (legacy.Count == 0) return;

        if (fs.DirectoryExists(target))
        {
            log.Warn($"Both {target} and {string.Join(" and ", legacy)} exist. Using {target}; the other folder is left untouched.");
            return;
        }
        if (legacy.Count > 1)
        {
            log.Warn($"Found {string.Join(" and ", legacy)}. Renaming {legacy[0]} to {target}; {string.Join(", ", legacy.Skip(1))} is left untouched.");
        }

        var source = legacy[0];
        foreach (var versionDir in fs.EnumerateDirectories(source))
        {
            var commits = fs.EnumerateFiles(versionDir)
                .Where(f => PathUtil.Name(f).StartsWith("WTF-Account.", StringComparison.OrdinalIgnoreCase)
                            && PathUtil.Name(f).EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(f => fs.GetFileInfo(f))
                .Select(i => new ManifestEntry(PathUtil.Name(i.FullPath), i.Size, i.LastWriteUtc))
                .ToList();
            if (commits.Count > 0)
                backups.CreateFromFiles(PathUtil.Name(versionDir), versionDir, commits, "cloud-folder-rename");
        }

        try
        {
            fs.MoveDirectory(source, target);
            log.Info($"Renamed the cloud folder {source} to {target} (commit files backed up first).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Error($"Could not rename {source} to {target}: {e.Message}. Syncing waits until it can; nothing was changed.");
            throw;
        }
    }
}
