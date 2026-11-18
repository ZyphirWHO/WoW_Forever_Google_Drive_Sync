using System.Globalization;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Backup;

public sealed record BackupInfo(string VersionFolder, string Path, DateTime CreatedUtc, string Reason, int FileCount);

/// <summary>
/// Timestamped copies under %LOCALAPPDATA%\InterfaceSync\Backups\&lt;version&gt;\, newest 10 kept per version.
/// Every overwrite or delete in a game folder is preceded by one of these.
/// </summary>
public sealed class BackupStore
{
    public const int KeepPerVersion = 10;
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";

    private readonly IFileSystem _fs;
    private readonly IClock _clock;

    public BackupStore(IFileSystem fs, IClock clock, string root)
    {
        _fs = fs;
        _clock = clock;
        Root = root;
    }

    public string Root { get; }

    /// <summary>Copies <paramref name="sourceFolder"/> into a new backup. Returns null if the source is missing or empty.</summary>
    public BackupInfo? Create(string versionFolder, string sourceFolder, string reason)
    {
        var files = FolderScanner.AllFiles(_fs, sourceFolder);
        if (files.Count == 0) return null;

        var now = _clock.UtcNow;
        var name = $"{now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Sanitize(reason)}";
        var dest = PathUtil.Join(Root, versionFolder, name);
        for (int i = 2; _fs.DirectoryExists(dest); i++) dest = PathUtil.Join(Root, versionFolder, $"{name}-{i}");

        TreeCopy.CopyFiles(_fs, sourceFolder, dest, files);
        Prune(versionFolder, reason);
        return new BackupInfo(versionFolder, dest, now, reason, files.Count);
    }

    /// <summary>Backs up only the listed files (e.g. the cloud files a push is about to replace).</summary>
    public BackupInfo CreateFromFiles(string versionFolder, string sourceFolder, IReadOnlyCollection<ManifestEntry> files, string reason)
    {
        var now = _clock.UtcNow;
        var name = $"{now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Sanitize(reason)}";
        var dest = PathUtil.Join(Root, versionFolder, name);
        for (int i = 2; _fs.DirectoryExists(dest); i++) dest = PathUtil.Join(Root, versionFolder, $"{name}-{i}");
        TreeCopy.CopyFiles(_fs, sourceFolder, dest, files);
        Prune(versionFolder, reason);
        return new BackupInfo(versionFolder, dest, now, reason, files.Count);
    }

    public IReadOnlyList<BackupInfo> List(string versionFolder)
    {
        var dir = PathUtil.Join(Root, versionFolder);
        if (!_fs.DirectoryExists(dir)) return [];
        var result = new List<BackupInfo>();
        foreach (var sub in _fs.EnumerateDirectories(dir))
        {
            var name = PathUtil.Name(sub);
            if (name.Length < StampFormat.Length ||
                !DateTime.TryParseExact(name[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
                continue;
            var reason = name.Length > StampFormat.Length + 1 ? name[(StampFormat.Length + 1)..] : "";
            result.Add(new BackupInfo(versionFolder, sub, created, reason, FolderScanner.AllFiles(_fs, sub).Count));
        }
        return result.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Keeps the newest 10 per version, counting cloud-copy backups ("cloud-...") and this PC's backups
    /// separately, so frequent push backups never push out the backups of this PC's own settings.
    /// </summary>
    private void Prune(string versionFolder, string reason)
    {
        bool cloud = IsCloudReason(reason);
        foreach (var old in List(versionFolder).Where(b => IsCloudReason(b.Reason) == cloud).Skip(KeepPerVersion))
            _fs.DeleteDirectory(old.Path);
    }

    public static bool IsCloudReason(string reason) => reason.StartsWith("cloud-", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string reason)
    {
        var chars = reason.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}

public static class TreeCopy
{
    /// <summary>Copies the listed files from one root to another, creating folders as needed.</summary>
    public static void CopyFiles(IFileSystem fs, string sourceRoot, string destRoot, IEnumerable<ManifestEntry> files)
    {
        fs.CreateDirectory(destRoot);
        foreach (var entry in files)
            fs.CopyFile(PathUtil.FromRelative(sourceRoot, entry.RelativePath), PathUtil.FromRelative(destRoot, entry.RelativePath));
    }
}
