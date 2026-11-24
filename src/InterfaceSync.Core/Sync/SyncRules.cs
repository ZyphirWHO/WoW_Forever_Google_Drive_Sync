namespace InterfaceSync.Core.Sync;

/// <summary>
/// "Choose what syncs": characters and addons this computer leaves out for a game version. Their files on this
/// computer are never uploaded, downloaded or deleted, and their copies in the cloud are left as they are, so other
/// computers keep syncing them. Switching one back on downloads the cloud's copy (this computer's is backed up first).
/// </summary>
public sealed class SyncRules
{
    public static SyncRules None { get; } = new();

    /// <summary>"Realm/Character" keys (see <see cref="SettingsItem.CharacterKey"/>).</summary>
    public List<string> Characters { get; set; } = [];

    /// <summary>Addon names as in SavedVariables ("WeakAuras" for WeakAuras.lua).</summary>
    public List<string> Addons { get; set; } = [];

    public bool IsEmpty => Characters.Count == 0 && Addons.Count == 0;

    public bool Excludes(string relativePath)
    {
        if (IsEmpty) return false;
        var item = SettingsItem.Parse(relativePath);
        return (item.CharacterKey is { } c && Characters.Contains(c, StringComparer.OrdinalIgnoreCase))
               || (item.Addon is { } a && Addons.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    public bool SameAs(SyncRules? other) =>
        other != null && SameSet(Characters, other.Characters) && SameSet(Addons, other.Addons);

    /// <summary>True when <paramref name="newer"/> syncs something this leaves out (a character or addon switched back on).</summary>
    public bool IncludesLessThan(SyncRules newer) =>
        Characters.Except(newer.Characters, StringComparer.OrdinalIgnoreCase).Any()
        || Addons.Except(newer.Addons, StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>Leaves out everything either one leaves out.</summary>
    public SyncRules Union(SyncRules other) => new()
    {
        Characters = Characters.Union(other.Characters, StringComparer.OrdinalIgnoreCase).ToList(),
        Addons = Addons.Union(other.Addons, StringComparer.OrdinalIgnoreCase).ToList(),
    };

    public SyncRules Copy() => new() { Characters = [.. Characters], Addons = [.. Addons] };

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

    /// <summary>"2 characters and 1 addon not synced", or null when everything syncs.</summary>
    public string? Describe()
    {
        if (IsEmpty) return null;
        var parts = new List<string>();
        if (Characters.Count > 0) parts.Add(Characters.Count == 1 ? "1 character" : $"{Characters.Count} characters");
        if (Addons.Count > 0) parts.Add(Addons.Count == 1 ? "1 addon" : $"{Addons.Count} addons");
        return $"{string.Join(" and ", parts)} not synced on this computer";
    }

    private static bool SameSet(List<string> a, List<string> b) =>
        a.Count == b.Count && !a.Except(b, StringComparer.OrdinalIgnoreCase).Any();
}

/// <summary>What can be left out of a game version's sync: its characters and addons, from the settings folder.</summary>
public sealed record SyncChoices(IReadOnlyList<(string Key, string Label)> Characters, IReadOnlyList<string> Addons)
{
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
