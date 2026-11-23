using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Tests.Fakes;
using Xunit;

namespace InterfaceSync.Core.Tests;

public sealed class StartAtLoginTests
{
    private const string Exe = @"C:\Users\Player\Documents\Interface Sync\InterfaceSync.exe";
    private const string Temp = @"C:\Users\Player\AppData\Local\Temp";

    [Fact]
    public void Windows_enable_writes_the_per_user_run_entry_that_starts_in_the_tray()
    {
        var reg = new FakeRegistry();
        var s = new WindowsStartAtLogin(reg, Exe, Temp);
        Assert.False(s.IsEnabled);
        s.Enable();
        Assert.Equal($"\"{Exe}\" --tray", reg.GetString(RegistryHive.CurrentUser, WindowsStartAtLogin.RunKey, "InterfaceSync"));
        Assert.True(s.IsEnabled);
        s.Disable();
        Assert.False(s.IsEnabled);
        Assert.Null(reg.GetString(RegistryHive.CurrentUser, WindowsStartAtLogin.RunKey, "InterfaceSync"));
    }

    [Fact]
    public void Windows_switched_off_in_task_manager_shows_as_off_and_enabling_switches_it_back_on()
    {
        var reg = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, WindowsStartAtLogin.RunKey, "InterfaceSync", $"\"{Exe}\" --tray")
            .Set(RegistryHive.CurrentUser, WindowsStartAtLogin.ApprovedKey, "InterfaceSync", "030000000000000000000000");
        var s = new WindowsStartAtLogin(reg, Exe, Temp);
        Assert.True(s.SwitchedOffInTaskManager);
        Assert.False(s.IsEnabled);
        s.Enable();
        Assert.True(s.IsEnabled);
        reg.Set(RegistryHive.CurrentUser, WindowsStartAtLogin.ApprovedKey, "InterfaceSync", "020000000000000000000000");
        Assert.True(s.IsEnabled);
    }

    [Fact]
    public void Windows_refuses_to_register_a_copy_running_from_inside_a_zip()
    {
        var inZip = Temp + @"\Temp1_InterfaceSync-dev.zip\InterfaceSync.exe";
        var s = new WindowsStartAtLogin(new FakeRegistry(), inZip, Temp);
        Assert.Contains("temporary folder", s.CannotEnableReason);
        Assert.Throws<InvalidOperationException>(s.Enable);
        Assert.Null(new WindowsStartAtLogin(new FakeRegistry(), Exe, Temp).CannotEnableReason);
    }

    [Fact]
    public void Windows_repair_follows_the_exe_after_it_moved_and_leaves_other_cases_alone()
    {
        var reg = new FakeRegistry().Set(RegistryHive.CurrentUser, WindowsStartAtLogin.RunKey, "InterfaceSync", "\"C:\\Users\\Player\\Downloads\\InterfaceSync.exe\" --tray");
        var s = new WindowsStartAtLogin(reg, Exe, Temp);
        Assert.Contains("Downloads", s.Repair());
        Assert.True(s.IsEnabled);
        Assert.Null(s.Repair());                                            // already right
        Assert.Null(new WindowsStartAtLogin(new FakeRegistry(), Exe, Temp).Repair());   // never switched on: stays off
    }

    [Fact]
    public void Mac_login_item_is_a_launch_agent_that_opens_the_app()
    {
        var fs = new InMemoryFileSystem();
        const string app = "/Applications/Interface Sync.app/Contents/MacOS/InterfaceSync";
        var s = new MacStartAtLogin(fs, "/Users/player", app, "io.github.zyphirwho.interfacesync");
        Assert.False(s.IsEnabled);
        s.Enable();
        Assert.Equal("/Users/player/Library/LaunchAgents/io.github.zyphirwho.interfacesync.plist", s.PlistPath);
        var plist = fs.ReadAllText(s.PlistPath);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains($"<string>{app}</string>", plist);
        Assert.True(s.IsEnabled);
        s.Disable();
        Assert.False(fs.FileExists(s.PlistPath));
    }

    [Fact]
    public void Mac_refuses_from_a_translocated_or_disk_image_copy_and_repairs_after_a_move()
    {
        var fs = new InMemoryFileSystem();
        var moved = new MacStartAtLogin(fs, "/Users/player", "/private/var/folders/x/AppTranslocation/ABC/d/Interface Sync.app/Contents/MacOS/InterfaceSync", "id");
        Assert.Contains("Applications", moved.CannotEnableReason);
        var old = new MacStartAtLogin(fs, "/Users/player", "/Users/player/Downloads/Interface Sync.app/Contents/MacOS/InterfaceSync", "id");
        old.Enable();
        var now = new MacStartAtLogin(fs, "/Users/player", "/Applications/Interface Sync.app/Contents/MacOS/InterfaceSync", "id");
        Assert.False(now.IsEnabled);
        Assert.Contains("Downloads", now.Repair());
        Assert.True(now.IsEnabled);
    }
}
