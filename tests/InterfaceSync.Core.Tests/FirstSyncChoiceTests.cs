using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>Setup asks, per version, whether to use the cloud's settings or this computer's when the cloud already has some.</summary>
public sealed class FirstSyncChoiceTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    private (SimulatedPc home, SimulatedPc laptop) CloudFromHomeThenLaptop(bool laptopNewer)
    {
        var laptop = _world.Pc("Laptop");
        if (!laptopNewer) laptop.WriteSetting(@"PLAYER#1\macros-cache.txt", "laptop's own");
        _world.Clock.Advance(TimeSpan.FromHours(1));
        var home = _world.Pc("HomePC");
        home.WriteSetting(@"PLAYER#1\macros-cache.txt", "home's");
        Assert.Equal(SyncOutcome.Pushed, home.Sync.Tick());
        _world.Clock.Advance(TimeSpan.FromHours(1));
        if (laptopNewer) laptop.WriteSetting(@"PLAYER#1\macros-cache.txt", "laptop's own");
        return (home, laptop);
    }

    [Fact]
    public void Preview_shows_where_the_cloud_copy_came_from_and_suggests_the_newer_one_without_writing()
    {
        var (_, laptop) = CloudFromHomeThenLaptop(laptopNewer: false);
        var before = Directory.GetFiles(_world.MyDrive, "*", SearchOption.AllDirectories).Length;

        var p = laptop.Sync.Preview();

        Assert.True(p.NeedsChoice);
        Assert.Equal("HomePC", p.CloudFrom);
        Assert.Equal(FirstSyncChoice.UseCloud, p.Suggested);
        Assert.Contains("from HomePC", p.CloudOptionText());
        Assert.StartsWith("Use this PC's settings", p.LocalOptionText());
        Assert.StartsWith("Use this Mac's settings", p.LocalOptionText("Mac"));
        Assert.Equal(before, Directory.GetFiles(_world.MyDrive, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(FirstSyncChoice.UseThisComputer, CloudFromHomeThenLaptop2().Sync.Preview().Suggested);
    }

    private SimulatedPc CloudFromHomeThenLaptop2()
    {
        var world = _world;
        _world.Clock.Advance(TimeSpan.FromHours(1));
        var other = world.Pc("Desktop");
        other.WriteSetting(@"PLAYER#1\macros-cache.txt", "newest of all");
        return other;
    }

    [Fact]
    public void Choosing_this_computer_uploads_it_even_though_the_cloud_copy_is_newer()
    {
        var (_, laptop) = CloudFromHomeThenLaptop(laptopNewer: false);
        laptop.Sync.FirstSyncChoice = FirstSyncChoice.UseThisComputer;

        Assert.Equal(SyncOutcome.Pushed, laptop.Sync.Tick());
        Assert.Equal("laptop's own", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.Contains(laptop.Backups.List("_retail_"), b => b.Reason == "cloud-before-push-changes");   // the cloud's were backed up
    }

    [Fact]
    public void Choosing_the_cloud_downloads_it_even_though_this_computer_is_newer()
    {
        var (_, laptop) = CloudFromHomeThenLaptop(laptopNewer: true);
        laptop.Sync.FirstSyncChoice = FirstSyncChoice.UseCloud;

        Assert.Equal(SyncOutcome.Pulled, laptop.Sync.Tick());                                       // no question: already answered
        Assert.Equal("home's", laptop.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Contains(laptop.Backups.List("_retail_"), b => b.Reason == "first-join-changes");
    }

    [Fact]
    public void Automatic_still_asks_when_this_computer_is_newer()
    {
        var (_, laptop) = CloudFromHomeThenLaptop(laptopNewer: true);
        Assert.Equal(SyncOutcome.Conflict, laptop.Sync.Tick());
    }
}
