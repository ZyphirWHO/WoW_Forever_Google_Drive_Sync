using System.Runtime.InteropServices;
using System.Text.Json;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Settings;

namespace InterfaceSync.Core.Sync;

/// <summary>
/// One computer that syncs this game version. Each computer writes its own small file at
/// &lt;cloud&gt;\InterfaceSync\&lt;version&gt;\Computers\&lt;name&gt;.json after it uploads or downloads (never while idle),
/// so every computer can list the others ("Your computers").
/// </summary>
public sealed class DeviceInfo
{
    public string Format { get; set; } = "interfacesync-computer/1";
    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTime LastSyncUtc { get; set; }

    /// <summary>"Uploaded" or "Downloaded".</summary>
    public string LastAction { get; set; } = "";

    /// <summary>Game versions this computer syncs (filled when lists from several versions are merged).</summary>
    public List<string> Versions { get; set; } = [];

    /// <summary>Set when read: this is the computer the app runs on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsThisComputer { get; set; }
}

public static class Devices
{
    public const string FolderName = "Computers";

    public static string FolderFor(CloudLayout cloud) => PathUtil.Join(cloud.VersionRoot, FolderName);

    /// <summary>A file name made from the computer name: letters, digits, '-' and '_' only.</summary>
    public static string FileNameFor(string machine)
    {
        var chars = machine.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var name = new string(chars).Trim('_');
        return (name.Length == 0 ? "computer" : name) + ".json";
    }

    public static string SystemDescription()
    {
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsWindows())
            return Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
        return RuntimeInformation.OSDescription;
    }

    /// <summary>Writes this computer's file. Only inside the version folder; failures are ignored (it's informational).</summary>
    public static void Write(IFileSystem fs, CloudLayout cloud, string machine, string action, DateTime utc)
    {
        if (!cloud.IsSafeWriteTarget(out _)) return;
        try
        {
            var folder = FolderFor(cloud);
            fs.CreateDirectory(folder);
            var info = new DeviceInfo
            {
                Name = machine,
                System = SystemDescription(),
                AppVersion = ProductInfo.DisplayVersion,
                LastSyncUtc = utc,
                LastAction = action,
            };
            fs.WriteAllText(PathUtil.Join(folder, FileNameFor(machine)), JsonSerializer.Serialize(info, Json.Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Every computer's file for one game version. Cloud conflict copies and unreadable files are skipped.</summary>
    public static List<DeviceInfo> Read(IFileSystem fs, CloudLayout cloud)
    {
        var result = new List<DeviceInfo>();
        var folder = FolderFor(cloud);
        try
        {
            if (!fs.DirectoryExists(folder)) return result;
            var files = fs.EnumerateFiles(folder).ToList();
            var names = files.Select(PathUtil.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var name = PathUtil.Name(file);
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || FolderScanner.IsConflictCopyName(name, names)) continue;
                try
                {
                    var info = JsonSerializer.Deserialize<DeviceInfo>(fs.ReadAllText(file), Json.Options);
                    if (info is { Name.Length: > 0 }) result.Add(info);
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>Merges the lists of several game versions: one row per computer, newest sync wins, versions joined.</summary>
    public static List<DeviceInfo> Merge(IEnumerable<(string Version, IEnumerable<DeviceInfo> Devices)> perVersion, string thisMachine)
    {
        var byName = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (version, devices) in perVersion)
            foreach (var d in devices)
            {
                if (!byName.TryGetValue(d.Name, out var existing))
                {
                    existing = new DeviceInfo { Name = d.Name };
                    byName[d.Name] = existing;
                }
                if (d.LastSyncUtc >= existing.LastSyncUtc)
                {
                    existing.LastSyncUtc = d.LastSyncUtc;
                    existing.LastAction = d.LastAction;
                    if (d.System.Length > 0) existing.System = d.System;
                    if (d.AppVersion.Length > 0) existing.AppVersion = d.AppVersion;
                }
                if (!existing.Versions.Contains(version, StringComparer.OrdinalIgnoreCase)) existing.Versions.Add(version);
            }
        foreach (var d in byName.Values) d.IsThisComputer = d.Name.Equals(thisMachine, StringComparison.OrdinalIgnoreCase);
        return byName.Values.OrderByDescending(d => d.IsThisComputer).ThenByDescending(d => d.LastSyncUtc).ToList();
    }
}
