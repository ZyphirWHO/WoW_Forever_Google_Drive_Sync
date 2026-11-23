using System.Text.Json;
using System.Text.Json.Serialization;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Settings;

/// <summary>What the player chose in setup for a version whose cloud copy already has settings.</summary>
public enum FirstSyncChoice
{
    /// <summary>The safe rules: the cloud's settings win, unless this computer's are newer, then ask.</summary>
    Automatic,
    /// <summary>Download the settings already in the cloud (this computer's are backed up first).</summary>
    UseCloud,
    /// <summary>Upload this computer's settings, replacing the cloud's (the cloud's are backed up first).</summary>
    UseThisComputer,
}

/// <summary>Window colours: follow the computer's light/dark setting, or always one of them.</summary>
public enum AppTheme { System, Light, Dark }

public enum AddonMode { Off /*, CopyWhenClosed — planned for 2.1 */ }

/// <summary>%LOCALAPPDATA%\InterfaceSync\settings.json: user overrides and choices. Detected paths are re-detected every start.</summary>
public sealed class AppSettings
{
    /// <summary>2: dark theme by default (3.0). Older files are upgraded on load.</summary>
    public int SchemaVersion { get; set; } = CurrentSchema;
    public const int CurrentSchema = 2;

    /// <summary>User-chosen WoW install folder; null = auto-detect.</summary>
    public string? WowInstallOverride { get; set; }

    /// <summary>Which cloud app's folder InterfaceSync uses. Null = not chosen yet (Google Drive for settings from earlier builds).</summary>
    public Detection.CloudProvider? CloudProvider { get; set; }

    /// <summary>User-chosen folder for the selected cloud app ("My Drive", OneDrive or Dropbox folder); null = auto-detect.</summary>
    public string? DriveOverride { get; set; }

    /// <summary>Game version folder name (e.g. "_retail_") → sync on/off.</summary>
    public Dictionary<string, bool> EnabledVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Setup's answer per version for its first sync; removed once that sync is done.</summary>
    public Dictionary<string, FirstSyncChoice> FirstSyncChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional mapping game version folder → cloud folder name, for data written by v1.1.</summary>
    public Dictionary<string, string> CloudFolderNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Paused { get; set; }
    /// <summary>Dark unless the player switched to light (the sun/moon button).</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Look for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public bool SetupComplete { get; set; }
    public AddonMode AddonMode { get; set; } = AddonMode.Off;
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Last detected paths, for diagnostics and for noticing that Drive moved.</summary>
    public string? LastDetectedWowInstall { get; set; }
    public string? LastDetectedDrive { get; set; }

    /// <summary>Duplicate cloud folders the player has looked at and hidden ("Hide" in the status window).</summary>
    public List<string> HiddenDuplicates { get; set; } = [];
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
            s.FirstSyncChoices = new(s.FirstSyncChoices ?? [], StringComparer.OrdinalIgnoreCase);
            if (s.SchemaVersion < 2)
            {
                // 3.0 starts in dark mode; earlier builds defaulted to "match the computer".
                if (s.Theme == AppTheme.System) s.Theme = AppTheme.Dark;
                s.SchemaVersion = 2;
            }
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
