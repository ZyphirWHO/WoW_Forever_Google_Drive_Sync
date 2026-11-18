using System.Text.Json;
using System.Text.Json.Serialization;
using WoWSync.Core.Abstractions;

namespace WoWSync.Core.Settings;

public enum AddonMode { Off /*, CopyWhenClosed — planned for 2.1 */ }

/// <summary>%LOCALAPPDATA%\WoWSync\settings.json: user overrides and choices. Detected paths are re-detected every start.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>User-chosen WoW install folder; null = auto-detect.</summary>
    public string? WowInstallOverride { get; set; }

    /// <summary>User-chosen "My Drive" folder; null = auto-detect.</summary>
    public string? DriveOverride { get; set; }

    /// <summary>Game version folder name (e.g. "_retail_") → sync on/off.</summary>
    public Dictionary<string, bool> EnabledVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional mapping game version folder → cloud folder name, for data written by v1.1.</summary>
    public Dictionary<string, string> CloudFolderNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Paused { get; set; }
    public bool SetupComplete { get; set; }
    public AddonMode AddonMode { get; set; } = AddonMode.Off;
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Last detected paths, for diagnostics and for noticing that Drive moved.</summary>
    public string? LastDetectedWowInstall { get; set; }
    public string? LastDetectedDrive { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class SettingsStore
{
    private readonly IFileSystem _fs;
    public string FilePath { get; }

    public SettingsStore(IFileSystem fs, string dataFolder)
    {
        _fs = fs;
        FilePath = PathUtil.Join(dataFolder, "settings.json");
    }

    public AppSettings Load()
    {
        if (!_fs.FileExists(FilePath)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(_fs.ReadAllText(FilePath), Json.Options) ?? new AppSettings();
            // Dictionaries come back case-sensitive; restore comparer.
            s.EnabledVersions = new(s.EnabledVersions, StringComparer.OrdinalIgnoreCase);
            s.CloudFolderNames = new(s.CloudFolderNames, StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch (JsonException)
        {
            // Keep the unreadable file for support, start fresh.
            _fs.CopyFile(FilePath, FilePath + ".unreadable");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) =>
        _fs.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json.Options));
}
