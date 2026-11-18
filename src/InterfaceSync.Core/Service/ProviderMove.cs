using System.Text.Json;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Settings;

namespace InterfaceSync.Core.Service;

/// <summary>Left in the old cloud folder after its data moved to another app, so no PC keeps syncing there.</summary>
public sealed class MovedMarker
{
    public const string FileName = "MOVED-TO.json";
    public CloudProvider Provider { get; set; }
    public string MovedBy { get; set; } = "";
    public DateTime MovedAtUtc { get; set; }

    public static string PathIn(string appFolder) => PathUtil.Join(appFolder, FileName);

    public static MovedMarker? TryRead(Abstractions.IFileSystem fs, string appFolder)
    {
        var path = PathIn(appFolder);
        if (!fs.FileExists(path)) return null;
        try { return JsonSerializer.Deserialize<MovedMarker>(fs.ReadAllText(path), Json.Options); }
        catch (JsonException) { return new MovedMarker(); }
    }
}

public sealed record ProviderChangeResult(bool Changed, string Message);
