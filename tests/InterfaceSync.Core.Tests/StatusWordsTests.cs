using InterfaceSync.Core;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Sync;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class StatusWordsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(5, "5 minutes ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(150, "2 hours ago")]
    [InlineData(60 * 24 * 3, "3 days ago")]
    public void Ago_reads_naturally(int minutes, string expected) =>
        Assert.Equal(expected, StatusWords.Ago(Now.AddMinutes(-minutes), Now));

    [Fact]
    public void Ago_handles_never_and_old_dates()
    {
        Assert.Equal("never", StatusWords.Ago(null, Now));
        Assert.Matches(@"\d+ \w{3} 2026", StatusWords.Ago(Now.AddDays(-30), Now));
    }

    [Fact]
    public void Every_state_has_a_tone_and_headline()
    {
        foreach (var s in Enum.GetValues<OverallState>()) Assert.False(string.IsNullOrEmpty(StatusWords.Headline(s)));
        Assert.Equal(StatusTone.Attention, StatusWords.Tone(SyncStatus.Error));
        Assert.Equal(StatusTone.Attention, StatusWords.Tone(OverallState.NeedsAttention));
        Assert.Equal(StatusTone.Good, StatusWords.Tone(OverallState.InSync));
        Assert.Equal(StatusTone.Waiting, StatusWords.Tone(SyncStatus.GameRunning));
    }
}
