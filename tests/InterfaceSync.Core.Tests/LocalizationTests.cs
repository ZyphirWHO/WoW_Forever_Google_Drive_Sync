using System.Text;
using System.Text.RegularExpressions;
using InterfaceSync.Core.Localization;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>
/// Every text the code passes to L.T / L.F / L.N (and L.Implicit) has a translation in every language, with the same
/// placeholders, and no translation is left over. Adding a string to the interface without translating it fails here.
/// </summary>
[Collection("Language")]
public sealed class LocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    /// <summary>The English texts used in the code: string literals in the translated argument(s), "a" + "b" joined.</summary>
    internal static SortedSet<string> UsedTexts()
    {
        var root = RepoRoot();
        var files = new[] { "src/InterfaceSync.Core", "src/InterfaceSync.App", "src/InterfaceSync.Mac" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\bL\.([TFN])\("))
            {
                var args = Arguments(text, m.Index + m.Length);
                var translated = m.Groups[1].Value == "N" ? args.Skip(1).Take(2) : args.Take(1);
                foreach (var arg in translated)
                    foreach (var s in Literals(arg))
                        if (s.Length > 0) keys.Add(s);
            }
        }
        foreach (var s in L.Implicit) keys.Add(s);
        return keys;
    }

    private static List<string> Arguments(string text, int start)
    {
        var args = new List<string>();
        int depth = 0, from = start;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++) if (text[i] == '\\') i++;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) { args.Add(text[from..i]); return args; }
                depth--;
            }
            else if (c == ',' && depth == 0) { args.Add(text[from..i]); from = i + 1; }
        }
        return args;
    }

    private static IEnumerable<string> Literals(string expression)
    {
        var result = new List<string>();
        StringBuilder? current = null;
        bool joining = false;
        foreach (Match t in Regex.Matches(expression, @"\$?""(?:[^""\\]|\\.)*""|\+|[^""+\s]+"))
        {
            var v = t.Value;
            if (v == "+") { joining = true; continue; }
            if (v.StartsWith('"'))
            {
                var s = Regex.Unescape(v[1..^1]);
                if (joining && current != null) current.Append(s);
                else { if (current != null) result.Add(current.ToString()); current = new StringBuilder(s); }
            }
            else if (current != null && !joining) { result.Add(current.ToString()); current = null; }
            joining = false;
        }
        if (current != null) result.Add(current.ToString());
        return result;
    }

    private static IEnumerable<string> Placeholders(string s) => Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order();

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("pt")]
    public void Every_text_is_translated_with_the_same_placeholders(string code)
    {
        var used = UsedTexts();
        var table = L.Load(code);
        Assert.NotNull(table);
        var missing = used.Where(k => !table!.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"{code}: no translation for:\n" + string.Join("\n", missing));
        var unused = table!.Keys.Where(k => !used.Contains(k)).ToList();
        Assert.True(unused.Count == 0, $"{code}: translations no longer used:\n" + string.Join("\n", unused));
        foreach (var (en, tr) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(tr), $"{code}: empty translation for {en}");
            Assert.Equal(Placeholders(en), Placeholders(tr));
        }
    }

    [Fact]
    public void Switching_language_changes_the_words_and_unknown_languages_fall_back_to_English()
    {
        try
        {
            L.Use("de");
            Assert.Equal("Jetzt synchronisieren", L.T("Sync now"));
            Assert.Equal("vor 5 Minuten", StatusWords.Ago(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow));
            Assert.Equal("Not in any table", L.T("Not in any table"));
            L.Use("fr");
            Assert.Equal("Tout est synchronisé", StatusWords.Headline(Service.OverallState.InSync));
            L.Use("xx");
            Assert.Equal("en", L.Language);
            Assert.Equal("Sync now", L.T("Sync now"));
        }
        finally { L.Use("en"); }
    }

    [Fact]
    public void The_settings_offer_every_embedded_language()
    {
        foreach (var (code, _) in L.Languages.Where(l => l.Code != "en")) Assert.NotNull(L.Load(code));
    }
}

/// <summary>The language is app-wide, so tests that switch it run on their own, never beside other tests.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;
