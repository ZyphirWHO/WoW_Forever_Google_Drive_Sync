namespace WoWSync.Core.Abstractions;

/// <summary>Metadata for one file, as the sync engine needs it.</summary>
public sealed record FileEntryInfo(string FullPath, long Size, DateTime LastWriteUtc, FileAttributes Attributes)
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: the file is online-only (cloud placeholder).</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: placeholder that is fetched when opened.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>True when the bytes are not on this PC yet (Drive streaming "online-only").</summary>
    public bool IsOnlineOnly =>
        (Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;
}

public enum DriveKind { Fixed, Removable, Network, Other }

public sealed record DriveEntry(string RootPath, DriveKind Kind, string VolumeLabel, bool IsReady);

/// <summary>
/// The only way Core touches the disk. Small on purpose so tests can fake it
/// (in-memory for detection, real temp folders for the two-PC sync tests).
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);

    /// <summary>True when the path itself is a symlink/junction/other reparse point.</summary>
    bool IsReparsePoint(string path);

    /// <summary>Immediate child directories (full paths). Includes reparse points; callers decide.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Immediate child files (full paths).</summary>
    IEnumerable<string> EnumerateFiles(string path);

    FileEntryInfo GetFileInfo(string path);

    void CreateDirectory(string path);

    /// <summary>Copies a file, overwriting, and preserves the source's last-write time.</summary>
    void CopyFile(string source, string destination);

    void DeleteFile(string path);

    /// <summary>Moves/renames a file, overwriting the destination.</summary>
    void MoveFile(string source, string destination);

    /// <summary>Deletes a directory tree. Must not follow reparse points inside it (remove the link only).</summary>
    void DeleteDirectory(string path);

    /// <summary>Removes a directory link/junction itself, never its target.</summary>
    void RemoveLink(string path);

    void MoveDirectory(string source, string destination);

    string ReadAllText(string path);

    /// <summary>Writes atomically where possible (temp file + replace).</summary>
    void WriteAllText(string path, string contents);

    void AppendAllText(string path, string contents);

    IReadOnlyList<DriveEntry> GetDrives();
}
