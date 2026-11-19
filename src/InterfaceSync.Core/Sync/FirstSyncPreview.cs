namespace InterfaceSync.Core.Sync;

/// <summary>What a version's first sync would find, shown in the setup window before anything is copied.</summary>
public sealed record FirstSyncPreview(
    string Version,
    string ProviderName,
    bool AlreadySynced,
    bool CloudHasData,
    string? CloudFrom,
    DateTime? CloudNewestUtc,
    DateTime? LocalNewestUtc,
    string? StaleProblem)
{
    /// <summary>The newer copy is suggested; with no cloud data there is nothing to choose.</summary>
    public Settings.FirstSyncChoice Suggested =>
        LocalNewestUtc != null && (CloudNewestUtc == null || LocalNewestUtc > CloudNewestUtc + Manifest.TimeTolerance)
            ? Settings.FirstSyncChoice.UseThisComputer
            : Settings.FirstSyncChoice.UseCloud;

    /// <summary>True when setup should show the player a choice for this version.</summary>
    public bool NeedsChoice => !AlreadySynced && CloudHasData && LocalNewestUtc != null && StaleProblem == null;

    private static string When(DateTime? utc) => utc?.ToLocalTime().ToString("g") ?? "unknown";

    /// <summary>"Use the settings already in Google Drive (from HomePC, last changed 11/18 2:56 PM). This PC's are backed up first."</summary>
    public string CloudOptionText(string computer = "PC") =>
        $"Use the settings already in {ProviderName} ({(CloudFrom != null ? $"from {CloudFrom}, " : "")}last changed {When(CloudNewestUtc)}). " +
        $"This {computer}'s are backed up first.";

    /// <summary>"Use this PC's settings (last changed …) and replace the ones in Google Drive, which are backed up first."</summary>
    public string LocalOptionText(string computer = "PC") =>
        $"Use this {computer}'s settings (last changed {When(LocalNewestUtc)}) and replace the ones in {ProviderName}, which are backed up first.";
}
