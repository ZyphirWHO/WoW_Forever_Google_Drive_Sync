using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class SyncEngineTests : IDisposable
{
    private readonly SimWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    /// <summary>A has settings and pushes first; B joins and pulls.</summary>
    private (SimulatedPc a, SimulatedPc b) TwoJoinedPcs(ScriptedFileSystem? fsA = null, ScriptedFileSystem? fsB = null)
    {
        var a = _world.Pc("HomePC", fs: fsA);
        a.WriteSetting(@"PLAYER#1\bindings-cache.wtf", "A1");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        _world.Clock.Advance(FiveMinutes);
        var b = _world.Pc("WorkLaptop", fs: fsB);
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        return (a, b);
    }

    // ---------------------------------------------------------------- push after close

    [Fact]
    public void Push_happens_only_after_the_game_closes_and_the_10_second_wait()
    {
        var (a, _) = TwoJoinedPcs();

        a.GameRunning = true;
        Assert.Equal(SyncOutcome.Waiting, a.Sync.Tick());
        Assert.Equal(SyncStatus.GameRunning, a.Sync.Status);
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M2");

        a.GameRunning = false;
        Assert.Equal(SyncOutcome.Waiting, a.Sync.Tick());       // exit seen
        Assert.True(a.Sync.State.PendingPush);
        _world.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SyncOutcome.Waiting, a.Sync.Tick());       // still inside the 10 s wait
        Assert.Equal("M1", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));

        _world.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        Assert.Equal("M2", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.False(a.Sync.State.PendingPush);
    }

    [Fact]
    public void Game_restarting_during_the_wait_postpones_the_push()
    {
        var (a, _) = TwoJoinedPcs();
        a.GameRunning = true; a.Sync.Tick();
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M2");
        a.GameRunning = false; a.Sync.Tick();
        _world.Clock.Advance(TimeSpan.FromSeconds(5));
        a.GameRunning = true;                                     // relaunched within 10 s
        _world.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(SyncOutcome.Waiting, a.Sync.Tick());
        Assert.Equal("M1", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void Pending_push_survives_a_reboot()
    {
        var (a, _) = TwoJoinedPcs();
        a.GameRunning = true; a.Sync.Tick();
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M2");
        a.GameRunning = false; a.Sync.Tick();                    // exit seen, flag saved

        a.Restart();                                              // PC reboots before the push
        Assert.True(a.Sync.State.PendingPush);
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        Assert.Equal("M2", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void Failed_push_keeps_the_pending_flag_and_retries_on_the_next_poll()
    {
        var fs = new ScriptedFileSystem();
        var (a, _) = TwoJoinedPcs(fsA: fs);
        fs.FailCopy = (_, dest) => dest.StartsWith(_world.MyDrive);

        Assert.Equal(SyncOutcome.Failed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));
        Assert.True(a.Sync.State.PendingPush);
        Assert.Equal(SyncStatus.Error, a.Sync.Status);
        Assert.Single(a.Log.Lines, l => l.Message.Contains("sync failed"));
        Assert.Equal(SyncOutcome.Failed, a.PollAfter(TimeSpan.FromSeconds(15)));
        Assert.Single(a.Log.Lines, l => l.Message.Contains("sync failed")); // not logged again every poll

        fs.FailCopy = null;
        Assert.Equal(SyncOutcome.Pushed, a.PollAfter(TimeSpan.FromSeconds(15)));
        Assert.False(a.Sync.State.PendingPush);
        Assert.Null(a.Sync.State.LastError);
    }

    [Fact]
    public void Nothing_changed_while_playing_means_no_push()
    {
        var (a, _) = TwoJoinedPcs();
        var commitBefore = File.ReadAllText(_world.CommitFilePath());
        Assert.Equal(SyncOutcome.NothingToDo, a.PlaySession());
        Assert.Equal(commitBefore, File.ReadAllText(_world.CommitFilePath()));
        Assert.False(a.Sync.State.PendingPush);
    }

    // ---------------------------------------------------------------- pull rules

    [Fact]
    public void Other_pc_pulls_when_the_cloud_is_clearly_newer()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));

        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(FiveMinutes));
        Assert.Equal("M2", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal(SyncOutcome.NothingToDo, b.PollAfter(FiveMinutes));
        Assert.True(b.Backups.List("_retail_").Count >= 1);
    }

    [Fact]
    public void Cloud_change_that_is_not_two_minutes_newer_asks_the_player_instead_of_guessing()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, b.PlaySession((@"PLAYER#1\macros-cache.txt", "B-latest")));
        Assert.Equal(SyncOutcome.Pulled, a.PollAfter(FiveMinutes));

        // A's next write is stamped only 1 minute after B's newest file (clock skew between PCs).
        var bNewest = b.Sync.State.LocalManifest!.NewestUtc!.Value;
        _world.Clock.UtcNow = bNewest + TimeSpan.FromMinutes(1);
        a.WriteSetting(@"PLAYER#1\bindings-cache.wtf", "A-skewed");
        Assert.Equal(SyncOutcome.Pushed, a.Sync.SyncNow());

        _world.Clock.UtcNow = bNewest + FiveMinutes;
        Assert.Equal(SyncOutcome.Conflict, b.Sync.Tick());
        Assert.Equal("B-latest", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("A1", b.ReadSetting(@"PLAYER#1\bindings-cache.wtf"));     // nothing overwritten
        Assert.NotNull(b.Sync.State.Conflict);
    }

    [Fact]
    public void Pending_push_is_done_before_any_pull_and_detects_the_conflict()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "from A")));

        // B played offline at the same time, so it has a pending push of its own.
        Assert.Equal(SyncOutcome.Conflict, b.PlaySession((@"PLAYER#1\bindings-cache.wtf", "from B")));
        Assert.Equal("from B", b.ReadSetting(@"PLAYER#1\bindings-cache.wtf"));
        Assert.Equal("M1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
    }

    [Fact]
    public void Game_starting_mid_pull_cancels_the_pull_and_leaves_local_files_untouched()
    {
        var fsB = new ScriptedFileSystem();
        var (a, b) = TwoJoinedPcs(fsB: fsB);
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));

        fsB.OnCopy = (_, dest) => { if (dest.Contains(".interfacesync-staging")) b.GameRunning = true; };
        Assert.Equal(SyncOutcome.Cancelled, b.PollAfter(FiveMinutes));

        Assert.Equal("M1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.False(Directory.Exists(b.AccountPath + ".interfacesync-staging"));
        Assert.Contains(b.Log.Lines, l => l.Message.Contains("game started during the download"));
    }

    [Fact]
    public void Online_only_cloud_files_block_the_pull_with_clear_instructions()
    {
        var fsB = new ScriptedFileSystem();
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");
        a.Sync.Tick();
        fsB.OnlineOnly = path => path.StartsWith(_world.MyDrive);
        var b = _world.Pc("WorkLaptop", fs: fsB);
        b.WriteSetting(@"PLAYER#1\macros-cache.txt", "B-own");

        Assert.Equal(SyncOutcome.Waiting, b.Sync.Tick());
        Assert.Equal(SyncStatus.WaitingForCloud, b.Sync.Status);
        Assert.Contains("Available offline", b.Sync.StatusText);
        Assert.Equal("B-own", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));

        fsB.OnlineOnly = null;
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
    }

    [Fact]
    public void Half_arrived_cloud_copy_is_never_pulled()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession(
            (@"PLAYER#1\macros-cache.txt", "M2"), (@"PLAYER#1\SavedVariables\Big.lua", new string('x', 5000))));

        // Drive on B has delivered the commit file but not the big file yet.
        var big = Path.Combine(_world.CloudAccount(), "PLAYER#1", "SavedVariables", "Big.lua");
        var realBig = File.ReadAllText(big);
        File.WriteAllText(big, "partial");
        Assert.Equal(SyncOutcome.Waiting, b.PollAfter(FiveMinutes));
        Assert.Contains("still downloading", b.Sync.StatusText);
        Assert.Equal("M1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));

        File.WriteAllText(big, realBig);
        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(FiveMinutes));
    }

    [Fact]
    public void Interrupted_push_is_finished_after_reboot_and_the_other_pc_waits_meanwhile()
    {
        var fsA = new ScriptedFileSystem();
        var (a, b) = TwoJoinedPcs(fsA: fsA);
        int cloudCopies = 0;
        fsA.FailCopy = (_, dest) => dest.StartsWith(_world.MyDrive) && ++cloudCopies > 1;

        Assert.Equal(SyncOutcome.Failed, a.PlaySession(
            (@"PLAYER#1\macros-cache.txt", "M2"), (@"PLAYER#1\bindings-cache.wtf", "A2")));
        Assert.Contains("\"writing\"", File.ReadAllText(_world.CommitFilePath()), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(SyncOutcome.Waiting, b.PollAfter(FiveMinutes));
        Assert.Equal("M1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));

        fsA.FailCopy = null;
        a.Restart();
        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(FiveMinutes));
        Assert.Equal("M2", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("A2", b.ReadSetting(@"PLAYER#1\bindings-cache.wtf"));
    }

    // ---------------------------------------------------------------- safety rules

    [Fact]
    public void Empty_local_folder_is_never_pushed_over_the_cloud()
    {
        var (a, _) = TwoJoinedPcs();
        Directory.Delete(a.AccountPath, recursive: true);
        Directory.CreateDirectory(a.AccountPath);

        Assert.Equal(SyncOutcome.Refused, a.Sync.SyncNow());
        Assert.Equal("M1", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.Equal(SyncStatus.NeedsAttention, a.Sync.Status);
    }

    [Fact]
    public void Empty_cloud_folder_is_never_pulled_and_first_pc_uploads_instead()
    {
        Directory.CreateDirectory(_world.CloudAccount());
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M1");

        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        Assert.Equal("M1", a.ReadSetting(@"PLAYER#1\macros-cache.txt"));
    }

    [Fact]
    public void First_sync_on_a_new_pc_cloud_wins_and_local_is_backed_up()
    {
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "from cloud");
        a.Sync.Tick();

        var b = _world.Pc("WorkLaptop");
        b.WriteSetting(@"PLAYER#1\macros-cache.txt", "laptop's own");
        b.WriteSetting(@"PLAYER#1\only-on-laptop.txt", "x");
        _world.Clock.Advance(FiveMinutes);                       // even when local is newer

        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        Assert.Equal("from cloud", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Null(b.ReadSetting(@"PLAYER#1\only-on-laptop.txt"));
        var backup = Assert.Single(b.Backups.List("_retail_"));
        Assert.Equal("laptop's own", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void Existing_v1_cloud_data_without_a_commit_file_is_reused()
    {
        var legacy = Path.Combine(_world.CloudAccount(), "PLAYER#1");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "macros-cache.txt"), "from v1.1");

        var b = _world.Pc("WorkLaptop");
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        Assert.Equal("from v1.1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));

        Assert.Equal(SyncOutcome.Pushed, b.PlaySession((@"PLAYER#1\macros-cache.txt", "from 2.0")));
        Assert.True(File.Exists(_world.CommitFilePath()));
    }

    private void WriteLegacyCloud(string relative, string contents, DateTime? stamp = null)
    {
        var path = Path.Combine(_world.CloudAccount(), relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, stamp ?? _world.Clock.UtcNow);
    }

    [Fact]
    public void Legacy_cloud_folder_with_files_but_no_commit_file_is_pulled_on_first_sync_not_pushed_over()
    {
        // v1.1 data: files in WTF-Account, no WTF-Account.interfacesync.json.
        WriteLegacyCloud(@"PLAYER#1\macros-cache.txt", "v1.1 macros");
        WriteLegacyCloud(@"PLAYER#1\SavedVariables\Addon.lua", "v1.1 addon settings");
        _world.Clock.Advance(FiveMinutes);
        var laptop = _world.Pc("WorkLaptop");
        laptop.WriteSetting(@"PLAYER#1\macros-cache.txt", "laptop macros (newer)");
        laptop.WriteSetting(@"PLAYER#1\only-local.txt", "x");

        Assert.Equal(SyncOutcome.Pulled, laptop.Sync.Tick());

        // Cloud won; the cloud copy was not touched.
        Assert.Equal("v1.1 macros", laptop.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("v1.1 addon settings", laptop.ReadSetting(@"PLAYER#1\SavedVariables\Addon.lua"));
        Assert.Null(laptop.ReadSetting(@"PLAYER#1\only-local.txt"));
        Assert.Equal("v1.1 macros", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.DoesNotContain(laptop.Log.Lines, l => l.Message.Contains("uploaded"));

        // This PC's settings were backed up first.
        var backup = Assert.Single(laptop.Backups.List("_retail_"), b => b.Reason == "first-join");
        Assert.Equal("laptop macros (newer)", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "macros-cache.txt")));

        // A commit file now describes the adopted data, with the same id as the legacy content.
        var commit = CommitFile.TryRead(new InterfaceSync.Core.Platform.PhysicalFileSystem(), _world.CommitFilePath())!;
        Assert.Equal(CommitState.Committed, commit.State);
        Assert.Equal(laptop.Sync.State.LastCommitId, commit.CommitId);
        Assert.Equal(2, commit.Files.Count);
        Assert.Equal(SyncOutcome.NothingToDo, laptop.PollAfter(FiveMinutes));
    }

    [Fact]
    public void Second_pc_that_joined_the_same_legacy_data_sees_no_change_after_the_commit_file_appears()
    {
        WriteLegacyCloud(@"PLAYER#1\macros-cache.txt", "v1.1 macros");
        var a = _world.Pc("HomePC");
        var b = _world.Pc("WorkLaptop");
        Assert.Equal(SyncOutcome.Pulled, a.Sync.Tick());   // writes the commit file
        Assert.Equal(SyncOutcome.Pulled, b.Sync.Tick());
        Assert.Equal(SyncOutcome.NothingToDo, a.PollAfter(FiveMinutes));
        Assert.Equal(SyncOutcome.NothingToDo, b.PollAfter(FiveMinutes));
    }

    [Fact]
    public void Push_over_a_cloud_copy_without_a_commit_file_backs_up_the_whole_cloud_copy_first()
    {
        var (a, _) = TwoJoinedPcs();
        File.Delete(_world.CommitFilePath());                       // e.g. written by something other than 2.0
        WriteLegacyCloud(@"PLAYER#1\extra-from-elsewhere.txt", "keep me");

        Assert.Equal(SyncOutcome.Pushed, a.Sync.ResolveConflict(KeepChoice.Local));

        var backup = Assert.Single(a.Backups.List("_retail_"), x => x.Reason == "cloud-before-push");
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "extra-from-elsewhere.txt")));
        Assert.Equal("M1", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "macros-cache.txt")));
    }

    [Fact]
    public void Every_push_backs_up_the_cloud_files_it_replaces()
    {
        var (a, _) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));

        var backup = Assert.Single(a.Backups.List("_retail_"), x => x.Reason == "cloud-before-push");
        Assert.Equal("M1", File.ReadAllText(Path.Combine(backup.Path, "PLAYER#1", "macros-cache.txt")));
        Assert.Equal(1, backup.FileCount);                          // only what changed
    }

    [Fact]
    public void Cloud_folder_holding_only_conflict_copies_is_not_treated_as_empty()
    {
        WriteLegacyCloud(@"PLAYER#1\macros-cache (1).txt", "only a Drive conflict copy");
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "local");

        Assert.Equal(SyncOutcome.Waiting, a.Sync.Tick());
        Assert.Contains("conflict copies", a.Sync.StatusText);
        Assert.False(File.Exists(_world.CommitFilePath()));         // nothing uploaded
    }

    [Fact]
    public void Uploading_to_an_empty_cloud_logs_the_exact_folder_and_other_folders_found()
    {
        Directory.CreateDirectory(Path.Combine(_world.MyDrive, "InterfaceSync", "forever", "WTF-Account"));
        var a = _world.Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "local");

        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        var line = Assert.Single(a.Log.Lines, l => l.Message.Contains("first sync on this PC")).Message;
        Assert.Contains(_world.CloudAccount(), line);
        Assert.Contains("does not exist", line);
        Assert.Contains("Other folders in", line);
        Assert.Contains("forever", line);
    }

    [Fact]
    public void Config_wtf_is_never_synced()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.False(File.Exists(Path.Combine(_world.CloudAccount(), "..", "Config.wtf")));
        Assert.Empty(Directory.GetFiles(_world.CloudAccount(), "Config.wtf", SearchOption.AllDirectories));
        Assert.Contains("WorkLaptop", File.ReadAllText(Path.Combine(b.VersionPath, "WTF", "Config.wtf")));
    }

    [Fact]
    public void Local_deletions_are_mirrored_to_the_cloud_with_a_backup()
    {
        var (a, b) = TwoJoinedPcs();
        a.GameRunning = true; a.Sync.Tick();
        File.Delete(Path.Combine(a.AccountPath, "PLAYER#1", "bindings-cache.wtf"));
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "M2");
        a.GameRunning = false; a.Sync.Tick();
        Assert.Equal(SyncOutcome.Pushed, a.PollAfter(TimeSpan.FromSeconds(15)));

        Assert.False(File.Exists(Path.Combine(_world.CloudAccount(), "PLAYER#1", "bindings-cache.wtf")));
        var removed = Directory.GetDirectories(Path.Combine(a.DataFolder, "Backups", "_retail_"), "*cloud-before-push");
        Assert.True(File.Exists(Path.Combine(Assert.Single(removed), "PLAYER#1", "bindings-cache.wtf")));

        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(FiveMinutes));
        Assert.Null(b.ReadSetting(@"PLAYER#1\bindings-cache.wtf"));
    }

    [Fact]
    public void Drive_conflict_copies_are_reported_not_synced()
    {
        var (a, b) = TwoJoinedPcs();
        var copy = Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache (1).txt");
        File.WriteAllText(copy, "drive conflict copy");

        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));
        Assert.True(File.Exists(copy));                                            // not deleted by the mirror
        Assert.Contains(a.Log.Lines, l => l.Message.Contains("conflict copies") && l.Message.Contains("macros-cache (1).txt"));

        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(FiveMinutes));
        Assert.Null(b.ReadSetting(@"PLAYER#1\macros-cache (1).txt"));
    }

    [Fact]
    public void Folder_links_are_skipped_and_never_followed()
    {
        var (a, _) = TwoJoinedPcs();
        var outside = Path.Combine(_world.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "should not sync");
        Directory.CreateSymbolicLink(Path.Combine(a.AccountPath, "PLAYER#1", "Linked"), outside);

        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));
        Assert.False(Directory.Exists(Path.Combine(_world.CloudAccount(), "PLAYER#1", "Linked")));
        Assert.Contains(a.Log.Lines, l => l.Message.Contains("skipped folder link"));
    }

    [Fact]
    public void Nothing_is_written_while_the_game_is_running()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "M2")));
        b.GameRunning = true;

        Assert.Equal(SyncOutcome.Waiting, b.PollAfter(FiveMinutes));
        Assert.Equal(SyncOutcome.Waiting, b.Sync.SyncNow());
        Assert.Equal("M1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
    }

    // ---------------------------------------------------------------- conflicts

    [Fact]
    public void Conflict_keeps_local_saves_cloud_copy_and_suggests_the_newer()
    {
        var (a, b) = TwoJoinedPcs();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "from A")));
        Assert.Equal(SyncOutcome.Conflict, b.PlaySession((@"PLAYER#1\macros-cache.txt", "from B")));

        var conflict = b.Sync.State.Conflict!;
        Assert.Equal("from B", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("from A", File.ReadAllText(Path.Combine(conflict.CloudBackupPath!, "PLAYER#1", "macros-cache.txt")));
        Assert.Equal(KeepChoice.Local, conflict.Suggested);               // B played later
        Assert.Equal(SyncStatus.NeedsAttention, b.Sync.Status);

        b.Restart();                                                      // conflict survives restart
        Assert.Equal(SyncOutcome.Conflict, b.PollAfter(FiveMinutes));
    }

    [Fact]
    public void Resolving_a_conflict_by_keeping_the_cloud_copy()
    {
        var (a, b) = TwoJoinedPcs();
        a.PlaySession((@"PLAYER#1\macros-cache.txt", "from A"));
        b.PlaySession((@"PLAYER#1\macros-cache.txt", "from B"));

        Assert.Equal(SyncOutcome.Pulled, b.Sync.ResolveConflict(KeepChoice.Cloud));
        Assert.Equal("from A", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Null(b.Sync.State.Conflict);
        Assert.Contains(b.Backups.List("_retail_"), x => x.Reason == "before-keep-cloud");
    }

    [Fact]
    public void Resolving_a_conflict_by_keeping_this_pc()
    {
        var (a, b) = TwoJoinedPcs();
        a.PlaySession((@"PLAYER#1\macros-cache.txt", "from A"));
        b.PlaySession((@"PLAYER#1\macros-cache.txt", "from B"));

        Assert.Equal(SyncOutcome.Pushed, b.Sync.ResolveConflict(KeepChoice.Local));
        Assert.Equal("from B", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.Equal(SyncOutcome.Pulled, a.PollAfter(FiveMinutes));
        Assert.Equal("from B", a.ReadSetting(@"PLAYER#1\macros-cache.txt"));
    }

    // ---------------------------------------------------------------- backups

    [Fact]
    public void Restore_from_backup_puts_files_back_and_pushes_them()
    {
        var (a, b) = TwoJoinedPcs();
        a.PlaySession((@"PLAYER#1\macros-cache.txt", "good"));
        b.PollAfter(FiveMinutes);
        a.PlaySession((@"PLAYER#1\macros-cache.txt", "broken by an addon"));
        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(FiveMinutes));

        var good = b.Backups.List("_retail_").First(x => File.ReadAllText(Path.Combine(x.Path, "PLAYER#1", "macros-cache.txt")) == "good");
        Assert.Equal(SyncOutcome.Pushed, b.Sync.RestoreBackup(good));
        Assert.Equal("good", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("good", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));
        Assert.Contains(b.Backups.List("_retail_"), x => x.Reason == "before-restore");
    }

    [Fact]
    public void Backups_are_pruned_to_the_newest_ten_per_version()
    {
        var a = _world.Pc("HomePC");
        for (int i = 0; i < 13; i++)
        {
            _world.Clock.Advance(TimeSpan.FromMinutes(1));
            a.WriteSetting(@"PLAYER#1\macros-cache.txt", $"version {i}");   // a changed folder each time
            a.Backups.Create("_retail_", a.AccountPath, $"test-{i}");
        }
        var list = a.Backups.List("_retail_");
        Assert.Equal(10, list.Count);
        Assert.Equal("test-12", list[0].Reason);
        Assert.Equal("test-3", list[^1].Reason);
    }

    // ---------------------------------------------------------------- long run

    [Fact]
    public void Fifty_alternating_sessions_with_crashes_mid_push_lose_nothing()
    {
        var fsA = new ScriptedFileSystem();
        var fsB = new ScriptedFileSystem();
        var (a, b) = TwoJoinedPcs(fsA, fsB);

        for (int i = 0; i < 50; i++)
        {
            var (pc, fs, other) = i % 2 == 0 ? (a, fsA, b) : (b, fsB, a);

            Assert.NotEqual(SyncOutcome.Conflict, pc.PollAfter(FiveMinutes)); // pulls the other PC's last session

            bool crash = i % 10 == 7;
            int copies = 0;
            if (crash) fs.FailCopy = (_, dest) => dest.StartsWith(_world.MyDrive) && ++copies > 1;

            var outcome = pc.PlaySession(
                ($@"PLAYER#1\SavedVariables\Session{i:00}.lua", $"session {i} on {pc.Name}"),
                (@"PLAYER#1\macros-cache.txt", $"M{i}"));

            if (crash)
            {
                Assert.Equal(SyncOutcome.Failed, outcome);
                Assert.NotEqual(SyncOutcome.Pulled, other.PollAfter(TimeSpan.FromMinutes(1)));
                fs.FailCopy = null;
                pc.Restart();                                                   // forced reboot mid-sync
                outcome = pc.Sync.Tick();
            }
            Assert.Equal(SyncOutcome.Pushed, outcome);
        }

        Assert.NotEqual(SyncOutcome.Conflict, a.PollAfter(FiveMinutes));
        Assert.NotEqual(SyncOutcome.Conflict, b.PollAfter(FiveMinutes));
        foreach (var pc in new[] { a, b })
        {
            Assert.Null(pc.Sync.State.Conflict);
            Assert.Equal("M49", pc.ReadSetting(@"PLAYER#1\macros-cache.txt"));
            for (int i = 0; i < 50; i++)
                Assert.NotNull(pc.ReadSetting($@"PLAYER#1\SavedVariables\Session{i:00}.lua"));
        }
    }
}
