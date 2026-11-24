using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class HelpTests
{
    [Fact]
    public void The_running_versions_release_notes_are_embedded()
    {
        var notes = ReleaseNotes.Current;
        Assert.NotNull(notes);
        Assert.Contains($"What's new in {string.Join('.', ReleaseNotes.BaseVersion(ProductInfo.DisplayVersion).Split('.').Take(2))}", notes);
        Assert.NotNull(ReleaseNotes.For("3.0.0"));
        Assert.Null(ReleaseNotes.For("0.0.1"));
        Assert.Equal("3.1.0", ReleaseNotes.BaseVersion("3.1.0-dev.57"));
    }

    [Fact]
    public void Markdown_becomes_readable_text()
    {
        var text = ReleaseNotes.ToPlainText("## What's new\n\n- **Bold.** A line\n  that wraps, with `code`.\n- [Link](https://x)\n");
        Assert.Equal($"What's new{Environment.NewLine}{Environment.NewLine}• Bold. A line that wraps, with code.{Environment.NewLine}• Link", text);
    }

    [Fact]
    public void Redaction_replaces_paths_longest_first_and_names_as_whole_words()
    {
        var text = @"Data: C:\Users\Sam\AppData\Local\InterfaceSync; drive G:\My Drive; Sam on SAMS-PC, pushed by SAMS-PC-2; Samuel";
        var redacted = Feedback.Redact(text,
            [(@"C:\Users\Sam", "~"), (@"C:\Users\Sam\AppData\Local", "%LOCALAPPDATA%"), (@"G:\My Drive", "<cloud>")],
            [("SAMS-PC", "<this computer>"), ("Sam", "<user>")]);
        Assert.Equal(@"Data: %LOCALAPPDATA%\InterfaceSync; drive <cloud>; <user> on <this computer>, pushed by SAMS-PC-2; Samuel", redacted);
        Assert.Equal("/Users/<user>", Feedback.Redact("/Users/Sam", [], [("Sam", "<user>")]));
        Assert.Equal("~/x", Feedback.Redact("/Users/Sam/x", [("/Users/Sam", "~")], []));
    }

    [Fact]
    public void Issue_link_is_prefilled_and_short_enough_for_github()
    {
        var url = Feedback.IssueUrl(new string('x', 20_000)).ToString();
        Assert.StartsWith($"https://github.com/{ProductInfo.Repository}/issues/new?title=", url);
        Assert.True(url.Length < 8000, $"{url.Length} characters");
        Assert.Contains("Diagnostics", Uri.UnescapeDataString(url));
    }

    [Theory]
    [InlineData(NoticeKind.Conflict, NotificationLevel.Important, true)]
    [InlineData(NoticeKind.Downloaded, NotificationLevel.Important, false)]
    [InlineData(NoticeKind.Downloaded, NotificationLevel.All, true)]
    [InlineData(NoticeKind.SyncFailed, NotificationLevel.Off, false)]
    public void Notification_setting_decides_which_pop_ups_show(NoticeKind kind, NotificationLevel level, bool shown) =>
        Assert.Equal(shown, SyncService.ShouldShow(kind, level));
}
