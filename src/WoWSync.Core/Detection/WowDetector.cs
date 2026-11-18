using System.Text.RegularExpressions;
using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Detection;

public enum DetectionSource { UserOverride, BattleNetProductDb, RegistryHint, DriveFsSettings, CommonLocation, Probe, NotFound }

/// <summary>What was found, how, and what was tried on the way (shown in the setup window and diagnostics).</summary>
public sealed record DetectionResult(string? Path, DetectionSource Source, IReadOnlyList<string> Attempts)
{
    public bool Found => Path != null;
}

/// <summary>One World of Warcraft install folder, and what Battle.net says is installed in it.</summary>
public sealed record WowInstall(string Root, DetectionSource Source, IReadOnlyList<BattleNetProduct> Products);

/// <summary>All WoW install folders found (Battle.net can put game versions in different folders).</summary>
public sealed record WowInstallDetection(IReadOnlyList<WowInstall> Installs, DetectionSource Source, IReadOnlyList<string> Attempts)
{
    public bool Found => Installs.Count > 0;
    public string? PrimaryRoot => Installs.Count > 0 ? Installs[0].Root : null;
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

    /// <summary>Battle.net product uid (e.g. "wowf") and client version, when product.db lists this folder.</summary>
    public string? ProductUid { get; init; }
    public string? ClientVersion { get; init; }
}

/// <summary>
/// Finds the World of Warcraft install folder(s) and their game versions.
/// Order: user override → Battle.net product.db → Blizzard registry key (hint only) →
/// common folders on every fixed drive → ask the user. Every candidate must pass the folder scan.
/// The Windows uninstall list is not used: verified to have no WoW entry on a real install.
/// </summary>
public sealed class WowDetector
{
    internal const string BlizzardKeyWow64 = @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft";
    internal const string ProductDbRelative = @"Battle.net\Agent\product.db";
    private const int ProductDbReadAttempts = 3;

    private static readonly Regex VersionFolderPattern = new(@"^_[A-Za-z0-9_]+_$", RegexOptions.CultureInvariant);

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;
    private readonly IUserEnvironment _env;
    private readonly string _scratchFolder;

    /// <param name="scratchFolder">Where product.db is copied before reading (e.g. %LOCALAPPDATA%\WoWSync\Temp).</param>
    public WowDetector(IFileSystem fs, IRegistry registry, IUserEnvironment env, string scratchFolder)
    {
        _fs = fs;
        _registry = registry;
        _env = env;
        _scratchFolder = scratchFolder;
    }

    public string ProductDbPath => PathUtil.Join(_env.ProgramData, ProductDbRelative);

    public WowInstallDetection DetectInstalls(string? userOverride)
    {
        var attempts = new List<string>();

        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            var root = ToInstallRoot(userOverride);
            if (IsInstallRoot(root))
                return Single(root, DetectionSource.UserOverride, attempts);
            attempts.Add($"Your chosen folder '{userOverride}' no longer contains a WoW game version.");
        }

        var fromBattleNet = FromProductDb(attempts);
        if (fromBattleNet.Count > 0)
            return new WowInstallDetection(fromBattleNet, DetectionSource.BattleNetProductDb, attempts);

        var hint = _registry.GetString(RegistryHive.LocalMachine, BlizzardKeyWow64, "InstallPath");
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var root = ToInstallRoot(hint.Trim().Trim('"'));
            if (IsInstallRoot(root))
            {
                attempts.Add($"Blizzard registry hint: found {root}");
                return Single(root, DetectionSource.RegistryHint, attempts);
            }
            attempts.Add($"Blizzard registry hint '{hint}' has no game versions");
        }

        foreach (var candidate in CommonLocations().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsInstallRoot(candidate))
            {
                attempts.Add($"Common location: found {candidate}");
                return Single(candidate, DetectionSource.CommonLocation, attempts);
            }
        }
        attempts.Add("No World of Warcraft folder in the usual places on any fixed drive.");

        attempts.Add("World of Warcraft was not found automatically. Choose its folder.");
        return new WowInstallDetection([], DetectionSource.NotFound, attempts);
    }

    /// <summary>Single-folder view for callers that only need the main install.</summary>
    public DetectionResult DetectInstall(string? userOverride)
    {
        var d = DetectInstalls(userOverride);
        return new DetectionResult(d.PrimaryRoot, d.Source, d.Attempts);
    }

    private static WowInstallDetection Single(string root, DetectionSource source, List<string> attempts) =>
        new([new WowInstall(root, source, [])], source, attempts);

    /// <summary>WoW products from Battle.net's product.db, grouped by install folder.</summary>
    private List<WowInstall> FromProductDb(List<string> attempts)
    {
        var products = ReadProductDb(attempts);
        if (products == null) return [];

        var wow = products.Where(p => p.IsWow).ToList();
        foreach (var skipped in products.Where(p => !p.IsWow))
            attempts.Add($"Battle.net product '{skipped.Uid}' skipped (not WoW)");
        if (wow.Count == 0)
        {
            attempts.Add("Battle.net lists no World of Warcraft products.");
            return [];
        }

        var installs = new List<WowInstall>();
        foreach (var group in wow.Where(p => p.InstallPath != null)
                     .GroupBy(p => PathUtil.Normalize(p.InstallPath!)))
        {
            var root = ToInstallRoot(group.First().InstallPath!);
            var names = string.Join(", ", group.Select(p => p.GameFolder ?? p.Uid));
            if (IsInstallRoot(root))
            {
                attempts.Add($"Battle.net: {root} ({names})");
                installs.Add(new WowInstall(root, DetectionSource.BattleNetProductDb, group.ToList()));
            }
            else
            {
                attempts.Add($"Battle.net lists {root} ({names}), but no game version was found there.");
            }
        }
        foreach (var p in wow.Where(p => p.InstallPath == null))
            attempts.Add($"Battle.net product '{p.Uid}' has no install folder recorded.");
        return installs;
    }

    /// <summary>
    /// Copies product.db and reads the copy, never the live file Battle.net is rewriting.
    /// Retries when the copy is mid-write (unreadable), then gives up and falls back.
    /// </summary>
    private IReadOnlyList<BattleNetProduct>? ReadProductDb(List<string> attempts)
    {
        var source = ProductDbPath;
        if (!_fs.FileExists(source))
        {
            attempts.Add($"Battle.net install list not found ({source}).");
            return null;
        }

        var copy = PathUtil.Join(_scratchFolder, "product.db.copy");
        string? lastError = null;
        for (int attempt = 1; attempt <= ProductDbReadAttempts; attempt++)
        {
            try
            {
                _fs.CreateDirectory(_scratchFolder);
                _fs.CopyFile(source, copy);
                var bytes = _fs.ReadAllBytes(copy);
                return ProductDb.Parse(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                lastError = e.Message;
                if (attempt < ProductDbReadAttempts) Thread.Sleep(200);
            }
            finally
            {
                try { if (_fs.FileExists(copy)) _fs.DeleteFile(copy); } catch (IOException) { }
            }
        }
        attempts.Add($"Battle.net install list could not be read ({lastError}).");
        return null;
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

    /// <summary>Some sources record the _retail_ folder itself; step up to the install root.</summary>
    private static string ToInstallRoot(string path)
    {
        var trimmed = PathUtil.TrimEnd(path.Replace('/', '\\'));
        return VersionFolderPattern.IsMatch(PathUtil.Name(trimmed)) && PathUtil.Parent(trimmed) is { } parent
            ? parent
            : trimmed;
    }

    private bool IsInstallRoot(string path) => _fs.DirectoryExists(path) && ScanVersions(path).Count > 0;

    /// <summary>Game versions across every detected install, tagged with Battle.net's product info where known.</summary>
    public IReadOnlyList<GameVersion> ScanVersions(WowInstallDetection detection)
    {
        var result = new List<GameVersion>();
        foreach (var install in detection.Installs)
        {
            foreach (var v in ScanVersions(install.Root))
            {
                var product = install.Products.FirstOrDefault(p =>
                    string.Equals(p.GameFolder, v.FolderName, StringComparison.OrdinalIgnoreCase));
                result.Add(product == null ? v : v with { ProductUid = product.Uid, ClientVersion = product.Version });
            }
        }
        return result;
    }

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
