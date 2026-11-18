using System.Text.Json;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Detection;

public enum DriveMode { Unknown, Streaming, Mirror }

/// <summary>The cloud folder InterfaceSync keeps its data in (the app on this PC does the uploading).</summary>
public enum CloudProvider { GoogleDrive, OneDrive, Dropbox }

public static class CloudProviders
{
    public static readonly CloudProvider[] All = [CloudProvider.GoogleDrive, CloudProvider.OneDrive, CloudProvider.Dropbox];

    public static string DisplayName(this CloudProvider p) => p switch
    {
        CloudProvider.OneDrive => "OneDrive",
        CloudProvider.Dropbox => "Dropbox",
        _ => "Google Drive",
    };

    /// <summary>How to make sure the app folder's files are really on this computer.</summary>
    public static string KeepOfflineHint(this CloudProvider p, HostOs os) => os == HostOs.MacOS
        ? p switch
        {
            CloudProvider.OneDrive => $"In Finder, Control-click the {ProductInfo.Id} folder in OneDrive and choose Always Keep on This Device.",
            CloudProvider.Dropbox => $"In Finder, Control-click the {ProductInfo.Id} folder in Dropbox and choose Make available offline.",
            _ => $"In Finder, Control-click My Drive/{ProductInfo.Id} in Google Drive and choose Offline access → Available offline.",
        }
        : p.KeepOfflineHint();

    /// <summary>How to make sure the app folder's files are really on this PC (Windows wording).</summary>
    public static string KeepOfflineHint(this CloudProvider p) => p switch
    {
        CloudProvider.OneDrive => $"In File Explorer, right-click the {ProductInfo.Id} folder in OneDrive and choose Always keep on this device.",
        CloudProvider.Dropbox => $"In File Explorer, right-click the {ProductInfo.Id} folder in Dropbox and choose Make available offline.",
        _ => $"In File Explorer, right-click My Drive\\{ProductInfo.Id} and choose Offline access → Available offline.",
    };
}

/// <summary>
/// Where the chosen cloud app keeps its synced folder on this PC. <see cref="MyDrivePath"/> is the
/// folder InterfaceSync puts its "InterfaceSync" folder in: Google Drive's "My Drive", the OneDrive folder, or the Dropbox folder.
/// </summary>
public sealed record DriveDetectionResult(string? MyDrivePath, DriveMode Mode, DetectionSource Source, IReadOnlyList<string> Attempts,
    CloudProvider Provider = CloudProvider.GoogleDrive)
{
    public bool Found => MyDrivePath != null;
}

/// <summary>
/// Finds the user's "My Drive" folder for Google Drive for desktop, in streaming (G:\My Drive)
/// or mirror (%USERPROFILE%\My Drive or a chosen folder) mode. Re-run on every start.
/// </summary>
public sealed class DriveDetector
{
    // VERIFY on the real PCs: DriveFS writes per-account JSON preferences here.
    internal const string DriveFsUserKey = @"Software\Google\DriveFS";
    internal const string DriveFsPolicyKey = @"SOFTWARE\Google\DriveFS";
    internal const string MyDriveFolderName = "My Drive";

    internal const string DriveFsDbRelative = @"Google\DriveFS\root_preference_sqlite.db";

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;
    private readonly IUserEnvironment _env;
    private readonly string _scratchFolder;

    /// <param name="scratchFolder">Where Drive's database is copied before reading; defaults to %LOCALAPPDATA%\InterfaceSync\Temp.</param>
    public DriveDetector(IFileSystem fs, IRegistry registry, IUserEnvironment env, string? scratchFolder = null)
    {
        _fs = fs;
        _registry = registry;
        _env = env;
        _scratchFolder = scratchFolder ?? PathUtil.Join(env.LocalAppData, ProductInfo.Id, "Temp");
    }

    public string DriveFsDbPath => PathUtil.Join(_env.LocalAppData, DriveFsDbRelative);

    public DriveDetectionResult Detect(string? userOverride)
    {
        var attempts = new List<string>();

        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            if (_fs.DirectoryExists(userOverride))
                return new(PathUtil.TrimEnd(userOverride), GuessMode(userOverride), DetectionSource.UserOverride, attempts);
            attempts.Add($"Your chosen Google Drive folder '{userOverride}' no longer exists.");
        }

        if (_env.Os == HostOs.MacOS) return DetectMac(attempts);

        foreach (var mount in MountPointsFromSettings(attempts))
        {
            var myDrive = MyDriveUnder(mount);
            if (myDrive != null)
            {
                attempts.Add($"Drive settings: found {myDrive}");
                return new(myDrive, GuessMode(myDrive), DetectionSource.DriveFsSettings, attempts);
            }
            attempts.Add($"Drive settings point at '{mount}', which has no '{MyDriveFolderName}' folder");
        }

        var databaseRoots = RootsFromDriveDatabase(attempts).ToList();

        // A folder Drive itself flags as My Drive (roots.is_my_drive) is used as it is, whatever its name: Drive
        // names it "My Drive (account)" when an older "My Drive" folder is still there (home PC, 2026-11-18 17:32).
        foreach (var root in _myDriveRoots)
            if (_fs.DirectoryExists(root))
            {
                attempts.Add($"Drive database: My Drive is {root}");
                return new(PathUtil.TrimEnd(root), GuessMode(root), DetectionSource.DriveFsSettings, attempts);
            }
        // Drive names its My Drive folder but it isn't there: don't guess another folder. A leftover "My Drive"
        // folder from an earlier Drive set-up is exactly the out-of-date copy that must never be synced with.
        if (_myDriveRoots.Count > 0)
        {
            attempts.Add($"Google Drive's own settings put My Drive at {string.Join(" or ", _myDriveRoots)}, which isn't on this computer. " +
                         "Not guessing another folder (an old one left behind would be out of date). Check that Google Drive is running and signed in.");
            return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts);
        }

        foreach (var root in databaseRoots)
        {
            var myDrive = MyDriveUnder(root);
            if (myDrive != null)
            {
                attempts.Add($"Drive database: found {myDrive}");
                return new(myDrive, GuessMode(myDrive), DetectionSource.DriveFsSettings, attempts);
            }
        }

        foreach (var drive in _fs.GetDrives())
        {
            if (!drive.IsReady || !drive.VolumeLabel.Equals("Google Drive", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = PathUtil.Join(drive.RootPath, MyDriveFolderName);
            if (_fs.DirectoryExists(candidate))
            {
                attempts.Add($"Drive letter {drive.RootPath} labelled 'Google Drive': found {candidate}");
                return new(candidate, DriveMode.Streaming, DetectionSource.Probe, attempts);
            }
        }

        var mirror = PathUtil.Join(_env.UserProfile, MyDriveFolderName);
        if (_fs.DirectoryExists(mirror))
        {
            attempts.Add($"Mirrored folder: found {mirror}");
            return new(mirror, DriveMode.Mirror, DetectionSource.Probe, attempts);
        }
        attempts.Add($"No '{MyDriveFolderName}' at {mirror}");

        attempts.Add("Google Drive was not found. Is Google Drive for desktop installed and signed in?");
        return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts);
    }

    /// <summary>
    /// macOS: Drive's own database (~/Library/Application Support/Google/DriveFS), then the File Provider
    /// folder ~/Library/CloudStorage/GoogleDrive-&lt;account&gt;/My Drive, then older locations.
    /// </summary>
    private DriveDetectionResult DetectMac(List<string> attempts)
    {
        var databaseRoots = RootsFromDriveDatabase(attempts).ToList();

        // A folder Drive itself flags as My Drive (roots.is_my_drive) is used as it is, whatever its name: Drive
        // names it "My Drive (account)" when an older "My Drive" folder is still there (home PC, 2026-11-18 17:32).
        foreach (var root in _myDriveRoots)
            if (_fs.DirectoryExists(root))
            {
                attempts.Add($"Drive database: My Drive is {root}");
                return new(PathUtil.TrimEnd(root), DriveMode.Unknown, DetectionSource.DriveFsSettings, attempts);
            }
        // Drive names its My Drive folder but it isn't there: don't guess another folder. A leftover "My Drive"
        // folder from an earlier Drive set-up is exactly the out-of-date copy that must never be synced with.
        if (_myDriveRoots.Count > 0)
        {
            attempts.Add($"Google Drive's own settings put My Drive at {string.Join(" or ", _myDriveRoots)}, which isn't on this computer. " +
                         "Not guessing another folder (an old one left behind would be out of date). Check that Google Drive is running and signed in.");
            return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts);
        }

        foreach (var root in databaseRoots)
        {
            var myDrive = MyDriveUnder(root);
            if (myDrive != null)
            {
                attempts.Add($"Drive database: found {myDrive}");
                return new(myDrive, DriveMode.Unknown, DetectionSource.DriveFsSettings, attempts);
            }
        }

        var cloudStorage = PathUtil.Join(_env.UserProfile, "Library", "CloudStorage");
        if (_fs.DirectoryExists(cloudStorage))
        {
            foreach (var account in _fs.EnumerateDirectories(cloudStorage)
                         .Where(d => PathUtil.Name(d).StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var myDrive = MyDriveUnder(account);
                if (myDrive != null)
                {
                    attempts.Add($"Google Drive folder: found {myDrive}");
                    return new(myDrive, DriveMode.Unknown, DetectionSource.Probe, attempts);
                }
                attempts.Add($"{account} has no '{MyDriveFolderName}' folder");
            }
        }
        else attempts.Add($"No {cloudStorage} folder");

        foreach (var legacy in new[] { "/Volumes/GoogleDrive", PathUtil.Join(_env.UserProfile, "Google Drive"), _env.UserProfile })
        {
            var myDrive = MyDriveUnder(legacy);
            if (myDrive != null)
            {
                attempts.Add($"Google Drive folder: found {myDrive}");
                return new(myDrive, DriveMode.Unknown, DetectionSource.Probe, attempts);
            }
        }
        attempts.Add("Google Drive was not found. Is Google Drive for desktop installed and signed in?");
        return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts);
    }

    /// <summary>
    /// Mount points from DriveFS's own settings. A value may be a drive letter ("G", "G:")
    /// or a folder path (mirror root or custom mount).
    /// </summary>
    private IEnumerable<string> MountPointsFromSettings(List<string> attempts)
    {
        var raw = new List<string>();

        var json = _registry.GetString(RegistryHive.CurrentUser, DriveFsUserKey, "PerAccountPreferences");
        if (!string.IsNullOrWhiteSpace(json))
        {
            var found = ExtractMountPoints(json, attempts).ToList();
            attempts.Add(found.Count == 0
                ? $"Drive settings in the registry (HKCU\\{DriveFsUserKey} PerAccountPreferences): no mount point recorded"
                : $"Drive settings in the registry (HKCU\\{DriveFsUserKey} PerAccountPreferences): mount point {string.Join(", ", found)}");
            raw.AddRange(found);
        }
        else
        {
            attempts.Add($"Drive settings in the registry: none (HKCU\\{DriveFsUserKey} has no PerAccountPreferences)");
        }

        foreach (var valueName in new[] { "DefaultMountPoint", "MountPoint" })
        {
            var v = _registry.GetString(RegistryHive.CurrentUser, DriveFsUserKey, valueName)
                    ?? _registry.GetString(RegistryHive.LocalMachine, DriveFsPolicyKey, valueName);
            if (!string.IsNullOrWhiteSpace(v)) raw.Add(v);
        }

        foreach (var value in raw.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return ToRootPath(value);
    }

    /// <summary>
    /// Folder paths Drive for desktop records in %LOCALAPPDATA%\Google\DriveFS\root_preference_sqlite.db
    /// (its mirrored "My Drive" root among them). Read from a copy, never the live file. Every step is
    /// reported in <paramref name="attempts"/> so the log shows why this source was used or skipped.
    /// </summary>
    private IEnumerable<string> RootsFromDriveDatabase(List<string> attempts)
    {
        _myDriveRoots = [];
        var source = DriveFsDbPath;
        if (!_fs.FileExists(source))
        {
            attempts.Add($"Drive database not found ({source})");
            return [];
        }

        IReadOnlyList<SqliteReader.Table> tables;
        var copy = PathUtil.Join(_scratchFolder, "root_preference.db.copy");
        try
        {
            _fs.CreateDirectory(_scratchFolder);
            _fs.CopyFile(source, copy);
            tables = new SqliteReader(_fs.ReadAllBytes(copy)).ReadTables();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
                                      or ArgumentException or IndexOutOfRangeException or InvalidCastException or OverflowException)
        {
            attempts.Add($"Drive database {source} could not be read ({e.GetType().Name}: {e.Message})");
            return [];
        }
        finally
        {
            try { if (_fs.FileExists(copy)) _fs.DeleteFile(copy); } catch (IOException) { }
        }

        var wal = source + "-wal";
        if (_fs.FileExists(wal) && _fs.GetFileInfo(wal).Size > 0)
            attempts.Add("Drive database has recent changes in its -wal file that may not be included yet");

        // Preferred: the roots table's "My Drive" rows (is_my_drive = 1), the current Drive account's first.
        var paths = MyDriveRoots(tables, attempts);
        _myDriveRoots = paths.Where(p => IsFullPath(p, _env.Os))
            .Select(p => PathUtil.ExpandUserVars(p, _env.UserProfile, _env.LocalAppData))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var t in tables)
        {
            var pathColumns = t.Columns.Select((c, i) => (c, i))
                .Where(x => x.c.Contains("path", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var row in t.Rows)
                foreach (var (_, i) in pathColumns)
                    if (i < row.Length && row[i] is string s && !string.IsNullOrWhiteSpace(s))
                        paths.Add(CleanPath(s));
        }
        if (paths.Count == 0)
        {
            // Unknown schema: fall back to any absolute Windows path stored anywhere.
            foreach (var t in tables)
                foreach (var row in t.Rows)
                    foreach (var v in row)
                        if (v is string s && CleanPath(s) is { Length: > 3 } c && IsFullPath(c, _env.Os))
                            paths.Add(c);
        }

        var described = string.Join("; ", tables.Select(t => $"{t.Name}({string.Join(", ", t.Columns)}) {t.Rows.Count} row(s)"));
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Only full paths count: a relative value (seen on a real PC: "Users\name\My Drive") would be
        // resolved against whatever folder InterfaceSync happened to start in.
        var relative = paths.Where(p => !IsFullPath(p, _env.Os)).ToList();
        if (relative.Count > 0)
            attempts.Add($"Drive database: ignored paths without a drive letter: {string.Join(", ", relative)}");
        paths = paths.Where(p => IsFullPath(p, _env.Os)).ToList();
        attempts.Add(paths.Count == 0
            ? $"Drive database {source}: no folder paths recorded. Tables: {described}"
            : $"Drive database {source}: folder paths {string.Join(", ", paths)}");
        if (paths.Count > 0 && !paths.Any(p => MyDriveUnder(PathUtil.ExpandUserVars(p, _env.UserProfile, _env.LocalAppData)) != null))
            attempts.Add($"Drive database: none of those is, or contains, an existing '{MyDriveFolderName}' folder");
        return paths.Select(p => PathUtil.ExpandUserVars(p, _env.UserProfile, _env.LocalAppData));
    }

    /// <summary>"C:\..." or "\\server\share\...", plus "/..." so tests on Linux can use real temp folders.</summary>
    internal static bool IsFullPath(string p, HostOs os) =>
        (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/'))
        || p.StartsWith(@"\\", StringComparison.Ordinal)
        || ((os == HostOs.MacOS || !OperatingSystem.IsWindows()) && p.StartsWith('/'));

    /// <summary>
    /// Rows flagged is_my_drive = 1, ordered so the account in CurrentAccountToken comes first.
    /// The token is read from DriveFS's registry key or a key/value row in the database; it is never logged.
    /// </summary>
    private List<string> MyDriveRoots(IReadOnlyList<SqliteReader.Table> tables, List<string> attempts)
    {
        var current = _registry.GetString(RegistryHive.CurrentUser, DriveFsUserKey, "CurrentAccountToken")
                      ?? CurrentAccountTokenFromTables(tables);
        var result = new List<(bool currentAccount, string path)>();
        foreach (var t in tables)
        {
            int flag = IndexOf(t.Columns, "is_my_drive");
            if (flag < 0) continue;
            int token = IndexOf(t.Columns, "account_token");
            var pathColumns = t.Columns.Select((c, i) => (c, i))
                .Where(x => x.c.Contains("path", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.c.Contains("absolute", StringComparison.OrdinalIgnoreCase) ? 0 : 1) // last_seen_absolute_path first
                .Select(x => x.i).ToList();
            foreach (var row in t.Rows)
            {
                if (flag >= row.Length || row[flag] is not long and not double || Convert.ToInt64(row[flag]) != 1) continue;
                bool isCurrent = current != null && token >= 0 && token < row.Length && row[token] as string == current;
                foreach (var i in pathColumns)
                    if (i < row.Length && row[i] is string s && !string.IsNullOrWhiteSpace(s))
                        result.Add((isCurrent, CleanPath(s)));
            }
        }
        if (result.Count == 0) return [];
        var ordered = result.OrderByDescending(r => r.currentAccount).Select(r => r.path).ToList();
        attempts.Add(current == null
            ? $"Drive database: My Drive root(s) from is_my_drive: {string.Join(", ", ordered.Distinct(StringComparer.OrdinalIgnoreCase))} (current account not recorded)"
            : $"Drive database: My Drive root(s) from is_my_drive: {string.Join(", ", ordered.Distinct(StringComparer.OrdinalIgnoreCase))}" +
              (result.Any(r => r.currentAccount) ? " (current account's first)" : " (none matched the current account)"));
        return ordered;
    }

    private static string? CurrentAccountTokenFromTables(IReadOnlyList<SqliteReader.Table> tables)
    {
        foreach (var t in tables)
        {
            if (t.Columns.Count < 2) continue;
            foreach (var row in t.Rows)
                for (int i = 0; i + 1 < row.Length; i++)
                    if (row[i] is string k && k.Equals("CurrentAccountToken", StringComparison.OrdinalIgnoreCase) && row[i + 1] is string v)
                        return v;
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string CleanPath(string s)
    {
        var p = s.Trim().Trim('"');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p.Length > 0 ? PathUtil.TrimEnd(p) : p;
    }

    /// <summary>Pulls every "mount_point_path" string out of DriveFS's PerAccountPreferences JSON.</summary>
    internal static IEnumerable<string> ExtractMountPoints(string json, List<string>? attempts = null)
    {
        var found = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, found);
        }
        catch (JsonException)
        {
            attempts?.Add("Drive settings in the registry could not be read (unexpected format)");
        }
        return found;

        static void Walk(JsonElement e, List<string> found)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name.Equals("mount_point_path", StringComparison.OrdinalIgnoreCase) &&
                            p.Value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(p.Value.GetString()))
                            found.Add(p.Value.GetString()!);
                        else
                            Walk(p.Value, found);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, found);
                    break;
            }
        }
    }

    private string ToRootPath(string value)
    {
        var v = PathUtil.ExpandUserVars(value.Trim().Trim('"'), _env.UserProfile, _env.LocalAppData);
        if (v.Length == 1 && char.IsLetter(v[0])) return v.ToUpperInvariant() + @":\";
        if (v.Length == 2 && v[1] == ':') return v.ToUpperInvariant() + @"\";
        return PathUtil.TrimEnd(v);
    }

    /// <summary>The mount may itself be "My Drive", or contain it.</summary>
    /// <summary>Folders Drive's database flags as My Drive (is_my_drive = 1), full paths only; filled by RootsFromDriveDatabase.</summary>
    private List<string> _myDriveRoots = [];

    private string? MyDriveUnder(string root)
    {
        if (PathUtil.Name(root).Equals(MyDriveFolderName, StringComparison.OrdinalIgnoreCase) && _fs.DirectoryExists(root))
            return PathUtil.TrimEnd(root);
        var child = PathUtil.Join(root, MyDriveFolderName);
        return _fs.DirectoryExists(child) ? child : null;
    }

    private DriveMode GuessMode(string myDrivePath)
    {
        foreach (var drive in _fs.GetDrives())
        {
            if (drive.VolumeLabel.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) &&
                PathUtil.IsUnder(myDrivePath, drive.RootPath))
                return DriveMode.Streaming;
        }
        return DriveMode.Mirror;
    }
}
