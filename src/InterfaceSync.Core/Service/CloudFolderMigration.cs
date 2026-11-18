using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Service;

/// <summary>
/// Once, on start: if the cloud folder has no "InterfaceSync" folder but has one an earlier build or v1.1 used
/// ("WoWSync" or "WoW Sync"), COPY each version's settings and commit file into "InterfaceSync" and leave a
/// README in the old folder pointing to the new one.
///
/// Folders inside a cloud app's folder are never renamed, moved or deleted. A rename made on a PC whose cloud
/// copy is out of date can't be reconciled by the cloud app: on 2026-11-18 a second rename of the same folder
/// made Google Drive upload the contents as loose items into the root of My Drive. Copying only adds files.
/// </summary>
public static class CloudFolderMigration
{
    public const string ReadmeName = "README - moved to InterfaceSync.txt";

    public static void Run(IFileSystem fs, string cloudRoot, BackupStore backups, ILog log)
    {
        var target = PathUtil.Join(cloudRoot, CloudLayout.AppFolderName);
        var legacy = ProductInfo.Legacy.CloudFolderNames
            .Select(n => PathUtil.Join(cloudRoot, n))
            .Where(fs.DirectoryExists)
            .ToList();
        if (legacy.Count == 0) return;

        // Both names present: use the new one and never touch the old one.
        if (fs.DirectoryExists(target)) return;

        var source = legacy[0];
        if (legacy.Count > 1)
            log.Warn($"Found {string.Join(" and ", legacy)}. Copying from {source}; the others are left untouched.");

        int copied = 0;
        foreach (var versionDir in fs.EnumerateDirectories(source))
        {
            if (fs.IsReparsePoint(versionDir)) continue;
            var version = PathUtil.Name(versionDir);
            var newVersion = PathUtil.Join(target, version);

            // Only the settings and the commit file: never AddOns or anything else that was in the old folder.
            var account = PathUtil.Join(versionDir, CloudLayout.AccountFolderName);
            var files = FolderScanner.AllFiles(fs, account)
                .Where(f => !f.RelativePath.EndsWith(FolderScanner.TempSuffix, StringComparison.OrdinalIgnoreCase)
                            && !f.RelativePath.EndsWith(ProductInfo.Legacy.TempSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count > 0)
            {
                TreeCopy.CopyFiles(fs, account, PathUtil.Join(newVersion, CloudLayout.AccountFolderName), files);
                copied += files.Count;
            }

            // The commit file under the current name, so other PCs see the same commit id and don't re-download.
            foreach (var name in new[] { CloudLayout.CommitFileName, ProductInfo.Legacy.CommitFileName })
            {
                var commit = PathUtil.Join(versionDir, name);
                if (!fs.FileExists(commit)) continue;
                fs.CreateDirectory(newVersion);
                fs.CopyFile(commit, PathUtil.Join(newVersion, CloudLayout.CommitFileName));
                break;
            }
        }

        fs.WriteAllText(PathUtil.Join(source, ReadmeName),
            $"{ProductInfo.Name} now keeps its settings in the folder \"{CloudLayout.AppFolderName}\" next to this one.{Environment.NewLine}" +
            $"This folder is no longer used. It was left as it was; you can delete it once every PC runs {ProductInfo.Name}.{Environment.NewLine}");
        log.Info($"Copied {copied} settings files from {source} to {target}. The old folder was left as it was, with a README pointing to the new one.");
    }
}
