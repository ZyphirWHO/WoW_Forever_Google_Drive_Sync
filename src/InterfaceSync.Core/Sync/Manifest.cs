using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;

namespace InterfaceSync.Core.Sync;

/// <summary>One file in a synced folder. RelativePath always uses '\'.</summary>
public sealed record ManifestEntry(string RelativePath, long Size, DateTime LastWriteUtc);

/// <summary>A snapshot of a folder: which files, what size, when written.</summary>
public sealed class Manifest
{
    /// <summary>Timestamps within this distance count as equal (FAT/Drive rounding).</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public List<ManifestEntry> Entries { get; init; } = [];

    [JsonIgnore]
    public int Count => Entries.Count;
    [JsonIgnore]
    public bool IsEmpty => Entries.Count == 0;
    [JsonIgnore]
    public long TotalBytes => Entries.Sum(e => e.Size);
    [JsonIgnore]
    public DateTime? NewestUtc => Entries.Count == 0 ? null : Entries.Max(e => e.LastWriteUtc);

    public Dictionary<string, ManifestEntry> ToLookup() =>
        Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

    public static bool SameFile(ManifestEntry a, ManifestEntry b) =>
        a.Size == b.Size && (a.LastWriteUtc - b.LastWriteUtc).Duration() <= TimeTolerance;

    /// <summary>Same set of files with the same sizes and times.</summary>
    public bool IsEquivalentTo(Manifest? other)
    {
        if (other == null || other.Count != Count) return false;
        var lookup = other.ToLookup();
        foreach (var e in Entries)
            if (!lookup.TryGetValue(e.RelativePath, out var o) || !SameFile(e, o)) return false;
        return true;
    }
}

/// <summary>Result of scanning a folder, including things the engine refuses to touch.</summary>
public sealed class FolderScan
{
    public required string Root { get; init; }
    public bool Exists { get; init; }
    public Manifest Manifest { get; init; } = new();

    /// <summary>Drive conflict copies such as "Name (1).lua": reported, never synced.</summary>
    public List<string> ConflictCopies { get; } = [];

    /// <summary>Folder links / symlinks found inside: skipped, never followed.</summary>
    public List<string> Links { get; } = [];

    /// <summary>Files whose bytes are only in the cloud (Drive streaming, not "Available offline").</summary>
    public List<string> OnlineOnly { get; } = [];

    /// <summary>Temporary files from an interrupted InterfaceSync copy or a Drive transfer.</summary>
    public List<string> TempFiles { get; } = [];
}

public static class FolderScanner
{
    public static readonly string TempSuffix = "." + ProductInfo.Id.ToLowerInvariant() + "-tmp";

    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "Thumbs.db", ".DS_Store", ".dropbox", ".dropbox.attr",
    };

    /// <summary>Conflict copy from Google Drive or Dropbox (names that need no neighbours to recognise).</summary>
    public static bool IsConflictCopyName(string name) =>
        GoogleDriveProvider.IsConflictName(name) || DropboxProvider.IsConflictName(name);

    /// <summary>Conflict copy from any supported cloud app (see <see cref="CloudProviderCatalog.IsAnyConflictCopy"/>).</summary>
    public static bool IsConflictCopyName(string name, ISet<string> siblings) => CloudProviderCatalog.IsAnyConflictCopy(name, siblings);

    private static bool IsDriveTempName(string name) =>
        name.StartsWith(".tmp.drive", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("~$", StringComparison.Ordinal);

    /// <summary>
    /// Every regular file under root (conflict copies and temp files included, links skipped).
    /// Used for backups so nothing is lost when a folder is replaced.
    /// </summary>
    public static List<ManifestEntry> AllFiles(IFileSystem fs, string root)
    {
        var result = new List<ManifestEntry>();
        if (!fs.DirectoryExists(root) || fs.IsReparsePoint(root)) return result;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in fs.EnumerateFiles(dir))
            {
                var info = fs.GetFileInfo(file);
                if (info.IsReparsePoint) continue;
                result.Add(new ManifestEntry(PathUtil.Relative(root, file), info.Size, info.LastWriteUtc));
            }
            foreach (var sub in fs.EnumerateDirectories(dir))
                if (!fs.IsReparsePoint(sub)) stack.Push(sub);
        }
        return result;
    }

    public static FolderScan Scan(IFileSystem fs, string root)
    {
        if (!fs.DirectoryExists(root))
            return new FolderScan { Root = root, Exists = false };

        var entries = new List<ManifestEntry>();
        var scan = new FolderScan { Root = root, Exists = true, Manifest = new Manifest { Entries = entries } };
        if (fs.IsReparsePoint(root))
        {
            scan.Links.Add(root);
            return scan;
        }
        Walk(fs, root, root, scan, entries);
        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return scan;
    }

    private static void Walk(IFileSystem fs, string root, string dir, FolderScan scan, List<ManifestEntry> entries)
    {
        var files = fs.EnumerateFiles(dir).ToList();
        var dirs = fs.EnumerateDirectories(dir).ToList();
        var fileNames = new HashSet<string>(files.Select(PathUtil.Name), StringComparer.OrdinalIgnoreCase);
        var dirNames = new HashSet<string>(dirs.Select(PathUtil.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var name = PathUtil.Name(file);
            var rel = PathUtil.Relative(root, file);
            if (IgnoredNames.Contains(name)) continue;
            if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(ProductInfo.Legacy.TempSuffix, StringComparison.OrdinalIgnoreCase)
                || IsDriveTempName(name))
            {
                scan.TempFiles.Add(rel);
                continue;
            }
            var info = fs.GetFileInfo(file);
            if (info.IsReparsePoint) { scan.Links.Add(rel); continue; }
            if (IsConflictCopyName(name, fileNames)) { scan.ConflictCopies.Add(rel); continue; }
            if (info.IsOnlineOnly) scan.OnlineOnly.Add(rel);
            entries.Add(new ManifestEntry(rel, info.Size, info.LastWriteUtc));
        }

        foreach (var sub in dirs)
        {
            var name = PathUtil.Name(sub);
            var rel = PathUtil.Relative(root, sub);
            if (fs.IsReparsePoint(sub)) { scan.Links.Add(rel); continue; }
            if (IsDriveTempName(name)) { scan.TempFiles.Add(rel); continue; }
            if (IsConflictCopyName(name, dirNames)) { scan.ConflictCopies.Add(rel); continue; }
            Walk(fs, root, sub, scan, entries);
        }
    }
}
