using System.Text.Json;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Settings;

namespace InterfaceSync.Core.Sync;

/// <summary>
/// Where things live in the Drive folder. Same layout as v1.1 so its data is reused:
/// &lt;My Drive&gt;\InterfaceSync\&lt;version&gt;\WTF-Account
/// </summary>
public sealed class CloudLayout
{
    public static string AppFolderName => ProductInfo.Id;
    public const string AccountFolderName = "WTF-Account";
    public static string CommitFileName => $"WTF-Account.{ProductInfo.Id.ToLowerInvariant()}.json";

    public CloudLayout(string myDrivePath, string cloudVersionFolderName, CloudProvider provider = CloudProvider.GoogleDrive)
    {
        MyDrive = myDrivePath;
        Provider = provider;
        VersionRoot = PathUtil.Join(myDrivePath, AppFolderName, cloudVersionFolderName);
        Account = PathUtil.Join(VersionRoot, AccountFolderName);
        CommitFile = PathUtil.Join(VersionRoot, CommitFileName);
        LegacyCommitFile = PathUtil.Join(VersionRoot, ProductInfo.Legacy.CommitFileName);
    }

    /// <summary>The cloud folder root on this PC (Google Drive's "My Drive", the OneDrive or the Dropbox folder).</summary>
    public string MyDrive { get; }
    public CloudProvider Provider { get; }
    public string ProviderName => Provider.DisplayName();
    public string VersionRoot { get; }
    public string Account { get; }
    public string CommitFile { get; }

    /// <summary>The commit file name earlier builds wrote; read if the current one is missing, removed on the next push.</summary>
    public string LegacyCommitFile { get; }

    /// <summary>
    /// Cloud folder name for a game version. Defaults to the game folder name (e.g. "_retail_");
    /// a settings mapping can point at a differently named v1.1 folder.
    /// </summary>
    public static string FolderNameFor(string gameVersionFolder, AppSettings settings) =>
        settings.CloudFolderNames.TryGetValue(gameVersionFolder, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : gameVersionFolder;

    /// <summary>
    /// Picks the cloud folder for a game version: a saved mapping first; otherwise the game folder name
    /// ("_forever_"), unless only the name without underscores ("forever", as v1.1 may have used) holds data.
    /// <paramref name="adopted"/> is set when the v1.1-style name was chosen, so the caller can save it.
    /// <paramref name="note"/> explains anything the player should know (e.g. both folders hold data).
    /// </summary>
    public static string ResolveFolderName(IFileSystem fs, string myDrive, string gameVersionFolder, AppSettings settings,
        out string? adopted, out string? note)
    {
        adopted = null;
        note = null;
        if (settings.CloudFolderNames.TryGetValue(gameVersionFolder, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped;

        var plain = gameVersionFolder.Trim('_');
        var primary = new CloudLayout(myDrive, gameVersionFolder);
        if (plain.Length == 0 || plain.Equals(gameVersionFolder, StringComparison.OrdinalIgnoreCase)) return gameVersionFolder;
        var alternate = new CloudLayout(myDrive, plain);

        bool primaryHasData = (fs.FileExists(primary.CommitFile) || fs.FileExists(primary.LegacyCommitFile)) || FolderScanner.AllFiles(fs, primary.Account).Count > 0;
        bool alternateHasData = (fs.FileExists(alternate.CommitFile) || fs.FileExists(alternate.LegacyCommitFile)) || FolderScanner.AllFiles(fs, alternate.Account).Count > 0;

        if (!primaryHasData && alternateHasData)
        {
            adopted = plain;
            note = $"{gameVersionFolder}: using the existing cloud folder {alternate.VersionRoot} (named as by WoWSync 1.1, the previous version).";
            return plain;
        }
        if (primaryHasData && alternateHasData)
            note = $"{gameVersionFolder}: both {primary.VersionRoot} and {alternate.VersionRoot} hold settings. Using {primary.VersionRoot}; " +
                   $"{alternate.VersionRoot} is left untouched.";
        return gameVersionFolder;
    }
}

public enum CommitState { Writing, Committed }

/// <summary>
/// Written beside the cloud copy. "Writing" while a push is copying, "Committed" with the full file list
/// once it is done. A PC only pulls a committed copy whose files have all arrived at the listed sizes,
/// so a half-uploaded copy is never pulled even though Drive delivers files one by one.
/// </summary>
public sealed class CommitFile
{
    public string Format { get; set; } = "interfacesync-commit/1";
    public CommitState State { get; set; }
    public string CommitId { get; set; } = "";
    public string PushedBy { get; set; } = "";
    public DateTime PushedAtUtc { get; set; }
    public List<ManifestEntry> Files { get; set; } = [];

    public Manifest ToManifest() => new() { Entries = Files };

    public static CommitFile? TryRead(IFileSystem fs, string path)
    {
        if (!fs.FileExists(path)) return null;
        try { return JsonSerializer.Deserialize<CommitFile>(fs.ReadAllText(path), Json.Options); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Write(IFileSystem fs, string path) => fs.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
}

public enum KeepChoice { Local, Cloud }

public sealed class ConflictInfo
{
    public DateTime DetectedUtc { get; set; }
    public string Reason { get; set; } = "";
    public DateTime? LocalNewestUtc { get; set; }
    public DateTime? CloudNewestUtc { get; set; }
    public string? CloudPushedBy { get; set; }
    public string? CloudCommitId { get; set; }
    public string? CloudBackupPath { get; set; }

    /// <summary>The newer copy is the default choice.</summary>
    public KeepChoice Suggested =>
        (CloudNewestUtc ?? DateTime.MinValue) > (LocalNewestUtc ?? DateTime.MinValue) ? KeepChoice.Cloud : KeepChoice.Local;
}

/// <summary>Per game version state, persisted so a pending push survives reboots.</summary>
public sealed class VersionState
{
    /// <summary>True after the first successful sync on this PC.</summary>
    public bool Joined { get; set; }
    public bool PendingPush { get; set; }
    public DateTime? LastPushUtc { get; set; }
    public DateTime? LastPullUtc { get; set; }

    /// <summary>Commit id of the cloud copy this PC last pushed or pulled.</summary>
    public string? LastCommitId { get; set; }

    /// <summary>The local folder as it was right after the last sync.</summary>
    public Manifest? LocalManifest { get; set; }

    public ConflictInfo? Conflict { get; set; }

    /// <summary>The cloud folder this PC last synced with, so a switch to another folder or provider is noticed.</summary>
    public string? CloudFolder { get; set; }

    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
}

public sealed class StateStore
{
    private readonly IFileSystem _fs;
    private readonly string _folder;

    public StateStore(IFileSystem fs, string dataFolder)
    {
        _fs = fs;
        _folder = PathUtil.Join(dataFolder, "State");
    }

    private string FileFor(string versionFolder) => PathUtil.Join(_folder, versionFolder.Trim('_') + ".json");

    public VersionState Load(string versionFolder)
    {
        var path = FileFor(versionFolder);
        if (!_fs.FileExists(path)) return new VersionState();
        try { return JsonSerializer.Deserialize<VersionState>(_fs.ReadAllText(path), Json.Options) ?? new VersionState(); }
        catch (JsonException)
        {
            // Unreadable state: start as "not joined" so the safe first-sync rule applies (cloud wins, local backed up).
            return new VersionState();
        }
    }

    public void Save(string versionFolder, VersionState state) =>
        _fs.WriteAllText(FileFor(versionFolder), JsonSerializer.Serialize(state, Json.Options));
}
