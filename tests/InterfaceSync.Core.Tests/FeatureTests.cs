using InterfaceSync.Core.Backup;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>3.1 features: activity timeline, snapshots, game-update copies, computers list, timed pause.</summary>
public sealed class FeatureTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    // ---------------------------------------------------------------- what a file is

    [Theory]
    [InlineData(@"PLAYER#1\SavedVariables\Details.lua", "Details", null, "Details")]
    [InlineData(@"PLAYER#1\SavedVariables\WeakAuras.lua.bak", "WeakAuras", null, "WeakAuras")]
    [InlineData(@"PLAYER#1\Silvermoon\Thrall\SavedVariables\Bartender4.lua", "Bartender4", "Thrall - Silvermoon", "Bartender4")]
    [InlineData(@"PLAYER#1\SavedVariables\Blizzard_Console.lua", "Game interface", null, null)]
    [InlineData(@"PLAYER#1\bindings-cache.wtf", "Key bindings", null, null)]
    [InlineData(@"PLAYER#1\Silvermoon\Thrall\macros-cache.txt", "Macros", "Thrall - Silvermoon", null)]
    [InlineData(@"PLAYER#1\Silvermoon\Thrall\AddOns.txt", "Enabled addons", "Thrall - Silvermoon", null)]
    [InlineData(@"PLAYER#1\Silvermoon\Thrall\edit-mode-cache-character.txt", "Edit Mode layouts", "Thrall - Silvermoon", null)]
    [InlineData(@"PLAYER#1\something.dat", "Other settings", null, null)]
    public void Settings_files_are_described_in_plain_words(string path, string area, string? character, string? addon)
    {
        var item = SettingsItem.Parse(path);
        Assert.Equal(area, item.Area);
        Assert.Equal(character, item.CharacterLabel);
        Assert.Equal(addon, item.Addon);
        Assert.Equal("PLAYER#1", item.Account);
    }

    [Fact]
    public void Summary_names_the_most_changed_areas_first_and_counts_the_rest()
    {
        var files = new[]
        {
            @"A\SavedVariables\Details.lua", @"A\R\C1\SavedVariables\Details.lua", @"A\R\C2\SavedVariables\Details.lua",
            @"A\bindings-cache.wtf", @"A\SavedVariables\WeakAuras.lua", @"A\SavedVariables\Plater.lua", @"A\R\C1\macros-cache.txt",
        };
        Assert.Equal("Details, Key bindings, Macros and 2 more", SettingsItem.Summarize(files));
        Assert.Equal("Details and Key bindings", SettingsItem.Summarize(files.Take(4)));
        Assert.Equal("Details", SettingsItem.Summarize(files.Take(1)));
    }

    // ---------------------------------------------------------------- activity

    [Fact]
    public void Uploads_downloads_and_restores_are_recorded_with_what_changed()
    {
        var a = _world.Pc("HomePC");
        var events = new List<ActivityEntry>();
        a.Sync.Activity += events.Add;
        a.WriteSetting(@"PLAYER#1\SavedVariables\Details.lua", "d1");
        a.WriteSetting(@"PLAYER#1\bindings-cache.wtf", "b1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        var up = Assert.Single(events);
        Assert.Equal(ActivityKind.Uploaded, up.Kind);
        Assert.Equal("Uploaded Details and Key bindings", up.Headline);
        Assert.Equal(2, up.FileCount);

        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        var b = _world.Pc("WorkLaptop");
        var bEvents = new List<ActivityEntry>();
        b.Sync.Activity += bEvents.Add;
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        Assert.Equal("Downloaded Details and Key bindings from HomePC", Assert.Single(bEvents).Headline);

        // Only the changed file is listed next time.
        events.Clear();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\SavedVariables\Details.lua", "d2")));
        Assert.Equal("Uploaded Details", Assert.Single(events).Headline);

        // Nothing changed: nothing recorded.
        events.Clear();
        a.PollAfter(TimeSpan.FromMinutes(1));
        Assert.Empty(events);
    }

    [Fact]
    public void Activity_store_keeps_the_newest_300_and_survives_a_restart()
    {
        var folder = Path.Combine(_world.Root, "data");
        var store = new ActivityStore(new PhysicalFileSystem(), folder);
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 305; i++)
            store.Add(new ActivityEntry { Utc = _world.Clock.UtcNow.AddMinutes(i), Version = "_retail_", Kind = ActivityKind.Uploaded, Summary = $"n{i}" });
        var reread = new ActivityStore(new PhysicalFileSystem(), folder).List();
        Assert.Equal(ActivityStore.MaxEntries, reread.Count);
        Assert.Equal("n304", reread[0].Summary);
        Assert.Equal("n5", reread[^1].Summary);
    }

    [Fact]
    public void Day_headings_read_today_yesterday_then_the_date()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("Today", ActivityStore.DayHeading(now.AddHours(-1), now));
        Assert.Equal("Yesterday", ActivityStore.DayHeading(now.AddDays(-1), now));
        Assert.Contains("November", ActivityStore.DayHeading(now.AddDays(-3), now));
    }

    // ---------------------------------------------------------------- snapshots

    [Fact]
    public void Snapshots_are_never_pruned_and_do_not_use_up_the_5_backups()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "raid");
        var snap = a.Sync.SaveSnapshot("Raid UI: final!");
        Assert.NotNull(snap);
        Assert.True(snap!.Pinned);
        Assert.Equal("Raid UI final", snap.SnapshotName);
        Assert.Equal("Snapshot: Raid UI final", snap.Description);

        for (int i = 0; i < 8; i++)
        {
            _world.Clock.Advance(TimeSpan.FromMinutes(1));
            a.WriteSetting(@"PLAYER#1\macros-cache.txt", $"v{i}");
            a.Backups.Create("_retail_", a.AccountPath, "before-pull");
        }
        var all = a.Backups.List("_retail_");
        Assert.Contains(all, b => b.Pinned);
        Assert.Equal(BackupStore.KeepPerVersion, all.Count(b => !b.Pinned));

        // Restoring the snapshot brings the whole folder back.
        a.WriteSetting(@"PLAYER#1\extra.txt", "x");
        a.Sync.RestoreBackup(all.Single(b => b.Pinned));
        Assert.Equal("raid", a.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Null(a.ReadSetting(@"PLAYER#1\extra.txt"));

        a.Backups.Delete(all.Single(b => b.Pinned));
        Assert.DoesNotContain(a.Backups.List("_retail_"), b => b.Pinned);
        Assert.Throws<InvalidOperationException>(() => a.Backups.Delete(a.Backups.List("_retail_")[0]));
    }

    [Fact]
    public void A_snapshot_needs_a_name()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "m");
        Assert.Throws<ArgumentException>(() => a.Sync.SaveSnapshot("  ?? "));
        Assert.Equal(BackupStore.MaxSnapshotNameLength, BackupStore.CleanSnapshotName(new string('a', 80)).Length);
    }

    [Fact]
    public void A_game_update_saves_a_copy_of_the_settings_once_and_keeps_the_newest_3()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "before patch");
        Assert.Null(a.Sync.NoteClientVersion("11.0.5.1"));             // first time seen: nothing to compare with
        Assert.Null(a.Sync.NoteClientVersion("11.0.5.1"));             // same version: nothing
        var copy = a.Sync.NoteClientVersion("11.0.7.2");
        Assert.NotNull(copy);
        Assert.True(copy!.GameUpdate);
        Assert.Equal("Before game update 11.0.7.2", copy.Description);
        Assert.False(copy.Partial);

        a.Restart();                                                   // remembered across restarts
        Assert.Null(a.Sync.NoteClientVersion("11.0.7.2"));

        foreach (var v in new[] { "11.1.0.3", "11.1.5.4", "11.2.0.5" })
        {
            _world.Clock.Advance(TimeSpan.FromDays(7));
            Assert.NotNull(a.Sync.NoteClientVersion(v));
        }
        var updates = a.Backups.List("_retail_").Where(b => b.GameUpdate).ToList();
        Assert.Equal(BackupStore.KeepGameUpdates, updates.Count);
        Assert.DoesNotContain(updates, b => b.Description.Contains("11.0.7.2"));
    }

    [Fact]
    public void Game_start_is_reported_once_per_launch()
    {
        var a = _world.Pc("HomePC");
        int started = 0;
        a.Sync.GameStarted += _ => started++;
        a.GameRunning = true;
        a.Sync.Tick();
        a.Sync.Tick();
        a.GameRunning = false;
        a.Sync.Tick();
        a.GameRunning = true;
        a.Sync.Tick();
        Assert.Equal(2, started);
    }

    // ---------------------------------------------------------------- computers

    [Fact]
    public void Each_computer_leaves_a_small_file_after_it_syncs_and_the_list_merges_them()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "m");
        a.Sync.Tick();
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        var b = _world.Pc("Work Laptop");
        b.Sync.Tick();

        var folder = Devices.FolderFor(a.Sync.Cloud);
        Assert.True(File.Exists(Path.Combine(folder, "HomePC.json")));
        Assert.True(File.Exists(Path.Combine(folder, "Work_Laptop.json")));
        File.WriteAllText(Path.Combine(folder, "HomePC (1).json"), "{ \"name\": \"Ghost\" }");   // a Drive conflict copy

        var list = Devices.Read(new PhysicalFileSystem(), a.Sync.Cloud);
        Assert.Equal(2, list.Count);
        var merged = Devices.Merge([("retail", list), ("forever", list.Where(d => d.Name == "HomePC"))], "Work Laptop");
        Assert.True(merged[0].IsThisComputer);
        Assert.Equal("Work Laptop", merged[0].Name);
        Assert.Equal("Downloaded", merged[0].LastAction);
        Assert.Equal(["retail", "forever"], merged.Single(d => d.Name == "HomePC").Versions);

        // The settings folder in the cloud is untouched by these files.
        Assert.False(Directory.Exists(Path.Combine(_world.CloudAccount(), Devices.FolderName)));
    }

    // ---------------------------------------------------------------- pause

    [Fact]
    public void Tomorrow_morning_is_the_next_6am_at_least_an_hour_away()
    {
        var evening = new DateTime(2026, 11, 20, 22, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Local), StatusWords.TomorrowMorning(evening).ToLocalTime());
        var night = new DateTime(2026, 11, 21, 2, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Local), StatusWords.TomorrowMorning(night).ToLocalTime());
        var early = new DateTime(2026, 11, 21, 5, 30, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Local), StatusWords.TomorrowMorning(early).ToLocalTime());
        Assert.Equal(4, StatusWords.PauseChoices(evening).Count);
    }

    [Fact]
    public void Tooltip_fits_the_windows_limit()
    {
        var now = DateTime.UtcNow;
        Assert.Equal($"{ProductInfo.Name}: In sync · synced 5 minutes ago", StatusWords.Tooltip("In sync", now.AddMinutes(-5), now));
        Assert.True(StatusWords.Tooltip(new string('x', 100), now, now).Length <= 63);
    }

    [Fact]
    public void Uploads_and_downloads_report_their_progress_and_clear_it_when_done()
    {
        var a = _world.Pc("HomePC");
        for (int i = 0; i < 5; i++) a.WriteSetting($@"PLAYER#1\SavedVariables\Addon{i}.lua", "x");
        var seen = new List<SyncProgress?>();
        a.Sync.ProgressChanged += w => seen.Add(w.Progress);
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        Assert.Contains(seen, p => p is { Action: "Uploading", Total: 5 });
        Assert.Null(seen[^1]);
        Assert.Null(a.Sync.Progress);
        Assert.Null(StatusWords.ProgressText(a.Sync));

        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        var b = _world.Pc("Laptop");
        var bSeen = new List<SyncProgress?>();
        b.Sync.ProgressChanged += w => bSeen.Add(w.Progress);
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        Assert.Contains(bSeen, p => p is { Action: "Downloading", Total: 5 });
        Assert.Null(b.Sync.Progress);
    }
}
