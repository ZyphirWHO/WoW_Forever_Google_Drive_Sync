using System.Globalization;
using System.Text.Json;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Localization;
using InterfaceSync.Core.Settings;

namespace InterfaceSync.Core.Sync;

/// <summary>
/// What a settings file under WTF\Account is, in the player's words. Paths look like
/// "ACCOUNT\SavedVariables\Details.lua" (account-wide) or "ACCOUNT\Realm\Character\SavedVariables\WeakAuras.lua".
/// </summary>
public sealed record SettingsItem(string RelativePath, string Account, string? Realm, string? Character, string? Addon, string Area)
{
    /// <summary>"Thrall - Silvermoon", or null for account-wide files.</summary>
    public string? CharacterLabel => Character == null ? null : Realm == null ? Character : $"{Character} - {Realm}";

    /// <summary>Key used for "choose what syncs": realm\character (lower case does not matter).</summary>
    public string? CharacterKey => Character == null ? null : $"{Realm}/{Character}";

    public static SettingsItem Parse(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var file = parts.Length > 0 ? parts[^1] : relativePath;
        var account = parts.Length > 1 ? parts[0] : "";
        int svIndex = Array.FindIndex(parts, p => p.Equals("SavedVariables", StringComparison.OrdinalIgnoreCase));
        int folderDepth = svIndex >= 0 ? svIndex : parts.Length - 1;   // folders before SavedVariables or the file
        string? realm = folderDepth >= 3 ? parts[1] : null;
        string? character = folderDepth >= 3 ? parts[2] : null;

        string? addon = null;
        string area;
        if (svIndex >= 0 && svIndex == parts.Length - 2)
        {
            var name = StripBackup(file);
            if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)) area = "Game interface";
            else
            {
                addon = name;
                area = name;
            }
        }
        else area = AreaFor(StripBackup(file));
        return new SettingsItem(relativePath, account, realm, character, addon, area);
    }

    private static string StripBackup(string name)
    {
        foreach (var suffix in new[] { ".bak", ".old" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        return name;
    }

    private static string AreaFor(string file) => file.ToLowerInvariant() switch
    {
        "bindings-cache.wtf" => "Key bindings",
        "macros-cache.txt" => "Macros",
        "addons.txt" => "Enabled addons",
        "layout-local.txt" => "Window layout",
        "chat-cache.txt" => "Chat windows",
        "config-cache.wtf" => "Game options",
        var f when f.StartsWith("edit-mode-cache", StringComparison.Ordinal) => "Edit Mode layouts",
        var f when f.StartsWith("cliconfig", StringComparison.Ordinal) => "Game options",
        _ => "Other settings",
    };

    /// <summary>"Details, WeakAuras, Key bindings and 3 more" for a list of changed files.</summary>
    public static string Summarize(IEnumerable<string> relativePaths, int show = 3)
    {
        var areas = relativePaths.Select(p => L.T(Parse(p).Area))
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key).ToList();
        if (areas.Count == 0) return L.T("no files");
        if (areas.Count <= show) return JoinAnd(areas);
        return L.F("{0} and {1} more", string.Join(", ", areas.Take(show)), areas.Count - show);
    }

    private static string JoinAnd(List<string> items) => items.Count switch
    {
        1 => items[0],
        _ => L.F("{0} and {1}", string.Join(", ", items.Take(items.Count - 1)), items[^1]),
    };
}

public enum ActivityKind { Uploaded, Downloaded, Restored, Conflict, Resolved, Snapshot, GameUpdated }

/// <summary>One line of the activity timeline: what changed, where and when.</summary>
public sealed class ActivityEntry
{
    public DateTime Utc { get; set; }
    public string Version { get; set; } = "";
    public ActivityKind Kind { get; set; }

    /// <summary>The other computer involved (who uploaded what was downloaded), when known.</summary>
    public string? Computer { get; set; }

    /// <summary>Plain words, e.g. "Details, WeakAuras and Key bindings".</summary>
    public string Summary { get; set; } = "";
    public int FileCount { get; set; }

    /// <summary>The changed files (at most <see cref="ActivityStore.MaxFilesPerEntry"/>), for the details view.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>The snapshot's name, or the game version a game-update copy was made for.</summary>
    public string? Name { get; set; }

    /// <summary>"Uploaded Details, WeakAuras and Key bindings" — the headline for the timeline, in the interface language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Headline
    {
        get
        {
            var what = Files.Count > 0 ? SettingsItem.Summarize(Files) : Summary;
            return Kind switch
            {
                ActivityKind.Uploaded => L.F("Uploaded {0}", what),
                ActivityKind.Downloaded => Computer is { Length: > 0 } c ? L.F("Downloaded {0} from {1}", what, c) : L.F("Downloaded {0}", what),
                ActivityKind.Restored => L.F("Restored {0}", what),
                ActivityKind.Conflict => L.T("Settings changed on two computers"),
                ActivityKind.Snapshot when Name != null => L.F("Saved the snapshot \"{0}\"", Name),
                ActivityKind.GameUpdated when Name != null => L.F("Game updated to {0}: saved a copy of the settings", Name),
                _ => Summary,
            };
        }
    }

    public static ActivityEntry ForFiles(DateTime utc, string version, ActivityKind kind, IReadOnlyCollection<string> files, string? computer = null) => new()
    {
        Utc = utc,
        Version = version,
        Kind = kind,
        Computer = computer,
        Summary = SettingsItem.Summarize(files),
        FileCount = files.Count,
        Files = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(ActivityStore.MaxFilesPerEntry).ToList(),
    };
}

/// <summary>
/// %LOCALAPPDATA%\InterfaceSync\activity.json: the newest 300 sync events, newest first. Written only when something
/// actually synced (never while idle), so it adds a few KB per sync session at most.
/// </summary>
public sealed class ActivityStore
{
    public const int MaxEntries = 300;
    public const int MaxFilesPerEntry = 60;

    private readonly IFileSystem _fs;
    private readonly object _gate = new();
    private List<ActivityEntry>? _cache;

    public ActivityStore(IFileSystem fs, string dataFolder)
    {
        _fs = fs;
        FilePath = PathUtil.Join(dataFolder, "activity.json");
    }

    public string FilePath { get; }

    public IReadOnlyList<ActivityEntry> List(int max = MaxEntries)
    {
        lock (_gate) return Load().Take(max).ToList();
    }

    public void Add(ActivityEntry entry)
    {
        lock (_gate)
        {
            var all = Load();
            int at = all.FindIndex(e => e.Utc <= entry.Utc);   // newest first, even if an older event arrives late
            all.Insert(at < 0 ? all.Count : at, entry);
            if (all.Count > MaxEntries) all.RemoveRange(MaxEntries, all.Count - MaxEntries);
            try { _fs.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json.Options)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the timeline is a nicety
        }
    }

    private List<ActivityEntry> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            _cache = _fs.FileExists(FilePath)
                ? JsonSerializer.Deserialize<List<ActivityEntry>>(_fs.ReadAllText(FilePath), Json.Options) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { _cache = []; }
        _cache = _cache.OrderByDescending(e => e.Utc).ToList();
        return _cache;
    }

    /// <summary>"Today", "Yesterday", "Monday 17 November" — the timeline's group headings.</summary>
    public static string DayHeading(DateTime utc, DateTime nowUtc)
    {
        var day = utc.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.ToString(day.Year == today.Year ? "dddd d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture);
    }
}
