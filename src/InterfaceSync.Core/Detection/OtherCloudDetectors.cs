using System.Text.Json;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Detection;

/// <summary>
/// Finds the OneDrive folder: OneDrive's own record of it (environment variables it sets for the user,
/// then its per-account registry keys), then the usual folders. Personal accounts are preferred over
/// work/school ones; every account found is listed in the attempts.
/// </summary>
public sealed class OneDriveDetector
{
    internal const string AccountsKey = @"Software\Microsoft\OneDrive\Accounts";

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;
    private readonly IUserEnvironment _env;

    public OneDriveDetector(IFileSystem fs, IRegistry registry, IUserEnvironment env)
    {
        _fs = fs;
        _registry = registry;
        _env = env;
    }

    public DriveDetectionResult Detect(string? userOverride)
    {
        var attempts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            if (_fs.DirectoryExists(userOverride))
                return Result(PathUtil.TrimEnd(userOverride), DetectionSource.UserOverride, attempts);
            attempts.Add($"Your chosen OneDrive folder '{userOverride}' no longer exists.");
        }

        if (_env.Os == HostOs.MacOS) return DetectMac(attempts);

        // 1. Environment variables OneDrive sets for the signed-in user.
        foreach (var name in new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" })
        {
            var value = _env.Variable(name);
            if (string.IsNullOrWhiteSpace(value)) { attempts.Add($"OneDrive: environment variable %{name}% not set"); continue; }
            if (_fs.DirectoryExists(value))
            {
                attempts.Add($"OneDrive: %{name}% = {value}");
                return Result(PathUtil.TrimEnd(value), DetectionSource.DriveFsSettings, attempts);
            }
            attempts.Add($"OneDrive: %{name}% = {value}, which doesn't exist");
        }

        // 2. OneDrive's per-account registry keys (Personal first, then Business1, Business2...).
        var accounts = _registry.GetSubKeyNames(RegistryHive.CurrentUser, AccountsKey)
            .OrderBy(a => a.Equals("Personal", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0) attempts.Add($"OneDrive: no accounts in the registry (HKCU\\{AccountsKey})");
        foreach (var account in accounts)
        {
            var folder = _registry.GetString(RegistryHive.CurrentUser, $@"{AccountsKey}\{account}", "UserFolder");
            if (string.IsNullOrWhiteSpace(folder)) { attempts.Add($"OneDrive account '{account}': no folder recorded"); continue; }
            if (_fs.DirectoryExists(folder))
            {
                attempts.Add($"OneDrive account '{account}': found {folder}");
                return Result(PathUtil.TrimEnd(folder), DetectionSource.DriveFsSettings, attempts);
            }
            attempts.Add($"OneDrive account '{account}': {folder} doesn't exist");
        }

        // 3. The usual folders: %USERPROFILE%\OneDrive, then "OneDrive - <organisation>".
        var plain = PathUtil.Join(_env.UserProfile, "OneDrive");
        if (_fs.DirectoryExists(plain))
        {
            attempts.Add($"OneDrive folder: found {plain}");
            return Result(plain, DetectionSource.Probe, attempts);
        }
        if (_fs.DirectoryExists(_env.UserProfile))
        {
            var work = _fs.EnumerateDirectories(_env.UserProfile)
                .Where(d => PathUtil.Name(d).StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (work != null)
            {
                attempts.Add($"OneDrive folder: found {work}");
                return Result(work, DetectionSource.Probe, attempts);
            }
        }
        attempts.Add($"No OneDrive folder in {_env.UserProfile}");
        attempts.Add("OneDrive was not found. Is OneDrive installed and signed in?");
        return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts, CloudProvider.OneDrive);
    }

    /// <summary>macOS: ~/Library/CloudStorage/OneDrive-Personal first, then OneDrive-&lt;org&gt;, then the older ~/OneDrive.</summary>
    private DriveDetectionResult DetectMac(List<string> attempts)
    {
        var cloudStorage = PathUtil.Join(_env.UserProfile, "Library", "CloudStorage");
        if (_fs.DirectoryExists(cloudStorage))
        {
            var found = _fs.EnumerateDirectories(cloudStorage)
                .Where(d => PathUtil.Name(d).StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => PathUtil.Name(d).Equals("OneDrive-Personal", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (found != null)
            {
                attempts.Add($"OneDrive folder: found {found}");
                return Result(found, DetectionSource.Probe, attempts);
            }
        }
        attempts.Add($"No OneDrive folder in {cloudStorage}");
        var legacy = PathUtil.Join(_env.UserProfile, "OneDrive");
        if (_fs.DirectoryExists(legacy))
        {
            attempts.Add($"OneDrive folder: found {legacy}");
            return Result(legacy, DetectionSource.Probe, attempts);
        }
        attempts.Add("OneDrive was not found. Is OneDrive installed and signed in?");
        return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts, CloudProvider.OneDrive);
    }

    // OneDrive's Files On-Demand can make files online-only, like Google Drive streaming; files are
    // checked one by one before every pull, so the mode is reported as unknown rather than guessed.
    private static DriveDetectionResult Result(string path, DetectionSource source, List<string> attempts) =>
        new(path, DriveMode.Unknown, source, attempts, CloudProvider.OneDrive);
}

/// <summary>
/// Finds the Dropbox folder: Dropbox's own info.json (personal, then business), then the usual folders.
/// </summary>
public sealed class DropboxDetector
{
    private readonly IFileSystem _fs;
    private readonly IUserEnvironment _env;

    public DropboxDetector(IFileSystem fs, IUserEnvironment env)
    {
        _fs = fs;
        _env = env;
    }

    public DriveDetectionResult Detect(string? userOverride)
    {
        var attempts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            if (_fs.DirectoryExists(userOverride))
                return Result(PathUtil.TrimEnd(userOverride), DetectionSource.UserOverride, attempts);
            attempts.Add($"Your chosen Dropbox folder '{userOverride}' no longer exists.");
        }

        // Windows: %APPDATA% / %LOCALAPPDATA%\Dropbox\info.json. macOS: ~/.dropbox/info.json.
        var infoFiles = _env.Os == HostOs.MacOS
            ? new[] { PathUtil.Join(_env.UserProfile, ".dropbox", "info.json") }
            : new[] { PathUtil.Join(_env.RoamingAppData, "Dropbox", "info.json"), PathUtil.Join(_env.LocalAppData, "Dropbox", "info.json") };
        foreach (var info in infoFiles)
        {
            if (!_fs.FileExists(info)) { attempts.Add($"Dropbox settings not found ({info})"); continue; }
            List<(string account, string path)> paths;
            try { paths = ParseInfoJson(_fs.ReadAllText(info)); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                attempts.Add($"Dropbox settings {info} could not be read ({e.GetType().Name}: {e.Message})");
                continue;
            }
            if (paths.Count == 0) attempts.Add($"Dropbox settings {info}: no folder recorded");
            foreach (var (account, path) in paths)
            {
                if (_fs.DirectoryExists(path))
                {
                    attempts.Add($"Dropbox settings ({account}): found {path}");
                    return Result(PathUtil.TrimEnd(path), DetectionSource.DriveFsSettings, attempts);
                }
                attempts.Add($"Dropbox settings ({account}): {path} doesn't exist");
            }
        }

        if (_env.Os == HostOs.MacOS)
        {
            var cloudStorage = PathUtil.Join(_env.UserProfile, "Library", "CloudStorage");
            if (_fs.DirectoryExists(cloudStorage) && _fs.EnumerateDirectories(cloudStorage)
                    .Where(d => PathUtil.Name(d).StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } fp)
            {
                attempts.Add($"Dropbox folder: found {fp}");
                return Result(fp, DetectionSource.Probe, attempts);
            }
        }
        foreach (var name in new[] { "Dropbox", "Dropbox (Personal)" })
        {
            var candidate = PathUtil.Join(_env.UserProfile, name);
            if (_fs.DirectoryExists(candidate))
            {
                attempts.Add($"Dropbox folder: found {candidate}");
                return Result(candidate, DetectionSource.Probe, attempts);
            }
        }
        attempts.Add($"No Dropbox folder in {_env.UserProfile}");
        attempts.Add("Dropbox was not found. Is the Dropbox app installed and signed in?");
        return new(null, DriveMode.Unknown, DetectionSource.NotFound, attempts, CloudProvider.Dropbox);
    }

    /// <summary>info.json: {"personal": {"path": "..."}, "business": {"path": "..."}}. Personal first.</summary>
    internal static List<(string account, string path)> ParseInfoJson(string json)
    {
        var result = new List<(string, string)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        var accounts = doc.RootElement.EnumerateObject()
            .OrderBy(p => p.Name.Equals("personal", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        foreach (var account in accounts)
        {
            if (account.Value.ValueKind == JsonValueKind.Object &&
                account.Value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(path.GetString()))
                result.Add((account.Name, path.GetString()!));
        }
        return result;
    }

    private static DriveDetectionResult Result(string path, DetectionSource source, List<string> attempts) =>
        new(path, DriveMode.Unknown, source, attempts, CloudProvider.Dropbox);
}

/// <summary>One entry point for whichever cloud app the player chose.</summary>
public static class CloudDetector
{
    public static DriveDetectionResult Detect(CloudProvider provider, IFileSystem fs, IRegistry registry, IUserEnvironment env,
        string scratchFolder, string? userOverride) =>
        CloudProviderCatalog.Create(provider, fs, registry, env, scratchFolder).Detect(userOverride);
}
