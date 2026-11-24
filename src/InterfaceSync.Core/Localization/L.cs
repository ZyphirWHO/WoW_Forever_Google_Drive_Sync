using System.Globalization;
using System.Text.Json;

namespace InterfaceSync.Core.Localization;

/// <summary>
/// Interface translations. The English text is the key, so code reads naturally (<c>L.T("Sync now")</c>) and any
/// text without a translation simply shows in English. Translations are embedded JSON files
/// (Localization/&lt;code&gt;.json, English → translated). The log always stays in English, for support.
/// </summary>
public static class L
{
    /// <summary>Languages offered in Settings, with their own names.</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"), ("pt", "Português"),
    ];

    /// <summary>
    /// Texts translated where they are shown rather than where they are made (the engine keeps them in English for the
    /// log): settings page names, what a file is, and the engine's states.
    /// </summary>
    public static readonly string[] Implicit =
    [
        "General", "Sync", "Cloud", "Backups", "Updates", "Advanced",
        "Key bindings", "Macros", "Enabled addons", "Window layout", "Chat windows", "Game options", "Edit Mode layouts",
        "Other settings", "Game interface",
        "In sync", "Paused", "Syncing", "Waiting", "Waiting: game running", "World of Warcraft not found",
        "No game version is switched on", "Log can't be written", "Game running", "Uploading settings", "Downloading settings",
        "Game closed, waiting for it to finish writing", "Choose which settings to keep",
        "Settings changed on this PC and in the cloud: choose which to keep", "Paused (upload waiting)", "Paused (push waiting)",
        "Nothing to sync yet: launch this game version once", "Pull undone: files were in use",
        "Start with Windows", "Open at Login",
        "Before downloading settings", "Before keeping the cloud settings", "Before restoring a backup", "Cloud copy, before uploading",
        "The cloud copy you chose not to keep", "Cloud copy, before moving cloud folder",
    ];

    private static Dictionary<string, string> _table = new();

    /// <summary>The language in use ("en", "de", ...).</summary>
    public static string Language { get; private set; } = "en";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Switches language: a code from <see cref="Languages"/>, or null for the computer's language when it is one of
    /// them (English otherwise).
    /// </summary>
    public static void Use(string? code)
    {
        code ??= CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (!Languages.Any(l => l.Code == code)) code = "en";
        Language = code;
        _table = code == "en" ? new() : Load(code) ?? new();
        try
        {
            var ui = CultureInfo.CurrentUICulture;
            Culture = ui.TwoLetterISOLanguageName == code ? ui : CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException) { Culture = CultureInfo.InvariantCulture; }
    }

    /// <summary>The translation of an English text (the text itself when there is none).</summary>
    public static string T(string english) => _table.TryGetValue(english, out var t) && t.Length > 0 ? t : english;

    /// <summary>A translated format string with its values filled in: <c>L.F("Last synced {0}", ago)</c>.</summary>
    public static string F(string englishFormat, params object?[] args) => string.Format(Culture, T(englishFormat), args);

    /// <summary>Singular or plural: <c>L.N(n, "{0} file", "{0} files")</c>.</summary>
    public static string N(int count, string one, string many) => F(count == 1 ? one : many, count);

    /// <summary>Every embedded translation table, for tests.</summary>
    public static Dictionary<string, string>? Load(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"localization/{code}.json");
        if (stream == null) return null;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}
