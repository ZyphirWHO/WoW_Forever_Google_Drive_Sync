using System.Text.Json;
using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Detection;

public enum DriveMode { Unknown, Streaming, Mirror }

public sealed record DriveDetectionResult(string? MyDrivePath, DriveMode Mode, DetectionSource Source, IReadOnlyList<string> Attempts)
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

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;
    private readonly IUserEnvironment _env;

    public DriveDetector(IFileSystem fs, IRegistry registry, IUserEnvironment env)
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
                return new(PathUtil.TrimEnd(userOverride), GuessMode(userOverride), DetectionSource.UserOverride, attempts);
            attempts.Add($"Your chosen Drive folder '{userOverride}' no longer exists.");
        }

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
    /// Mount points from DriveFS's own settings. A value may be a drive letter ("G", "G:")
    /// or a folder path (mirror root or custom mount).
    /// </summary>
    private IEnumerable<string> MountPointsFromSettings(List<string> attempts)
    {
        var raw = new List<string>();

        var json = _registry.GetString(RegistryHive.CurrentUser, DriveFsUserKey, "PerAccountPreferences");
        if (!string.IsNullOrWhiteSpace(json))
            raw.AddRange(ExtractMountPoints(json, attempts));

        foreach (var valueName in new[] { "DefaultMountPoint", "MountPoint" })
        {
            var v = _registry.GetString(RegistryHive.CurrentUser, DriveFsUserKey, valueName)
                    ?? _registry.GetString(RegistryHive.LocalMachine, DriveFsPolicyKey, valueName);
            if (!string.IsNullOrWhiteSpace(v)) raw.Add(v);
        }

        foreach (var value in raw.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return ToRootPath(value);
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
