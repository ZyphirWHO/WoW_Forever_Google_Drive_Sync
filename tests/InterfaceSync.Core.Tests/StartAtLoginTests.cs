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

public sealed class RememberedStartAtLoginTests
{
    private const string Exe = @"C:\Users\P\AppData\Local\Programs\Interface Sync\InterfaceSync.exe";

    private static (RememberedStartAtLogin s, WindowsStartAtLogin inner, InterfaceSync.Core.Tests.Fakes.FakeRegistry reg, Func<bool?> wanted) Make(bool? choice, bool setupComplete = true)
    {
        var reg = new InterfaceSync.Core.Tests.Fakes.FakeRegistry();
        var inner = new WindowsStartAtLogin(reg, Exe, @"C:\Users\P\AppData\Local\Temp");
        bool? saved = choice;
        var s = new RememberedStartAtLogin(inner, () => saved, on => saved = on, () => setupComplete);
        return (s, inner, reg, () => saved);
    }

    [Fact]
    public void An_entry_that_went_missing_is_put_back_at_start()
    {
        var (s, inner, _, wanted) = Make(choice: true);
        Assert.False(inner.IsRegistered);                                // e.g. removed by uninstalling
        Assert.Contains("added back", s.Repair());
        Assert.True(s.IsEnabled);
        Assert.True(wanted());
    }

    [Fact]
    public void Settings_from_before_the_choice_was_remembered_count_as_on()
    {
        var (s, _, _, wanted) = Make(choice: null);
        s.Repair();
        Assert.True(s.IsEnabled);
        Assert.True(wanted());
    }

    [Fact]
    public void Switching_it_off_is_remembered_and_respected()
    {
        var (s, _, _, wanted) = Make(choice: true);
        s.Enable();
        s.Disable();
        Assert.False(wanted());
        Assert.Null(s.Repair());
        Assert.False(s.IsEnabled);
    }

    [Fact]
    public void A_switch_off_in_Task_Manager_is_explained_not_overridden()
    {
        var (s, _, reg, _) = Make(choice: true);
        s.Enable();
        reg.SetString(Abstractions.RegistryHive.CurrentUser, WindowsStartAtLogin.ApprovedKey, WindowsStartAtLogin.ValueName, "030000000000000000000000");
        Assert.False(s.IsEnabled);
        Assert.Contains("Startup apps", s.Repair());
        Assert.False(s.IsEnabled);                                       // still off: the player's Windows choice
        Assert.NotNull(s.SwitchedOffNote);
        s.Enable();                                                      // switching it on here clears it
        Assert.True(s.IsEnabled);
    }

    [Fact]
    public void Nothing_happens_before_setup_is_done()
    {
        var (s, inner, _, wanted) = Make(choice: null, setupComplete: false);
        Assert.Null(s.Repair());
        Assert.False(inner.IsRegistered);
        Assert.Null(wanted());
    }
}
