using System.Text;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Platform;

/// <summary>Real disk access, managed code only (no robocopy, no child processes).</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool IsReparsePoint(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) return false;
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget != null;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);

    public FileEntryInfo GetFileInfo(string path)
    {
        var fi = new FileInfo(path);
        var attrs = fi.Attributes;
        if (fi.LinkTarget != null) attrs |= FileAttributes.ReparsePoint;
        return new FileEntryInfo(fi.FullName, fi.Length, fi.LastWriteTimeUtc, attrs);
    }

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destination)) ClearReadOnly(destination);
        CopyShared(source, destination);
        // Windows CopyFile already keeps the write time; set it explicitly so every platform agrees.
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    public void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        ClearReadOnly(path);
        WithRetry(() => File.Delete(path));
    }

    public void MoveFile(string source, string destination)
    {
        if (File.Exists(destination)) ClearReadOnly(destination);
        File.Move(source, destination, overwrite: true);
    }

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if (IsReparsePoint(path)) { RemoveLink(path); return; }

        // Windows refuses to remove a folder marked read-only (seen on a real WoW install:
        // WTF\Account\SavedVariables), so clear the mark first, as for files.
        ClearReadOnly(path);
        foreach (var sub in Directory.EnumerateDirectories(path))
            DeleteDirectory(sub); // links inside are removed, never followed
        foreach (var file in Directory.EnumerateFiles(path))
            DeleteFile(file);
        WithRetry(() => Directory.Delete(path, recursive: false));
    }

    /// <summary>Antivirus or the search indexer can hold a handle for a moment; try a few times before failing.</summary>
    private static void WithRetry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (attempt < 4 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    public void RemoveLink(string path)
    {
        if (!IsReparsePoint(path))
            throw new InvalidOperationException($"Refusing to remove '{path}': it is not a folder link.");
        try
        {
            // Non-recursive delete of a junction/symlink removes the link, not the target.
            Directory.Delete(path, recursive: false);
        }
        catch (IOException) when (!OperatingSystem.IsWindows())
        {
            File.Delete(path); // Unix directory symlink
        }
    }

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);

    public byte[] ReadAllBytes(string path)
    {
        // Share everything: never block the program that owns the file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    public void AppendAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(path, contents, Utf8NoBom);
    }

    public IReadOnlyList<DriveEntry> GetDrives()
    {
        var result = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            var kind = d.DriveType switch
            {
                DriveType.Fixed => DriveKind.Fixed,
                DriveType.Removable => DriveKind.Removable,
                DriveType.Network => DriveKind.Network,
                _ => DriveKind.Other,
            };
            string label = "";
            bool ready = false;
            try { ready = d.IsReady; if (ready) label = d.VolumeLabel; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            result.Add(new DriveEntry(d.RootDirectory.FullName, kind, label, ready));
        }
        return result;
    }

    /// <summary>
    /// Copies with full sharing on the source, so a file another program has open for writing
    /// (Battle.net's product.db, WoW's own files) can still be copied.
    /// </summary>
    private static void CopyShared(string source, string destination)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
    }

    private static void ClearReadOnly(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }
}
