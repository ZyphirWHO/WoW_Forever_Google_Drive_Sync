using System.Text.RegularExpressions;
using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Detection;

public enum DetectionSource { UserOverride, Registry, UninstallEntry, DriveFsSettings, CommonLocation, Probe, NotFound }

/// <summary>What was found, how, and what was tried on the way (shown in the setup window and diagnostics).</summary>
public sealed record DetectionResult(string? Path, DetectionSource Source, IReadOnlyList<string> Attempts)
{
    public bool Found => Path != null;
}

/// <summary>One playable game version inside the WoW install, e.g. _retail_ or _forever_.</summary>
public sealed record GameVersion(string FolderName, string FolderPath, IReadOnlyList<string> Executables, bool HasAccountFolder)
{
    /// <summary>Friendly name: "_classic_era_" → "classic_era".</summary>
    public string Key => FolderName.Trim('_');

    public string AccountPath => PathUtil.Join(FolderPath, "WTF", "Account");
    public string AddOnsPath => PathUtil.Join(FolderPath, "Interface", "AddOns");

    /// <summary>Only versions that have been launched once (WTF\Account exists) can be enabled.</summary>
    public bool CanEnable => HasAccountFolder;
}

/// <summary>Finds the World of Warcraft install folder and its game versions.</summary>
public sealed class WowDetector
{
    // VERIFY on the real PCs: exact keys Blizzard writes.
    internal const string BlizzardKeyWow64 = @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft";
    internal const string BlizzardKey = @"SOFTWARE\Blizzard Entertainment\World of Warcraft";
    internal const string UninstallKeyWow64 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly Regex VersionFolderPattern = new(@"^_[A-Za-z0-9_]+_$", RegexOptions.CultureInvariant);

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;

    public WowDetector(IFileSystem fs, IRegistry registry)
    {
        _fs = fs;
        _registry = registry;
    }

    public DetectionResult DetectInstall(string? userOverride)
    {
        var attempts = new List<string>();

        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            var root = ToInstallRoot(userOverride);
            if (IsInstallRoot(root)) return new(root, DetectionSource.UserOverride, attempts);
            attempts.Add($"Your chosen folder '{userOverride}' no longer contains a WoW game version.");
        }

        foreach (var key in new[] { BlizzardKeyWow64, BlizzardKey })
        {
            var value = _registry.GetString(RegistryHive.LocalMachine, key, "InstallPath");
            if (TryCandidate(value, $"Registry HKLM\\{key} InstallPath", attempts, out var found))
                return new(found, DetectionSource.Registry, attempts);
        }

        foreach (var key in new[] { UninstallKeyWow64, UninstallKey })
        {
            foreach (var sub in _registry.GetSubKeyNames(RegistryHive.LocalMachine, key))
            {
                var subPath = key + "\\" + sub;
                var displayName = _registry.GetString(RegistryHive.LocalMachine, subPath, "DisplayName");
                bool looksLikeWow = sub.Equals("World of Warcraft", StringComparison.OrdinalIgnoreCase)
                    || (displayName?.StartsWith("World of Warcraft", StringComparison.OrdinalIgnoreCase) ?? false);
                if (!looksLikeWow) continue;
                var value = _registry.GetString(RegistryHive.LocalMachine, subPath, "InstallLocation");
                if (TryCandidate(value, $"Uninstall entry '{sub}' InstallLocation", attempts, out var found))
                    return new(found, DetectionSource.UninstallEntry, attempts);
            }
        }

        foreach (var candidate in CommonLocations())
        {
            if (TryCandidate(candidate, "Common location", attempts, out var found))
                return new(found, DetectionSource.CommonLocation, attempts);
        }

        attempts.Add("World of Warcraft was not found automatically. Choose its folder.");
        return new(null, DetectionSource.NotFound, attempts);
    }

    private IEnumerable<string> CommonLocations()
    {
        yield return @"C:\Program Files (x86)\World of Warcraft";
        yield return @"C:\Program Files\World of Warcraft";
        foreach (var drive in _fs.GetDrives())
        {
            if (drive.Kind != DriveKind.Fixed || !drive.IsReady) continue;
            yield return PathUtil.Join(drive.RootPath, "World of Warcraft");
            yield return PathUtil.Join(drive.RootPath, "Games", "World of Warcraft");
            yield return PathUtil.Join(drive.RootPath, "Program Files (x86)", "World of Warcraft");
            yield return PathUtil.Join(drive.RootPath, "Program Files", "World of Warcraft");
        }
    }

    private bool TryCandidate(string? raw, string label, List<string> attempts, out string found)
    {
        found = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var root = ToInstallRoot(raw.Trim().Trim('"'));
        if (IsInstallRoot(root))
        {
            found = root;
            attempts.Add($"{label}: found {root}");
            return true;
        }
        attempts.Add($"{label}: '{raw}' has no game versions");
        return false;
    }

    /// <summary>Blizzard sometimes records the _retail_ folder itself; step up to the install root.</summary>
    private static string ToInstallRoot(string path)
    {
        var trimmed = PathUtil.TrimEnd(path);
        return VersionFolderPattern.IsMatch(PathUtil.Name(trimmed)) && PathUtil.Parent(trimmed) is { } parent
            ? parent
            : trimmed;
    }

    private bool IsInstallRoot(string path) => _fs.DirectoryExists(path) && ScanVersions(path).Count > 0;

    /// <summary>
    /// Every _name_ subfolder containing a Wow*.exe and a WTF folder or .flavor.info.
    /// New versions Blizzard adds are picked up without a code change.
    /// </summary>
    public IReadOnlyList<GameVersion> ScanVersions(string installRoot)
    {
        var result = new List<GameVersion>();
        if (!_fs.DirectoryExists(installRoot)) return result;

        IEnumerable<string> dirs;
        try { dirs = _fs.EnumerateDirectories(installRoot).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }

        foreach (var dir in dirs)
        {
            var name = PathUtil.Name(dir);
            if (!VersionFolderPattern.IsMatch(name) || _fs.IsReparsePoint(dir)) continue;

            List<string> exes;
            try
            {
                exes = _fs.EnumerateFiles(dir)
                    .Select(PathUtil.Name)
                    .Where(n => n.StartsWith("wow", StringComparison.OrdinalIgnoreCase)
                                && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            bool hasWtf = _fs.DirectoryExists(PathUtil.Join(dir, "WTF"));
            bool hasFlavor = _fs.FileExists(PathUtil.Join(dir, ".flavor.info"));
            if (exes.Count == 0 || !(hasWtf || hasFlavor)) continue;

            var account = PathUtil.Join(dir, "WTF", "Account");
            bool hasAccount = _fs.DirectoryExists(account) && !_fs.IsReparsePoint(account);
            result.Add(new GameVersion(name, dir, exes, hasAccount));
        }

        return result.OrderBy(v => v.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
