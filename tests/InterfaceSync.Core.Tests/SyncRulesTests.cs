using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>"Choose what syncs": a computer leaves out characters or addons without affecting the others.</summary>
public sealed class SyncRulesTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    private const string Shared = @"PLAYER#1\bindings-cache.wtf";
    private const string ThrallMacros = @"PLAYER#1\Realm\Thrall\macros-cache.txt";
    private const string ThrallBar = @"PLAYER#1\Realm\Thrall\SavedVariables\Bartender4.lua";
    private const string Weak = @"PLAYER#1\SavedVariables\WeakAuras.lua";

    private static SyncRules LeaveOutThrall => new() { Characters = ["Realm/Thrall"] };

    private (SimulatedPc home, SimulatedPc laptop) Joined()
    {
        var home = _world.Pc("HomePC");
        home.WriteSetting(Shared, "b1");
        home.WriteSetting(ThrallMacros, "home thrall");
        home.WriteSetting(ThrallBar, "home bar");
        home.WriteSetting(Weak, "w1");
        Assert.Equal(SyncOutcome.Pushed, home.Sync.Tick());
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        var laptop = _world.Pc("Laptop");
        Assert.Equal(SyncOutcome.Pulled, laptop.Sync.Tick());
        return (home, laptop);
    }

    [Fact]
    public void Rules_match_characters_and_addons_everywhere()
    {
        var rules = new SyncRules { Characters = ["realm/thrall"], Addons = ["weakauras"] };
        Assert.True(rules.Excludes(ThrallMacros));
        Assert.True(rules.Excludes(ThrallBar));
        Assert.True(rules.Excludes(Weak));
        Assert.True(rules.Excludes(@"PLAYER#1\Realm\Jaina\SavedVariables\WeakAuras.lua.bak"));
        Assert.False(rules.Excludes(Shared));
        Assert.False(rules.Excludes(@"PLAYER#1\Realm\Jaina\macros-cache.txt"));
        Assert.Equal("1 character and 1 addon not synced on this computer", rules.Describe());

        var choices = SyncChoices.From([Shared, ThrallMacros, ThrallBar, Weak, @"PLAYER#1\Realm\Jaina\macros-cache.txt"]);
        Assert.Equal(["Jaina - Realm", "Thrall - Realm"], choices.Characters.Select(c => c.Label));
        Assert.Equal(["Bartender4", "WeakAuras"], choices.Addons);
    }

    [Fact]
    public void A_left_out_character_is_neither_uploaded_downloaded_nor_deleted_and_stays_in_the_cloud()
    {
        var (home, laptop) = Joined();
        laptop.Sync.Rules = LeaveOutThrall;

        // The laptop changes Thrall its own way and something shared: only the shared change goes up.
        Assert.Equal(SyncOutcome.Pushed, laptop.PlaySession((ThrallMacros, "laptop thrall"), (Shared, "b2")));
        Assert.Equal("home thrall", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "Realm", "Thrall", "macros-cache.txt")));
        Assert.Equal("b2", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "bindings-cache.wtf")));
        var commit = CommitFile.TryRead(new Platform.PhysicalFileSystem(), _world.CommitFilePath())!;
        Assert.Contains(commit.Files, f => f.RelativePath.EndsWith("Thrall\\macros-cache.txt"));   // still listed for the others

        // Home still syncs Thrall: it downloads the shared change and keeps its Thrall.
        Assert.Equal(SyncOutcome.Pulled, home.PollAfter(TimeSpan.FromMinutes(5)));
        Assert.Equal("b2", home.ReadSetting(Shared));
        Assert.Equal("home thrall", home.ReadSetting(ThrallMacros));

        // Home changes Thrall: the laptop downloads the upload but keeps its own Thrall.
        Assert.Equal(SyncOutcome.Pushed, home.PlaySession((ThrallMacros, "home thrall 2"), (Shared, "b3")));
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromMinutes(5)));
        Assert.Equal("b3", laptop.ReadSetting(Shared));
        Assert.Equal("laptop thrall", laptop.ReadSetting(ThrallMacros));

        // The laptop deletes Thrall's bar settings: the cloud keeps them.
        File.Delete(Path.Combine(laptop.AccountPath, "PLAYER#1", "Realm", "Thrall", "SavedVariables", "Bartender4.lua"));
        laptop.PlaySession((Shared, "b4"));
        Assert.True(File.Exists(Path.Combine(_world.CloudAccount(), "PLAYER#1", "Realm", "Thrall", "SavedVariables", "Bartender4.lua")));
    }

    [Fact]
    public void Leaving_something_out_does_not_count_as_a_change_to_upload()
    {
        var (_, laptop) = Joined();
        laptop.Sync.Rules = new SyncRules { Addons = ["WeakAuras"] };
        Assert.Equal(SyncOutcome.NothingToDo, laptop.PollAfter(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Switching_a_character_back_on_downloads_the_clouds_copy_after_backing_up_this_one()
    {
        var (home, laptop) = Joined();
        laptop.Sync.Rules = LeaveOutThrall;
        laptop.PlaySession((ThrallMacros, "laptop thrall"));
        home.PlaySession((ThrallMacros, "home thrall 2"));
        laptop.PollAfter(TimeSpan.FromMinutes(5));                       // in step again; Thrall still left out
        Assert.Equal("laptop thrall", laptop.ReadSetting(ThrallMacros));

        // A character only the laptop has is kept and uploaded, never deleted.
        laptop.WriteSetting(@"PLAYER#1\Realm\Thrall\layout-local.txt", "laptop only");

        laptop.Sync.Rules = SyncRules.None;
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromMinutes(1)));
        Assert.Equal("home thrall 2", laptop.ReadSetting(ThrallMacros));
        Assert.Equal("laptop only", laptop.ReadSetting(@"PLAYER#1\Realm\Thrall\layout-local.txt"));
        var backup = laptop.Backups.List("_retail_").First(b => b.Reason.StartsWith("before-sync-again"));
        Assert.Equal("laptop thrall", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "Realm", "Thrall", "macros-cache.txt")));

        Assert.Equal(SyncOutcome.Pushed, laptop.PollAfter(TimeSpan.FromMinutes(1)));
        Assert.Equal("laptop only", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "Realm", "Thrall", "layout-local.txt")));
        Assert.Equal("home thrall 2", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "Realm", "Thrall", "macros-cache.txt")));
        Assert.Equal(SyncOutcome.NothingToDo, laptop.PollAfter(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Switching_back_on_while_a_shared_change_waits_to_upload_keeps_that_change()
    {
        var (_, laptop) = Joined();
        laptop.Sync.Rules = LeaveOutThrall;
        laptop.PlaySession((ThrallMacros, "laptop thrall"));
        laptop.GameRunning = true; laptop.Sync.Tick();
        laptop.WriteSetting(Shared, "laptop shared");
        laptop.GameRunning = false; laptop.Sync.Tick();                  // upload pending

        laptop.Sync.Rules = SyncRules.None;
        _world.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(SyncOutcome.Pulled, laptop.Sync.Tick());           // Thrall back from the cloud
        Assert.Equal("laptop shared", laptop.ReadSetting(Shared));       // untouched
        Assert.Equal(SyncOutcome.Pushed, laptop.PollAfter(TimeSpan.FromMinutes(1)));
        Assert.Equal("laptop shared", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "bindings-cache.wtf")));
        Assert.Equal("home thrall", laptop.ReadSetting(ThrallMacros));
    }

    [Fact]
    public void Switching_back_on_waits_while_the_cloud_has_news_then_happens()
    {
        var (home, laptop) = Joined();
        laptop.Sync.Rules = LeaveOutThrall;
        laptop.PlaySession((ThrallMacros, "laptop thrall"));
        home.PlaySession((Shared, "b9"), (ThrallMacros, "home thrall 9"));

        laptop.Sync.Rules = SyncRules.None;
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromMinutes(5)));   // the normal download first
        Assert.Equal("b9", laptop.ReadSetting(Shared));
        Assert.Equal("laptop thrall", laptop.ReadSetting(ThrallMacros));               // not yet
        Assert.Equal(SyncOutcome.Pulled, laptop.PollAfter(TimeSpan.FromMinutes(1)));   // then Thrall
        Assert.Equal("home thrall 9", laptop.ReadSetting(ThrallMacros));
    }

    [Fact]
    public void Rules_survive_a_restart_through_the_saved_state()
    {
        var (_, laptop) = Joined();
        laptop.Sync.Rules = LeaveOutThrall;
        laptop.PlaySession((ThrallMacros, "laptop thrall"));
        laptop.Restart();
        laptop.Sync.Rules = LeaveOutThrall;                                // the service sets it from settings
        Assert.Equal(SyncOutcome.NothingToDo, laptop.PollAfter(TimeSpan.FromMinutes(1)));
        Assert.NotNull(laptop.Sync.State.AppliedRules);
    }
}
