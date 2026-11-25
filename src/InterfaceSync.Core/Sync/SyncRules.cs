using InterfaceSync.Core.Localization;

namespace InterfaceSync.Core.Sync;

/// <summary>
/// "Choose what syncs": characters and addons this computer leaves out for a game version. Their files on this
/// computer are never uploaded, downloaded or deleted, and their copies in the cloud are left as they are, so other
/// computers keep syncing them. Switching one back on downloads the cloud's copy (this computer's is backed up first).
/// </summary>
public sealed class SyncRules
{
    /// <summary>Everything syncs. A new object each time: rules are changed in place by the service (installed addons).</summary>
    public static SyncRules None => new();

    /// <summary>"Realm/Character" keys (see <see cref="SettingsItem.CharacterKey"/>).</summary>
    public List<string> Characters { get; set; } = [];

    /// <summary>Addon names as in SavedVariables ("WeakAuras" for WeakAuras.lua).</summary>
    public List<string> Addons { get; set; } = [];

    /// <summary>
    /// The addons installed on this computer (folders in Interface\AddOns), when "leave out addons that aren't
    /// installed" is on: the settings of any other addon are left out. Null: off, or the AddOns folder couldn't be read.
    /// Set by the service, never typed by the player.
    /// </summary>
    public List<string>? InstalledAddons
    {
        get => _installed;
        set { _installed = value; _installedSet = value == null ? null : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase); }
    }
    private List<string>? _installed;
    private HashSet<string>? _installedSet;

    public bool IsEmpty => Characters.Count == 0 && Addons.Count == 0 && _installed == null;

    /// <summary>Left out because the player unticked it, or because it isn't installed here.</summary>
    public bool ExcludesAddon(string addon) =>
        Addons.Contains(addon, StringComparer.OrdinalIgnoreCase) || (_installedSet != null && !_installedSet.Contains(addon));

    /// <summary>Left out only because it isn't installed on this computer (not because the player unticked it).</summary>
    public bool NotInstalled(string addon) => _installedSet != null && !_installedSet.Contains(addon);

    public bool Excludes(string relativePath)
    {
        if (IsEmpty) return false;
        var item = SettingsItem.Parse(relativePath);
        return (item.CharacterKey is { } c && Characters.Contains(c, StringComparer.OrdinalIgnoreCase))
               || (item.Addon is { } a && ExcludesAddon(a));
    }

    /// <summary>True when the file is left out only because its addon isn't installed (see <see cref="NotInstalled"/>).</summary>
    public bool ExcludesOnlyAsNotInstalled(string relativePath)
    {
        var item = SettingsItem.Parse(relativePath);
        return item.Addon is { } a && NotInstalled(a) && !Addons.Contains(a, StringComparer.OrdinalIgnoreCase)
               && !(item.CharacterKey is { } c && Characters.Contains(c, StringComparer.OrdinalIgnoreCase));
    }

    public bool SameAs(SyncRules? other) =>
        other != null && SameSet(Characters, other.Characters) && SameSet(Addons, other.Addons)
        && (_installed == null ? other._installed == null : other._installed != null && SameSet(_installed, other._installed));

    /// <summary>True when <paramref name="newer"/> syncs something this leaves out (switched back on, or an addon installed).</summary>
    public bool IncludesLessThan(SyncRules newer) =>
        Characters.Except(newer.Characters, StringComparer.OrdinalIgnoreCase).Any()
        || Addons.Any(a => !newer.ExcludesAddon(a))
        || (_installedSet != null && (newer._installedSet == null
            || newer._installedSet.Any(a => !_installedSet.Contains(a) && !newer.Addons.Contains(a, StringComparer.OrdinalIgnoreCase))));

    /// <summary>Leaves out everything either one leaves out.</summary>
    public SyncRules Union(SyncRules other) => new()
    {
        Characters = Characters.Union(other.Characters, StringComparer.OrdinalIgnoreCase).ToList(),
        Addons = Addons.Union(other.Addons, StringComparer.OrdinalIgnoreCase).ToList(),
        InstalledAddons = _installed == null ? other._installed?.ToList()
            : other._installed == null ? _installed.ToList()
            : _installed.Intersect(other._installed, StringComparer.OrdinalIgnoreCase).ToList(),
    };

    /// <summary>The player's own choices (without what the service adds about installed addons).</summary>
    public SyncRules ChoicesOnly() => new() { Characters = [.. Characters], Addons = [.. Addons] };

    public SyncRules Copy() => new() { Characters = [.. Characters], Addons = [.. Addons], InstalledAddons = _installed?.ToList() };

    public Manifest Keep(Manifest? manifest) =>
        manifest == null ? new Manifest() : IsEmpty ? manifest : new Manifest { Entries = manifest.Entries.Where(e => !Excludes(e.RelativePath)).ToList() };

    public FolderScan Keep(FolderScan scan)
    {
        if (IsEmpty) return scan;
        var kept = new FolderScan { Root = scan.Root, Exists = scan.Exists, Manifest = Keep(scan.Manifest) };
        kept.ConflictCopies.AddRange(scan.ConflictCopies);
        kept.Links.AddRange(scan.Links);
        kept.OnlineOnly.AddRange(scan.OnlineOnly.Where(p => !Excludes(p)));
        kept.TempFiles.AddRange(scan.TempFiles);
        return kept;
    }

    /// <summary>"2 characters and 1 addon not synced", or null when everything the player can choose syncs.</summary>
    public string? Describe()
    {
        if (Characters.Count == 0 && Addons.Count == 0) return null;
        if (Addons.Count == 0) return L.N(Characters.Count, "{0} character not synced on this computer", "{0} characters not synced on this computer");
        if (Characters.Count == 0) return L.N(Addons.Count, "{0} addon not synced on this computer", "{0} addons not synced on this computer");
        return L.F("{0} and {1} not synced on this computer",
            L.N(Characters.Count, "{0} character", "{0} characters"), L.N(Addons.Count, "{0} addon", "{0} addons"));
    }

    private static bool SameSet(List<string> a, List<string> b) =>
        a.Count == b.Count && !a.Except(b, StringComparer.OrdinalIgnoreCase).Any();
}

/// <summary>What can be left out of a game version's sync: its characters and addons, from the settings folder.</summary>
public sealed record SyncChoices(IReadOnlyList<(string Key, string Label)> Characters, IReadOnlyList<string> Addons)
{
    /// <summary>The addons installed on this computer, or null when the AddOns folder couldn't be read.</summary>
    public IReadOnlyList<string>? Installed { get; init; }

    public bool IsInstalled(string addon) => Installed == null || Installed.Contains(addon, StringComparer.OrdinalIgnoreCase);

    public static SyncChoices From(IEnumerable<string> relativePaths)
    {
        var items = relativePaths.Select(SettingsItem.Parse).ToList();
        var characters = items.Where(i => i.CharacterKey != null)
            .GroupBy(i => i.CharacterKey!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().CharacterLabel!))
            .OrderBy(c => c.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        var addons = items.Where(i => i.Addon != null).Select(i => i.Addon!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new SyncChoices(characters, addons);
    }
}

/// <summary>The addons installed for a game version: the folders in Interface\AddOns that hold a .toc file.</summary>
public static class InstalledAddons
{
    /// <summary>Their names (case does not matter), or null when the AddOns folder is missing or can't be read.</summary>
    public static List<string>? Read(Abstractions.IFileSystem fs, string addOnsFolder)
    {
        try
        {
            if (!fs.DirectoryExists(addOnsFolder)) return null;
            var names = new List<string>();
            foreach (var dir in fs.EnumerateDirectories(addOnsFolder))
                if (fs.EnumerateFiles(dir).Any(f => f.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)))
                    names.Add(PathUtil.Name(dir));
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Settings on this computer of addons that aren't installed: which addons, how many files, how big.</summary>
public sealed record UninstalledAddonData(IReadOnlyList<string> Addons, int FileCount, long Bytes)
{
    public static UninstalledAddonData None { get; } = new([], 0, 0);
}

/// <summary>What a clean-up removed (backed up first: <see cref="Backup"/> undoes it), or why nothing was removed.</summary>
public sealed record CleanUpResult(Backup.BackupInfo? Backup, IReadOnlyList<string> Addons, int FileCount, long Bytes, string? Problem = null);
