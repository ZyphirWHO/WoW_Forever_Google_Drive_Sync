using System.Text;
using System.Text.RegularExpressions;

namespace InterfaceSync.Core;

/// <summary>"What's new": the release notes of each version, embedded from docs/release-notes/&lt;version&gt;.md.</summary>
public static class ReleaseNotes
{
    /// <summary>"3.1.0" for "3.1.0-dev.57".</summary>
    public static string BaseVersion(string version) => version.Split('-', '+')[0];

    /// <summary>The notes for a version as written (Markdown), or null when none were embedded.</summary>
    public static string? For(string version)
    {
        var name = $"release-notes/{BaseVersion(version)}.md";
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The running version's notes.</summary>
    public static string? Current => For(ProductInfo.DisplayVersion);

    /// <summary>Markdown as plain text for a window: headings on their own line, bullets as "•", no ** or `.</summary>
    public static string ToPlainText(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) line = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) line = "• " + trimmed[2..];
            else if (raw.StartsWith("  ") && sb.Length > 0 && line.Length > 0)
            {
                // A wrapped bullet: join it to the line above.
                sb.Length -= Environment.NewLine.Length;
                line = " " + trimmed;
            }
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            sb.Append(line).Append(Environment.NewLine);
        }
        return Regex.Replace(sb.ToString().Trim(), @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
    }
}

/// <summary>Help and "Report a problem" links.</summary>
public static class Feedback
{
    public static Uri ProjectPage => new($"https://github.com/{ProductInfo.Repository}");
    public static Uri HelpPage => new($"https://github.com/{ProductInfo.Repository}/blob/main/docs/FAQ.md");
    public static Uri ReleasesPage => new($"https://github.com/{ProductInfo.Repository}/releases");

    /// <summary>GitHub's limit on a new-issue link is about 8 KB; the body is trimmed to stay well under it.</summary>
    public const int MaxBodyLength = 5000;

    /// <summary>
    /// A link that opens a new GitHub issue with the diagnostics filled in. Nothing is sent until the player reviews it
    /// and clicks Submit on GitHub. The diagnostics must already be redacted (<see cref="Redact"/>).
    /// </summary>
    public static Uri IssueUrl(string redactedDiagnostics)
    {
        var body = new StringBuilder()
            .AppendLine("**What happened?**")
            .AppendLine()
            .AppendLine("<!-- Describe what you saw and what you expected. Screenshots help. -->")
            .AppendLine()
            .AppendLine("**Diagnostics** (folder names only; check there's nothing you'd rather not share)")
            .AppendLine("```")
            .ToString();
        const string end = "\n```\n";
        var room = MaxBodyLength - body.Length - end.Length;
        var diag = redactedDiagnostics.Length <= room ? redactedDiagnostics : "…" + redactedDiagnostics[^(room - 1)..];
        var title = $"Problem in {ProductInfo.DisplayVersion}: ";
        return new Uri($"https://github.com/{ProductInfo.Repository}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body + diag + end)}");
    }

    /// <summary>
    /// Replaces personal details with placeholders: each folder path becomes its label (the home folder becomes "~"),
    /// longest first so a path inside the home folder keeps its own label; each name (computer, user account) becomes
    /// its label wherever it appears as a whole word.
    /// </summary>
    public static string Redact(string text, IEnumerable<(string Path, string Label)> paths, IEnumerable<(string Name, string Label)> names)
    {
        foreach (var (value, label) in paths.Where(r => r.Path.Length >= 3).OrderByDescending(r => r.Path.Length))
        {
            text = text.Replace(value, label, StringComparison.OrdinalIgnoreCase);
            var other = value.Contains('\\') ? value.Replace('\\', '/') : value.Replace('/', '\\');
            if (other != value) text = text.Replace(other, label, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (name, label) in names.Where(n => n.Name.Length >= 2))
            text = Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])", label, RegexOptions.IgnoreCase);
        return text;
    }
}
