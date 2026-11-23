using System.Text.RegularExpressions;
using InterfaceSync.Core.Abstractions;

namespace InterfaceSync.Core.Detection;

/// <summary>
/// Everything that differs between cloud apps. The sync engine only sees a folder; it never asks
/// which app is behind it, except through this interface.
/// </summary>
public interface ICloudProvider
{
    CloudProvider Kind { get; }
    string DisplayName { get; }

    /// <summary>The app's synced folder on this PC, how it was found, and its mode.</summary>
    DriveDetectionResult Detect(string? userOverride);

    /// <summary>True when the file's bytes aren't on this PC (a placeholder the app downloads on demand).</summary>
    bool IsOnlineOnly(FileEntryInfo file);

    /// <summary>True when the name is this app's conflict copy of a file (see each app's naming).</summary>
    bool IsConflictCopy(string name, ISet<string> siblings);

    /// <summary>How the player keeps the app's folder on this PC.</summary>
    string KeepOnDeviceHelp { get; }

    /// <summary>Whether WoWSync can mark a folder "always keep on this device" itself (with the player's OK).</summary>
    bool CanPinFolder { get; }

    /// <summary>Marks a folder and everything in it "always keep on this device".</summary>
    void PinFolder(IFileSystem fs, string folder);
}

/// <summary>Shared rules: every supported app uses the Windows cloud-file attributes for online-only placeholders.</summary>
public abstract class CloudProviderBase : ICloudProvider
{
    public abstract CloudProvider Kind { get; }
    public string DisplayName => Kind.DisplayName();
    protected CloudProviderBase(HostOs os) => Os = os;
    protected HostOs Os { get; }
    public string KeepOnDeviceHelp => Kind.KeepOfflineHint(Os);
    public abstract DriveDetectionResult Detect(string? userOverride);
    public abstract bool IsConflictCopy(string name, ISet<string> siblings);

    /// <summary>RECALL_ON_DATA_ACCESS, RECALL_ON_OPEN or OFFLINE: the bytes are in the cloud only.</summary>
    public bool IsOnlineOnly(FileEntryInfo file) => file.IsOnlineOnly;

    public virtual bool CanPinFolder => false;

    public virtual void PinFolder(IFileSystem fs, string folder) =>
        throw new NotSupportedException($"{DisplayName}: keep the folder on this PC from the app itself. {KeepOnDeviceHelp}");
}

public sealed class GoogleDriveProvider(IFileSystem fs, IRegistry registry, IUserEnvironment env, string scratchFolder) : CloudProviderBase(env.Os)
{
    // "Name (1).ext", "Name (2)".
    private static readonly Regex Pattern = new(@"^.+ \(\d+\)(\.[^.\\/ ]+)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override CloudProvider Kind => CloudProvider.GoogleDrive;
    public override DriveDetectionResult Detect(string? userOverride) => new DriveDetector(fs, registry, env, scratchFolder).Detect(userOverride);
    public override bool IsConflictCopy(string name, ISet<string> siblings) => IsConflictName(name);
    public static bool IsConflictName(string name) => Pattern.IsMatch(name);
}

public sealed class OneDriveProvider(IFileSystem fs, IRegistry registry, IUserEnvironment env) : CloudProviderBase(env.Os)
{
    public override CloudProvider Kind => CloudProvider.OneDrive;
    public override DriveDetectionResult Detect(string? userOverride) => new OneDriveDetector(fs, registry, env).Detect(userOverride);
    public override bool IsConflictCopy(string name, ISet<string> siblings) => IsConflictName(name, siblings);

    /// <summary>
    /// "Name-PCNAME.ext" (optionally "-2"). Only counts when the original "Name.ext" is beside it and the
    /// suffix looks like a computer name (capitals, digits, dashes), so "Details-Forever.lua" never matches.
    /// </summary>
    public static bool IsConflictName(string name, ISet<string> siblings)
    {
        int dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        for (int i = stem.IndexOf('-'); i > 0; i = stem.IndexOf('-', i + 1))
        {
            var suffix = stem[(i + 1)..];
            if (LooksLikeComputerName(suffix) && siblings.Contains(stem[..i] + ext)) return true;
        }
        return false;
    }

    private static bool LooksLikeComputerName(string s) =>
        s.Length is >= 1 and <= 24
        && (char.IsAsciiLetterUpper(s[0]) || char.IsAsciiDigit(s[0]))
        && s.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '-');

    // OneDrive on Windows honours FILE_ATTRIBUTE_PINNED, which is what "Always keep on this device" sets.
    // On macOS it is a File Provider setting the player changes in Finder.
    public override bool CanPinFolder => Os == HostOs.Windows;

    public override void PinFolder(IFileSystem fs, string folder)
    {
        fs.CreateDirectory(folder);
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            Pin(fs, dir);
            foreach (var f in fs.EnumerateFiles(dir)) Pin(fs, f);
            foreach (var d in fs.EnumerateDirectories(dir)) if (!fs.IsReparsePoint(d)) stack.Push(d);
        }
    }

    private static void Pin(IFileSystem fs, string path) =>
        fs.SetAttributes(path, (fs.GetAttributes(path) | FileEntryInfo.Pinned) & ~FileEntryInfo.Unpinned);
}

public sealed class DropboxProvider(IFileSystem fs, IUserEnvironment env) : CloudProviderBase(env.Os)
{
    // "Name (PC's conflicted copy 2026-11-18).ext", "Name (Case Conflict).ext", "(Selective Sync Conflict)".
    private static readonly Regex Pattern =
        new(@"\((?:[^()]*'s )?conflicted copy[^()]*\)|\((?:Case|Selective Sync) Conflict[^()]*\)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override CloudProvider Kind => CloudProvider.Dropbox;
    public override DriveDetectionResult Detect(string? userOverride) => new DropboxDetector(fs, env).Detect(userOverride);
    public override bool IsConflictCopy(string name, ISet<string> siblings) => IsConflictName(name);
    public static bool IsConflictName(string name) => Pattern.IsMatch(name);
}

public sealed class ICloudDriveProvider(IFileSystem fs, IUserEnvironment env) : CloudProviderBase(env.Os)
{
    // Apple names a conflict copy "Name 2.ext" (then 3, 4…) beside the original.
    private static readonly Regex Numbered = new(@"^(?<stem>.+) (?<n>[2-9]|[1-9]\d{1,2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override CloudProvider Kind => CloudProvider.ICloudDrive;
    public override DriveDetectionResult Detect(string? userOverride) => new ICloudDriveDetector(fs, env).Detect(userOverride);
    public override bool IsConflictCopy(string name, ISet<string> siblings) => IsConflictName(name, siblings);

    /// <summary>
    /// "Name 2.ext" (or "Folder 2"), only when the original "Name.ext" is beside it, so a file that merely ends in a
    /// number (a realm like "Area 52", an addon's "Foo 2.lua" with no "Foo.lua") is never taken for a copy.
    /// </summary>
    public static bool IsConflictName(string name, ISet<string> siblings)
    {
        int dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        var m = Numbered.Match(stem);
        return m.Success && siblings.Contains(m.Groups["stem"].Value + ext);
    }
}

public static class CloudProviderCatalog
{
    public static ICloudProvider Create(CloudProvider kind, IFileSystem fs, IRegistry registry, IUserEnvironment env, string scratchFolder) => kind switch
    {
        CloudProvider.OneDrive => new OneDriveProvider(fs, registry, env),
        CloudProvider.Dropbox => new DropboxProvider(fs, env),
        CloudProvider.ICloudDrive => new ICloudDriveProvider(fs, env),
        _ => new GoogleDriveProvider(fs, registry, env, scratchFolder),
    };

    /// <summary>
    /// A conflict copy from any supported app. The scanner checks all three whichever app is in use:
    /// a folder can hold copies made while the player used another app, and none of them may ever be synced.
    /// </summary>
    public static bool IsAnyConflictCopy(string name, ISet<string> siblings) =>
        GoogleDriveProvider.IsConflictName(name)
        || DropboxProvider.IsConflictName(name)
        || OneDriveProvider.IsConflictName(name, siblings)
        || ICloudDriveProvider.IsConflictName(name, siblings);
}
