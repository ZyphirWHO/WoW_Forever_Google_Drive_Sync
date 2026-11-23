using System.Net;
using System.Security.Cryptography;
using System.Text;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Tests.Fakes;
using InterfaceSync.Core.Updates;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class UpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "is-updater", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    /// <summary>Answers GitHub's API and downloads from a table of URL → response.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{url} UA={request.Headers.UserAgent}");
            var response = Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static string Latest => $"https://api.github.com/repos/{ProductInfo.Repository}/releases/latest";

    private static string ReleaseJson(string tag, string assetName, string url, string? digest, bool prerelease = false, string notes = "") =>
        $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/{{ProductInfo.Repository}}/releases/tag/{{tag}}", "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}}, "body": {{System.Text.Json.JsonSerializer.Serialize(notes)}},
          "assets": [ { "name": "{{assetName}}", "browser_download_url": "{{url}}", "size": 5{{(digest != null ? $", \"digest\": \"sha256:{digest}\"" : "")}} } ] }
        """;

    private (Updater updater, FakeGitHub github, SyncService service) Make(string current = "3.0.0")
    {
        var env = new FakeEnvironment { LocalAppData = Path.Combine(_root, "Local"), UserProfile = Path.Combine(_root, "User"), ProgramData = Path.Combine(_root, "PD") };
        var fs = new PhysicalFileSystem();
        var service = new SyncService(fs, new FakeRegistry(), new FakeProcessList(), env, _clock, new MemoryLog(), new AppPaths(env.LocalAppData), "PC");
        var github = new FakeGitHub();
        return (new Updater(service, new HttpClient(github), _clock, current), github, service);
    }

    [Theory]
    [InlineData("3.0.1", "3.0.0", true)]
    [InlineData("3.1.0", "3.0.9", true)]
    [InlineData("3.0.0", "3.0.0-dev.42", true)]       // a release beats the dev builds before it
    [InlineData("3.0.0", "3.0.0", false)]
    [InlineData("3.0.0", "3.0.1", false)]
    [InlineData("2.9.9", "3.0.0-dev.1", false)]
    [InlineData("3.0.0-beta.2", "3.0.0-beta.1", true)]
    [InlineData("3.0.0-beta.10", "3.0.0-beta.9", true)]
    [InlineData("nonsense", "3.0.0", false)]
    public void Versions_compare_like_releases(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updater.IsNewer(candidate, current));

    [Fact]
    public async Task A_newer_release_is_offered_and_the_check_is_recorded()
    {
        var (updater, github, service) = Make();
        github.Routes[Latest] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReleaseJson("v3.0.1", "InterfaceSync-Setup-3.0.1.exe", "https://github.com/x/y/releases/download/v3.0.1/InterfaceSync-Setup-3.0.1.exe", new string('a', 64))) };
        await updater.CheckAsync();
        Assert.Equal(UpdateState.Available, updater.State);
        Assert.Equal("3.0.1", updater.Available!.Version);
        Assert.NotNull(updater.Available.WindowsInstaller);
        Assert.Equal(_clock.UtcNow, service.Settings.LastUpdateCheckUtc);
        Assert.Contains("is available", updater.Describe());
        // Anonymous: only the product's own user agent, nothing about the player or PC.
        Assert.All(github.Requests, r => Assert.EndsWith($"UA={ProductInfo.Id}/updater", r));
    }

    [Fact]
    public async Task No_release_yet_or_the_same_version_means_up_to_date()
    {
        var (updater, github, _) = Make();
        await updater.CheckAsync();                                  // 404: nothing published yet
        Assert.Equal(UpdateState.UpToDate, updater.State);
        Assert.Contains("No release has been published", updater.Describe());   // not "you have the latest"
        Assert.DoesNotContain("latest version", updater.Describe());
        github.Routes[Latest] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReleaseJson("v3.0.0", "InterfaceSync-Setup-3.0.0.exe", "https://github.com/x", null)) };
        await updater.CheckAsync();
        Assert.Equal(UpdateState.UpToDate, updater.State);
        Assert.Null(updater.Available);
    }

    [Fact]
    public async Task A_test_build_says_so_instead_of_claiming_to_be_the_latest_version()
    {
        var (updater, github, _) = Make("3.0.0-dev.44");
        await updater.CheckAsync();
        Assert.Contains("test build 3.0.0-dev.44", updater.Describe());
        Assert.Contains("No release has been published", updater.Describe());
        github.Routes[Latest] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReleaseJson("v2.9.0", "InterfaceSync-Setup-2.9.0.exe", "https://github.com/x", null)) };
        await updater.CheckAsync();
        Assert.Contains("newer than the latest release (2.9.0)", updater.Describe());
    }

    [Fact]
    public async Task Pre_releases_are_not_offered_and_failures_are_reported_plainly()
    {
        var (updater, github, _) = Make();
        github.Routes[Latest] = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReleaseJson("v3.1.0-beta.1", "x.exe", "https://github.com/x", null, prerelease: true)) };
        await updater.CheckAsync();
        Assert.Equal(UpdateState.UpToDate, updater.State);
        github.Routes[Latest] = () => new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = "rate limit exceeded" };
        await updater.CheckAsync();
        Assert.Equal(UpdateState.Failed, updater.State);
        Assert.Contains("403", updater.Describe());
    }

    [Fact]
    public async Task Automatic_checks_run_at_start_then_once_a_day_and_not_when_switched_off()
    {
        var (updater, github, service) = Make();
        await updater.CheckIfDueAsync();
        Assert.Single(github.Requests);
        await updater.CheckIfDueAsync();
        Assert.Single(github.Requests);                              // checked minutes ago
        _clock.Advance(TimeSpan.FromHours(25));
        await updater.CheckIfDueAsync();
        Assert.Equal(2, github.Requests.Count);
        service.SavePreference(s => s.CheckForUpdates = false);
        _clock.Advance(TimeSpan.FromHours(25));
        await updater.CheckIfDueAsync();
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_download_is_kept_only_when_its_SHA256_matches()
    {
        var bytes = Encoding.UTF8.GetBytes("installer bytes");
        var good = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        const string url = "https://github.com/o/r/releases/download/v3.0.1/InterfaceSync-Setup-3.0.1.exe";
        var (updater, github, _) = Make();
        github.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        var folder = Path.Combine(_root, "Updates");

        var path = await updater.DownloadAsync(new ReleaseAsset("InterfaceSync-Setup-3.0.1.exe", new Uri(url), bytes.Length, good), folder);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        var bad = await Assert.ThrowsAsync<InvalidDataException>(() =>
            updater.DownloadAsync(new ReleaseAsset("InterfaceSync-Setup-3.0.1.exe", new Uri(url), bytes.Length, new string('0', 64)), Path.Combine(_root, "Other")));
        Assert.Contains("didn't match", bad.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Other")));   // nothing left behind
    }

    [Fact]
    public async Task Downloads_must_come_from_GitHub_over_https_with_a_published_hash()
    {
        var (updater, _, _) = Make();
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(new ReleaseAsset("a.exe", new Uri("https://example.com/a.exe"), 1, new string('a', 64)), _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(new ReleaseAsset("a.exe", new Uri("http://github.com/a.exe"), 1, new string('a', 64)), _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(new ReleaseAsset("a.exe", new Uri("https://github.com/a.exe"), 1, null), _root));
    }

    [Fact]
    public void Without_GitHubs_digest_the_hash_comes_from_the_release_notes_table()
    {
        var hash = new string('b', 64);
        var json = ReleaseJson("v3.0.1", "InterfaceSync-Setup-3.0.1.exe", "https://github.com/x", null,
            notes: $"| File | SHA-256 |\n|---|---|\n| InterfaceSync-Setup-3.0.1.exe (installer, recommended) | `{hash.ToUpperInvariant()}` |\n");
        var release = Updater.Parse(json)!;
        Assert.Equal(hash, release.WindowsInstaller!.Sha256);
    }
}
