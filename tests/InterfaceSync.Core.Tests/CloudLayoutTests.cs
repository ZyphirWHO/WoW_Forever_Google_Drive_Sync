using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

/// <summary>Layout: &lt;provider root&gt;\InterfaceSync\&lt;_version_&gt;\WTF-Account (+ commit file). Nothing else is ever written or synced.</summary>
public sealed class CloudLayoutTests : IDisposable
{
    private readonly SimWorld _world = new();
    public void Dispose() => _world.Dispose();

    [Fact]
    public void Layout_is_app_folder_then_version_then_wtf_account_and_commit_file()
    {
        var layout = new CloudLayout(@"C:\Users\P\My Drive", "_forever_");
        Assert.Equal(@"C:\Users\P\My Drive\InterfaceSync\_forever_", layout.VersionRoot);
        Assert.Equal(@"C:\Users\P\My Drive\InterfaceSync\_forever_\WTF-Account", layout.Account);
        Assert.Equal(@"C:\Users\P\My Drive\InterfaceSync\_forever_\WTF-Account.interfacesync.json", layout.CommitFile);
    }

    [Fact]
    public void Syncing_never_creates_an_addons_folder_in_the_cloud()
    {
        var a = _world.Pc("HomePC", "_forever_");
        Directory.CreateDirectory(Path.Combine(a.VersionPath, "Interface", "AddOns", "Details"));
        File.WriteAllText(Path.Combine(a.VersionPath, "Interface", "AddOns", "Details", "Details.toc"), "addon");
        a.WriteSetting(@"PLAYER#1\SavedVariables\Details.lua", "addon settings");

        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());
        Assert.Equal(SyncOutcome.Pushed, a.PlaySession((@"PLAYER#1\SavedVariables\Details.lua", "changed")));

        var versionRoot = Path.Combine(_world.MyDrive, "InterfaceSync", "_forever_");
        // The settings, and the small "Your computers" files; never an AddOns folder.
        Assert.Equal(["Computers", "WTF-Account"], Directory.GetDirectories(versionRoot).Select(Path.GetFileName).Order());
        Assert.Equal(["HomePC.json"], Directory.GetFiles(Path.Combine(versionRoot, "Computers")).Select(Path.GetFileName));
        Assert.Equal(["WTF-Account.interfacesync.json"], Directory.GetFiles(versionRoot).Select(Path.GetFileName));
    }

    [Fact]
    public void A_leftover_v1_addons_folder_in_the_cloud_is_not_data_and_is_never_touched()
    {
        // The old v1.1 "_forever_\AddOns" cloud folder, if it ever reappears, is neither synced nor counted as settings.
        var addons = Path.Combine(_world.MyDrive, "InterfaceSync", "_forever_", "AddOns", "Details");
        Directory.CreateDirectory(addons);
        File.WriteAllText(Path.Combine(addons, "Details.toc"), "v1.1 addon copy");
        var a = _world.Pc("HomePC", "_forever_");
        a.WriteSetting(@"PLAYER#1\macros-cache.txt", "local");

        Assert.Equal(SyncOutcome.Pushed, a.Sync.Tick());                         // cloud had no settings: uploaded, not pulled
        Assert.Equal("local", a.ReadSetting(@"PLAYER#1\macros-cache.txt"));
        Assert.Equal("v1.1 addon copy", File.ReadAllText(Path.Combine(addons, "Details.toc")));
        Assert.False(Directory.Exists(Path.Combine(a.VersionPath, "WTF", "Account", "AddOns")));
        Assert.False(Directory.Exists(Path.Combine(a.VersionPath, "Interface")));  // local AddOns never created either

        var fs = new PhysicalFileSystem();
        var name = CloudLayout.ResolveFolderName(fs, _world.MyDrive, "_forever_", new AppSettings(), out var adopted, out _);
        Assert.Equal("_forever_", name);
        Assert.Null(adopted);
    }
}
