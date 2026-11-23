using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;

namespace InterfaceSync.Core.Sync;

/// <summary>A copy of an Interface Sync folder made by the cloud app (e.g. "InterfaceSync (1)").</summary>
public sealed record DuplicateFolder(string Path, string Name, string CopyOf, int FileCount, DateTime? NewestUtc);

/// <summary>
/// Finds the copies Google Drive, OneDrive and Dropbox make when two computers write the same folder at nearly the
/// same time: "InterfaceSync (1)", "InterfaceSync (PC's conflicted copy …)", "InterfaceSync-PCNAME", and the same for
/// a game version's folder inside it. Interface Sync never reads, moves or deletes them; it only points them out.
/// </summary>
public static class DuplicateFolders
{
    public static IReadOnlyList<DuplicateFolder> Find(IFileSystem fs, string cloudRoot)
    {
        var found = new List<DuplicateFolder>();
        var original = CloudLayout.AppFolderName;
        Look(fs, cloudRoot, name => IsCopyOf(name, original) ? original : null, found);
        var appRoot = PathUtil.Join(cloudRoot, original);
        if (fs.DirectoryExists(appRoot))
            Look(fs, appRoot, _ => "", found);   // version folders: the original is whatever the copy is named after
        return found;
    }

    private static void Look(IFileSystem fs, string folder, Func<string, string?> originalFor, List<DuplicateFolder> found)
    {
        if (!fs.DirectoryExists(folder)) return;
        List<string> dirs;
        try { dirs = fs.EnumerateDirectories(folder).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        var names = new HashSet<string>(dirs.Select(PathUtil.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            var name = PathUtil.Name(dir);
            if (!CloudProviderCatalog.IsAnyConflictCopy(name, names)) continue;
            var copyOf = originalFor(name);
            if (copyOf == null) continue;
            if (copyOf.Length == 0) copyOf = names.FirstOrDefault(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase) && name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) ?? name;
            int count = 0;
            DateTime? newest = null;
            try
            {
                foreach (var f in FolderScanner.AllFiles(fs, dir))
                {
                    count++;
                    if (newest == null || f.LastWriteUtc > newest) newest = f.LastWriteUtc;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            found.Add(new DuplicateFolder(dir, name, copyOf, count, newest));
        }
    }

    private static bool IsCopyOf(string name, string original) =>
        !name.Equals(original, StringComparison.OrdinalIgnoreCase) && name.StartsWith(original, StringComparison.OrdinalIgnoreCase);
}
