using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Service;

namespace InterfaceSync.Core.Updates;

public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);

public sealed record ReleaseInfo(string Version, string Tag, Uri Page, string Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The Windows installer ("InterfaceSync-Setup-3.0.1.exe").</summary>
    public ReleaseAsset? WindowsInstaller => Assets.FirstOrDefault(a => a.Name.Equals($"{ProductInfo.Id}-Setup-{Version}.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Mac disk image for this Mac ("InterfaceSync-3.0.1-macos-arm64.dmg").</summary>
    public ReleaseAsset? MacImage(bool appleSilicon) =>
        Assets.FirstOrDefault(a => a.Name.Equals($"{ProductInfo.Id}-{Version}-macos-{(appleSilicon ? "arm64" : "x64")}.dmg", StringComparison.OrdinalIgnoreCase));
}

public enum UpdateState { NotChecked, Checking, UpToDate, Available, Downloading, Failed }

/// <summary>
/// Looks for a newer release on the project's GitHub Releases page (at most once a day unless asked) and downloads
/// it. Only https downloads from GitHub are accepted, and a file is used only if its SHA-256 matches the hash
/// GitHub publishes for the release asset (or, failing that, the one in the release notes). It sends nothing about
/// the player: an anonymous request with a generic user agent.
/// </summary>
public sealed class Updater
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private readonly SyncService _service;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly string _current;
    private int _busy;

    public Updater(SyncService service, HttpClient http, IClock clock, string? currentVersion = null)
    {
        _service = service;
        _http = http;
        _clock = clock;
        _current = currentVersion ?? ProductInfo.DisplayVersion;
    }

    public UpdateState State { get; private set; } = UpdateState.NotChecked;
    public ReleaseInfo? Available { get; private set; }
    public string? Problem { get; private set; }

    /// <summary>The newest published release's version, or null when the project hasn't published one yet.</summary>
    public string? LatestPublished { get; private set; }

    /// <summary>A CI test build ("3.0.0-dev.44"), not a published release.</summary>
    public bool IsTestBuild => _current.Contains("-dev", StringComparison.OrdinalIgnoreCase);
    public string CurrentVersion => _current;

    /// <summary>Raised (on a background thread) whenever State changes.</summary>
    public event Action? Changed;

    /// <summary>Plain words for the status window.</summary>
    public string Describe() => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.Available => $"Version {Available!.Version} is available (you have {_current}).",
        UpdateState.Downloading => $"Downloading version {Available?.Version}…",
        UpdateState.UpToDate when LatestPublished == null => IsTestBuild
            ? $"You're on test build {_current}. No release has been published on GitHub yet, so there's nothing to update to; " +
              "test builds are downloaded from the project's build page."
            : $"No release has been published on GitHub yet (you have {_current}).",
        UpdateState.UpToDate when IsTestBuild => $"You're on test build {_current}, which is newer than the latest release ({LatestPublished}).",
        UpdateState.UpToDate => $"You have the latest version ({_current}).",
        UpdateState.Failed => $"Couldn't check for updates: {Problem}",
        _ => _service.Settings.CheckForUpdates ? $"Version {_current}. Checks once a day." : $"Version {_current}. Automatic checks are off.",
    };

    /// <summary>With automatic checks on: once when the app starts, then once a day.</summary>
    public Task CheckIfDueAsync()
    {
        var s = _service.Settings;
        if (!s.CheckForUpdates) return Task.CompletedTask;
        if (State != UpdateState.NotChecked && s.LastUpdateCheckUtc is { } last && _clock.UtcNow - last < CheckEvery) return Task.CompletedTask;
        return CheckAsync();
    }

    public async Task CheckAsync(CancellationToken cancel = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Set(UpdateState.Checking);
            var latest = await LatestAsync(cancel).ConfigureAwait(false);
            LatestPublished = latest?.Version;
            _service.SavePreference(s => s.LastUpdateCheckUtc = _clock.UtcNow);
            if (latest != null && IsNewer(latest.Version, _current))
            {
                bool first = Available?.Version != latest.Version;
                Available = latest;
                if (first) _service.Log.Info($"Update available: {ProductInfo.Name} {latest.Version} (this is {_current}).");
                Set(UpdateState.Available);
            }
            else
            {
                Available = null;
                Set(UpdateState.UpToDate);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            Problem = e is TaskCanceledException ? "GitHub didn't answer in time" : e.Message;
            _service.Log.Info($"Update check failed: {Problem}");
            Set(UpdateState.Failed);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>The newest published (non-pre-release) release, or null when the project has none yet.</summary>
    public async Task<ReleaseInfo?> LatestAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ProductInfo.Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(ProductInfo.Id, "updater"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;   // no release published yet
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Parse(json);
    }

    internal static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("release has no tag");
        var version = tag.TrimStart('v', 'V');
        var page = new Uri(root.GetProperty("html_url").GetString() ?? throw new InvalidDataException("release has no page"));
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list))
            foreach (var a in list.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (url == null) continue;
                string? sha = null;
                if (a.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d[7..];
                sha ??= HashFromNotes(notes, name);
                assets.Add(new ReleaseAsset(name, new Uri(url), a.TryGetProperty("size", out var size) ? size.GetInt64() : 0, sha?.ToLowerInvariant()));
            }
        return new ReleaseInfo(version, tag, page, notes, assets);
    }

    /// <summary>The release notes list "| InterfaceSync-Setup-3.0.1.exe ... | `HASH` |".</summary>
    internal static string? HashFromNotes(string notes, string asset)
    {
        foreach (var line in notes.Split('\n'))
            if (line.Contains(asset, StringComparison.OrdinalIgnoreCase) && Regex.Match(line, @"\b[0-9a-fA-F]{64}\b") is { Success: true } m)
                return m.Value;
        return null;
    }

    /// <summary>"3.0.1" &gt; "3.0.0" &gt; "3.0.0-dev.42" &gt; "2.9.9". Unparseable versions never count as newer.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version core, string? pre)? Split(string v)
        {
            var plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-');
            var core = dash >= 0 ? v[..dash] : v;
            var pre = dash >= 0 ? v[(dash + 1)..] : null;
            return System.Version.TryParse(core.Count(c => c == '.') == 0 ? core + ".0" : core, out var parsed) ? (Normalize(parsed), pre) : null;
        }
        static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        if (Split(candidate) is not { } c || Split(current) is not { } k) return false;
        var byCore = c.core.CompareTo(k.core);
        if (byCore != 0) return byCore > 0;
        if (c.pre == null) return k.pre != null;          // 3.0.0 is newer than 3.0.0-dev.42
        if (k.pre == null) return false;
        return ComparePre(c.pre, k.pre) > 0;
    }

    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            if (i >= x.Length) return -1;
            if (i >= y.Length) return 1;
            bool nx = int.TryParse(x[i], out var ix), ny = int.TryParse(y[i], out var iy);
            int cmp = nx && ny ? ix.CompareTo(iy) : string.CompareOrdinal(x[i], y[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>
    /// Downloads an asset of the available release into <paramref name="folder"/> and checks its SHA-256.
    /// Returns the file's path; throws if the download isn't from GitHub over https or the hash doesn't match.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, string folder, CancellationToken cancel = default)
    {
        if (asset.Url.Scheme != Uri.UriSchemeHttps || !IsGitHubHost(asset.Url.Host))
            throw new InvalidDataException($"Refusing to download from {asset.Url.Host}: updates only come from GitHub.");
        if (asset.Sha256 is not { Length: 64 } expected)
            throw new InvalidDataException("The release doesn't publish a SHA-256 for this file, so it can't be checked.");
        Set(UpdateState.Downloading);
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(asset.Name));
            var partial = target + ".download";
            using (var request = new HttpRequestMessage(HttpMethod.Get, asset.Url))
            {
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue(ProductInfo.Id, "updater"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for {asset.Name}");
                if (response.RequestMessage?.RequestUri is { } final && (final.Scheme != Uri.UriSchemeHttps || !IsGitHubHost(final.Host)))
                    throw new InvalidDataException($"The download was redirected to {final.Host}, which isn't GitHub.");
                await using var file = File.Create(partial);
                await response.Content.CopyToAsync(file, cancel).ConfigureAwait(false);
            }
            string actual;
            await using (var read = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(read, cancel).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != expected)
            {
                File.Delete(partial);
                throw new InvalidDataException($"{asset.Name} didn't match its published SHA-256, so it was deleted.");
            }
            File.Move(partial, target, overwrite: true);
            _service.Log.Info($"Downloaded {asset.Name} ({asset.Size:N0} bytes, SHA-256 checked).");
            Set(UpdateState.Available);
            return target;
        }
        catch
        {
            Set(UpdateState.Available);
            throw;
        }
    }

    internal static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private void Set(UpdateState state)
    {
        State = state;
        try { Changed?.Invoke(); } catch (Exception e) { _service.Log.Warn($"Update status refresh failed: {e.Message}"); }
    }
}
