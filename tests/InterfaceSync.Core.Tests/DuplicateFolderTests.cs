using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Sync;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class DuplicateFolderTests : IDisposable
{
    private readonly string _drive = Path.Combine(Path.GetTempPath(), "is-dups", Guid.NewGuid().ToString("N"), "My Drive");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_drive)!, true); } catch (IOException) { } }

    private void Folder(string relative, int files = 1)
    {
        var dir = Path.Combine(_drive, relative);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < files; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.lua"), "x");
    }

    [Fact]
    public void Copies_made_by_each_cloud_app_are_found_and_everything_else_is_left_alone()
    {
        Folder(@"InterfaceSync/_retail_/WTF-Account");
        Folder(@"InterfaceSync (1)/_retail_/WTF-Account", files: 3);                       // Google Drive
        Folder(@"InterfaceSync (MIKE-M3's conflicted copy 2026-11-23)/_retail_");           // Dropbox
        Folder(@"InterfaceSync-LAPTOP/_retail_");                                           // OneDrive
        Folder(@"InterfaceSync/_retail_ (1)/WTF-Account", files: 2);                       // a version folder copy
        Folder(@"InterfaceSync Notes");                                                     // the player's own: not a copy
        Folder(@"WoWSync/_retail_");                                                        // the old folder: not a copy
        Folder(@"Photos (1)");                                                              // unrelated

        var found = DuplicateFolders.Find(new PhysicalFileSystem(), _drive);
        Assert.Equal(
            ["InterfaceSync (1)", "InterfaceSync (MIKE-M3's conflicted copy 2026-11-23)", "InterfaceSync-LAPTOP", "_retail_ (1)"],
            found.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, found.Single(d => d.Name == "InterfaceSync (1)").FileCount);
        Assert.Equal("_retail_", found.Single(d => d.Name == "_retail_ (1)").CopyOf);
        Assert.Equal("InterfaceSync", found.Single(d => d.Name == "InterfaceSync-LAPTOP").CopyOf);
    }

    [Fact]
    public void Nothing_is_reported_when_there_are_no_copies()
    {
        Folder(@"InterfaceSync/_retail_/WTF-Account");
        Assert.Empty(DuplicateFolders.Find(new PhysicalFileSystem(), _drive));
        Assert.Empty(DuplicateFolders.Find(new PhysicalFileSystem(), Path.Combine(_drive, "missing")));
    }
}
