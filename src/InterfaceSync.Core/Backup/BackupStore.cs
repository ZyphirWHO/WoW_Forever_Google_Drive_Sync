using System.Globalization;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Backup;

public sealed record BackupInfo(string VersionFolder, string Path, DateTime CreatedUtc, string Reason, int FileCount)
{
    /// <summary>
    /// Holds only the files a sync was about to change (restored by putting them back over the current settings),
    /// rather than a whole copy (restored by replacing the settings). Earlier builds' "cloud-before-push" backups
    /// could be either, so they are treated as partial: putting files back never removes anything.
    /// </summary>
    public bool Partial => Reason.EndsWith(BackupStore.ChangesSuffix, StringComparison.OrdinalIgnoreCase) ||
                           Reason.Equals("cloud-before-push", StringComparison.OrdinalIgnoreCase);

    /// <summary>A snapshot the player saved and named: kept until they delete it, outside the 5-backup limit.</summary>
    public bool Pinned => Reason.StartsWith(BackupStore.SnapshotPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Taken automatically when the game was updated; the newest 3 are kept, outside the 5-backup limit.</summary>
    public bool GameUpdate => Reason.StartsWith(BackupStore.GameUpdatePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name the player gave a snapshot.</summary>
    public string? SnapshotName => Pinned ? Reason[BackupStore.SnapshotPrefix.Length..] : null;

    /// <summary>Plain words for the restore list.</summary>
    public string Description => Pinned ? L.F("Snapshot: {0}", SnapshotName)
        : GameUpdate ? L.F("Before game update {0}", Reason[BackupStore.GameUpdatePrefix.Length..])
        : L.T(EnglishDescription);

    /// <summary>The description in English (translated where shown; see <see cref="L.Implicit"/>).</summary>
    private string EnglishDescription => (Reason.EndsWith(BackupStore.ChangesSuffix, StringComparison.OrdinalIgnoreCase) ? Reason[..^BackupStore.ChangesSuffix.Length] : Reason) switch
    {
        "before-pull" or "first-join" => "Before downloading settings",
        "before-keep-cloud" => "Before keeping the cloud settings",
        "before-restore" => "Before restoring a backup",
        "cloud-before-push" or "cloud-before-push-all" => "Cloud copy, before uploading",
        "cloud-copy-conflict" => "The cloud copy you chose not to keep",
        var r when r.StartsWith("cloud-before-provider-move", StringComparison.OrdinalIgnoreCase) => "Cloud copy, before moving cloud folder",
        var r => r.Replace('-', ' '),
    };

    /// <summary>True when nothing had changed since this existing backup, so no new copy was made.</summary>
    public bool Reused { get; init; }
}

/// <summary>
/// Timestamped copies under %LOCALAPPDATA%\InterfaceSync\Backups\&lt;version&gt;\, at most 5 kept per game version.
/// Every overwrite or delete in a game folder is preceded by one of these.
/// </summary>
public sealed class BackupStore
{
    public const int KeepPerVersion = 5;

    /// <summary>Ends the reason of a backup that holds only the files a sync changed, e.g. "before-pull-changes".</summary>
    public const string ChangesSuffix = "-changes";

    /// <summary>Starts the reason of a named snapshot ("snapshot-Raid UI").</summary>
    public const string SnapshotPrefix = "snapshot-";

    /// <summary>Starts the reason of the automatic copy made when the game updates ("game-update-11.0.7.57788").</summary>
    public const string GameUpdatePrefix = "game-update-";
    public const int KeepGameUpdates = 3;
    public const int MaxSnapshotNameLength = 40;
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

        // Identical to the newest backup of the same kind? Reuse it. Without this, a sync retried every poll
        // made a new backup each time, and pruning to 10 then deleted the backup that mattered (laptop, 2026-11-18).
        var newest = List(versionFolder).FirstOrDefault(b => !b.Pinned && !b.GameUpdate && IsCloudReason(b.Reason) == IsCloudReason(reason));
        if (newest != null && SameFiles(files, FolderScanner.AllFiles(_fs, newest.Path)))
            return newest with { Reused = true };

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
        // The same files as the newest backup made for the same reason (a sync retried every poll)? Reuse it,
        // so retries never fill the 5 places and push out the backup that matters.
        var current = FolderScanner.Scan(_fs, sourceFolder).Manifest.ToLookup();
        var wanted = files.Select(f => current.TryGetValue(f.RelativePath, out var e) ? e : f).ToList();
        var newest = List(versionFolder).FirstOrDefault(b => b.Reason.Equals(Sanitize(reason), StringComparison.OrdinalIgnoreCase));
        if (newest != null && SameFiles(wanted, FolderScanner.AllFiles(_fs, newest.Path)))
            return newest with { Reused = true };

        var now = _clock.UtcNow;
        var name = $"{now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Sanitize(reason)}";
        var dest = PathUtil.Join(Root, versionFolder, name);
        for (int i = 2; _fs.DirectoryExists(dest); i++) dest = PathUtil.Join(Root, versionFolder, $"{name}-{i}");
        TreeCopy.CopyFiles(_fs, sourceFolder, dest, files);
        Prune(versionFolder, reason);
        return new BackupInfo(versionFolder, dest, now, reason, files.Count);
    }

    /// <summary>
    /// Saves a whole copy of <paramref name="sourceFolder"/> under the name the player chose. Snapshots are never
    /// pruned; the player deletes them. Returns null if the folder is empty.
    /// </summary>
    public BackupInfo? CreateSnapshot(string versionFolder, string sourceFolder, string name)
    {
        var clean = CleanSnapshotName(name);
        if (clean.Length == 0) throw new ArgumentException("Give the snapshot a name.", nameof(name));
        return CreateWhole(versionFolder, sourceFolder, SnapshotPrefix + clean);
    }

    /// <summary>The automatic copy made when a new game version is first seen. The newest 3 are kept.</summary>
    public BackupInfo? CreateGameUpdateSnapshot(string versionFolder, string sourceFolder, string clientVersion)
    {
        var info = CreateWhole(versionFolder, sourceFolder, GameUpdatePrefix + Sanitize(clientVersion));
        Prune(versionFolder);
        return info;
    }

    /// <summary>Deletes a snapshot (only snapshots and game-update copies; regular backups are managed automatically).</summary>
    public void Delete(BackupInfo backup)
    {
        if (!backup.Pinned && !backup.GameUpdate) throw new InvalidOperationException("Only snapshots can be deleted.");
        if (!PathUtil.IsUnder(backup.Path, PathUtil.Join(Root, backup.VersionFolder)))
            throw new InvalidOperationException($"{backup.Path} is not in the backups folder.");
        _fs.DeleteDirectory(backup.Path);
    }

    /// <summary>Letters, digits, spaces, '-' and '_' only (it becomes part of a folder name), at most 40 characters.</summary>
    public static string CleanSnapshotName(string name)
    {
        var chars = (name ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : ' ').ToArray();
        var clean = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxSnapshotNameLength ? clean[..MaxSnapshotNameLength].TrimEnd() : clean;
    }

    private BackupInfo? CreateWhole(string versionFolder, string sourceFolder, string reason)
    {
        var files = FolderScanner.AllFiles(_fs, sourceFolder);
        if (files.Count == 0) return null;
        var now = _clock.UtcNow;
        var name = $"{now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{reason}";
        var dest = PathUtil.Join(Root, versionFolder, name);
        for (int i = 2; _fs.DirectoryExists(dest); i++) dest = PathUtil.Join(Root, versionFolder, $"{name}-{i}");
        TreeCopy.CopyFiles(_fs, sourceFolder, dest, files);
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
    /// Keeps at most 5 backups per game version, the newest ones, and deletes the rest. Three are always among the 5:
    /// the newest backup of this PC's own settings, of the cloud copy, and of a "settings changed in two places"
    /// copy you chose not to keep. So a run of uploads can never purge the last backup of this PC's settings, or the
    /// copy you set aside in a conflict. Returns how many were deleted.
    /// </summary>
    public int Prune(string versionFolder, string? reason = null)
    {
        var listed = List(versionFolder);
        var oldGameUpdates = listed.Where(b => b.GameUpdate).Skip(KeepGameUpdates).ToList();
        var all = listed.Where(b => !b.Pinned && !b.GameUpdate).ToList();   // snapshots don't count towards the 5
        if (all.Count <= KeepPerVersion && oldGameUpdates.Count == 0) return 0;
        var mustKeep = new[]
        {
            all.FirstOrDefault(b => !IsCloudReason(b.Reason)),
            all.FirstOrDefault(b => IsCloudReason(b.Reason)),
            all.FirstOrDefault(b => b.Reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)),
        }.OfType<BackupInfo>().Distinct().ToList();
        var keep = mustKeep.Concat(all.Where(b => !mustKeep.Contains(b))).Take(KeepPerVersion).ToHashSet();
        int deleted = 0;
        foreach (var old in all.Where(b => !keep.Contains(b)).Concat(oldGameUpdates))
        {
            try { _fs.DeleteDirectory(old.Path); deleted++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // tried again next time
        }
        return deleted;
    }

    /// <summary>Prunes every game version's backups (at start, so folders from earlier builds shrink to 5 too).</summary>
    public int PruneAll()
    {
        if (!_fs.DirectoryExists(Root)) return 0;
        return _fs.EnumerateDirectories(Root).Sum(dir => Prune(PathUtil.Name(dir)));
    }

    private static bool SameFiles(List<ManifestEntry> a, List<ManifestEntry> b)
    {
        if (a.Count != b.Count) return false;
        var lookup = b.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        return a.All(e => lookup.TryGetValue(e.RelativePath, out var o) && Manifest.SameFile(e, o));
    }

    public static bool IsCloudReason(string reason) => reason.StartsWith("cloud-", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string reason)
    {
        var chars = reason.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray();
        return new string(chars).Trim('-', '.');
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
