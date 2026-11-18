using InterfaceSync.Core.Sync;

namespace InterfaceSync.Core.Service;

/// <summary>Plain-words result of one version's first sync, shared by the Windows and Mac setup windows.</summary>
public static class FirstSyncReport
{
    /// <param name="computer">"PC" on Windows, "Mac" on macOS.</param>
    public static (string text, bool problem) Describe(VersionSync w, SyncOutcome outcome, bool firstSync, string computer = "PC") => outcome switch
    {
        SyncOutcome.Pushed when firstSync => ($"Done. {w.Cloud.ProviderName} had no settings for this version yet, so this {computer}'s were uploaded.", false),
        SyncOutcome.Pushed => ($"Done. This {computer}'s settings were uploaded to {w.Cloud.ProviderName}.", false),
        SyncOutcome.Pulled when firstSync => ($"Done. The settings already in {w.Cloud.ProviderName} were copied to this {computer} (this {computer}'s previous settings are backed up).", false),
        SyncOutcome.Pulled => ($"Done. Newer settings from {w.Cloud.ProviderName} were copied to this {computer}.", false),
        SyncOutcome.NothingToDo => (w.Status == SyncStatus.Idle && w.StatusText.StartsWith("Nothing", StringComparison.Ordinal)
            ? w.StatusText : "Done. Already in sync.", false),
        SyncOutcome.Waiting when w.Status == SyncStatus.GameRunning =>
            ("Waiting: this game is running. It will sync after you close it.", false),
        SyncOutcome.Waiting => ($"Waiting: {w.StatusText}. It will sync on its own when ready.", false),
        SyncOutcome.Busy => ("Will sync in the background in a moment.", false),
        SyncOutcome.Conflict => ("Needs your choice: settings changed in two places. Open the status window to choose.", true),
        _ => ($"Problem: {(string.IsNullOrEmpty(w.StatusText) ? w.State.LastError : w.StatusText)}", true),
    };
}
