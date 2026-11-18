namespace WoWSync.Core;

/// <summary>
/// Separator-agnostic path helpers. Core runs on Windows, but tests run on Linux against
/// Windows-shaped sample paths, so never rely on Path.GetFileName for '\' paths.
/// </summary>
public static class PathUtil
{
    private static readonly char[] Separators = ['\\', '/'];

    public static string TrimEnd(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        // Keep "C:\" a root rather than "C:"
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed.Length == 0 ? path : trimmed;
    }

    public static string Name(string path)
    {
        var t = path.TrimEnd(Separators);
        var i = t.LastIndexOfAny(Separators);
        return i < 0 ? t : t[(i + 1)..];
    }

    public static string? Parent(string path)
    {
        var t = path.TrimEnd(Separators);
        var i = t.LastIndexOfAny(Separators);
        if (i <= 0) return null;
        var parent = t[..i];
        if (parent.Length == 2 && parent[1] == ':') parent += "\\";
        return parent;
    }

    /// <summary>Joins using the separator style the base path already uses.</summary>
    public static string Join(string basePath, params string[] parts)
    {
        char sep = basePath.Contains('\\') || (basePath.Length >= 2 && basePath[1] == ':') ? '\\' : Path.DirectorySeparatorChar;
        var result = basePath.TrimEnd(Separators);
        foreach (var part in parts)
        {
            var p = part.Trim(Separators);
            if (p.Length == 0) continue;
            result = result + sep + (sep == '\\' ? p.Replace('/', '\\') : p.Replace('\\', '/'));
        }
        return result;
    }

    public static string Normalize(string path) => TrimEnd(path).Replace('/', '\\').ToUpperInvariant();

    public static bool SamePath(string a, string b) => Normalize(a) == Normalize(b);

    /// <summary>True when <paramref name="child"/> is inside (or equal to) <paramref name="parent"/>. Case-insensitive.</summary>
    public static bool IsUnder(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        return c == p || c.StartsWith(p.EndsWith('\\') ? p : p + "\\", StringComparison.Ordinal);
    }

    /// <summary>Relative path of a file under a root, always with '\' separators (manifest form).</summary>
    public static string Relative(string root, string fullPath)
    {
        var r = TrimEnd(root).Replace('/', '\\');
        var f = fullPath.Replace('/', '\\');
        if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{fullPath}' is not under '{root}'.");
        return f[r.Length..].TrimStart('\\');
    }

    /// <summary>Converts a manifest-form relative path into a full path under root.</summary>
    public static string FromRelative(string root, string relative) =>
        Join(root, relative.Split('\\', StringSplitOptions.RemoveEmptyEntries));

    public static string ExpandUserVars(string path, string userProfile, string localAppData) =>
        path.Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", localAppData, StringComparison.OrdinalIgnoreCase);
}
