using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace InterfaceSync.Core.Tests;

/// <summary>How much Interface Sync writes to disk: nothing while idle, and little per game session.</summary>
public sealed class DiskWriteTests(ITestOutputHelper output) : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    private const int Files = 300, FileSize = 40_000;     // a realistic WTF\Account: 300 files, about 12 MB
    private static long Folder => (long)Files * FileSize;

    private (SimulatedPc pc, ScriptedFileSystem fs) Pc(string name)
    {
        var fs = new ScriptedFileSystem();
        var pc = _world.Pc(name, fs: fs);
        fs.Areas.Add(("backups", Path.Combine(pc.DataFolder, "Backups")));
        fs.Areas.Add(("state", pc.DataFolder));
        fs.Areas.Add(("cloud", _world.MyDrive));
        fs.Areas.Add(("game folder", Path.GetDirectoryName(pc.AccountPath)!));
        return (pc, fs);
    }

    private static void Fill(SimulatedPc pc, string tag)
    {
        var block = new string('x', FileSize);
        for (int i = 0; i < Files; i++) pc.WriteSetting($@"PLAYER#1\SavedVariables\Addon{i:000}.lua", $"{tag} {i:000} {block}".Substring(0, FileSize));
    }

    private string Report(ScriptedFileSystem fs) =>
        string.Join(", ", fs.Written.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value / 1024.0:N0} KB"));

    [Fact]
    public void Idle_polling_writes_nothing_and_a_session_writes_a_small_multiple_of_what_changed()
    {
        var (a, fsA) = Pc("HomePC");
        var (b, fsB) = Pc("Laptop");
        Fill(a, "home");
        a.Sync.SyncNow();                                   // first upload
        b.Sync.SyncNow();                                   // laptop joins: downloads
        fsA.Written.Clear(); fsB.Written.Clear();

        // An hour of polling every 15 seconds with nothing changing.
        for (int i = 0; i < 240; i++) { a.PollAfter(TimeSpan.FromSeconds(15)); b.PollAfter(TimeSpan.FromSeconds(0)); }
        output.WriteLine($"Idle hour: home {Report(fsA)} | laptop {Report(fsB)}");
        Assert.Empty(fsA.Written);
        Assert.Empty(fsB.Written);

        // A game session on the home PC changes 3 addons' settings, then the laptop picks them up.
        var changed = new[] { 5, 17, 123 }.Select(i => ($@"PLAYER#1\SavedVariables\Addon{i:000}.lua", $"changed {i} {new string('y', FileSize)}".Substring(0, FileSize))).ToArray();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession(changed));
        var upload = fsA.Written.Values.Sum();
        output.WriteLine($"Session upload (3 of {Files} files changed, folder {Folder / 1024 / 1024.0:N1} MB): {Report(fsA)}");
        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(TimeSpan.FromMinutes(5)));
        var download = fsB.Written.Values.Sum();
        output.WriteLine($"Download on the other PC: {Report(fsB)}");
        output.WriteLine($"Per session in all: {(upload + download) / 1024.0 / 1024:N1} MB");

        // Each PC writes a few times what changed (backup + new copy + small records), never the whole folder.
        long changedBytes = 3L * FileSize;
        Assert.True(upload < 6 * changedBytes, $"upload wrote {upload:N0} bytes");
        Assert.True(download < 4 * changedBytes, $"download wrote {download:N0} bytes");
    }

    [Fact]
    public void Restoring_puts_the_settings_back_as_they_were_across_several_downloads()
    {
        var (a, _) = Pc("HomePC");
        var (b, _) = Pc("Laptop");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "v1");
        a.WriteSetting(@"PLAYER#1\SavedVariables\Bags.lua", "bags v1");
        a.WriteSetting(@"PLAYER#1\SavedVariables\Map.lua", "map v1");
        a.Sync.SyncNow();
        b.Sync.SyncNow();
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "v2")));
        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(TimeSpan.FromMinutes(5)));                // backup 1: macros v1
        var beforeFirstChange = b.Backups.List("_retail_")[0];
        Assert.True(beforeFirstChange.Partial);
        Assert.Equal(1, beforeFirstChange.FileCount);
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\macros-cache.txt", "v3"), (@"PLAYER#1\SavedVariables\Bags.lua", "bags v3")));
        Assert.Equal(SyncOutcome.Pulled, b.PollAfter(TimeSpan.FromMinutes(5)));                // backup 2: macros v2, bags v1

        // Back to how the laptop was before the first of those downloads: every file as it was then.
        Assert.Equal(SyncOutcome.Pushed, b.Sync.RestoreBackup(beforeFirstChange));
        Assert.Equal("v1", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("bags v1", b.ReadSetting(@"PLAYER#1\SavedVariables\Bags.lua"));
        Assert.Equal("map v1", b.ReadSetting(@"PLAYER#1\SavedVariables\Map.lua"));       // untouched, still there
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_world.CloudAccount(), "PLAYER#1", "macros-cache.txt")));   // and uploaded

        // The restore itself can be undone: what it replaced was backed up.
        var undo = b.Backups.List("_retail_").First(x => x.Reason == "before-restore-changes");
        Assert.Equal(SyncOutcome.Pushed, b.Sync.RestoreBackup(undo));
        Assert.Equal("v3", b.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("bags v3", b.ReadSetting(@"PLAYER#1\SavedVariables\Bags.lua"));
    }

    [Fact]
    public void A_whole_folder_backup_from_an_earlier_build_still_restores_exactly()
    {
        var (a, _) = Pc("HomePC");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "now");
        a.WriteSetting(@"PLAYER#1\added-later.txt", "new file");
        a.Sync.SyncNow();
        var old = Path.Combine(a.Backups.Root, "_retail_", "20261101-120000-000-before-pull", "PLAYER#1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "macros-cache.txt"), "then");
        var backup = a.Backups.List("_retail_").Single(x => x.Reason == "before-pull");
        Assert.False(backup.Partial);
        Assert.Equal(SyncOutcome.Pushed, a.Sync.RestoreBackup(backup));
        Assert.Equal("then", a.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Null(a.ReadSetting(@"PLAYER#1\added-later.txt"));                             // exactly the old copy
    }
}
