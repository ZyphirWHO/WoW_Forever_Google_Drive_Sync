using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InterfaceSync.Core;
using InterfaceSync.Core.Abstractions;
using InterfaceSync.Core.Detection;
using InterfaceSync.Core.Logging;
using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Settings;
using InterfaceSync.Core.Sync;
using InterfaceSync.Core.Updates;

using InterfaceSync.Core.Localization;

namespace InterfaceSync.Desktop;

/// <summary>Things the main window's pages, the tray panel and the menu can all do.</summary>
internal static class Actions
{
    public static async void OpenFolder(TopLevel? from, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (from?.Launcher is { } launcher) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
            else System.Diagnostics.Process.Start("open", $"\"{folder}\"");
        }
        catch (Exception e) { Dialogs.Message(L.F("Couldn't open {0}: {1}", folder, e.Message), warning: true).Show(); }
    }

    public static async void OpenLink(TopLevel? from, Uri page)
    {
        try
        {
            if (from?.Launcher is { } launcher) await launcher.LaunchUriAsync(page);
            else System.Diagnostics.Process.Start("open", page.ToString());
        }
        catch (Exception) { }
    }

    /// <summary>A question with two buttons; <paramref name="yes"/> runs only when the main one is clicked.</summary>
    public static void Ask(string heading, string message, string yesLabel, Action yes)
    {
        Window? w = null;
        var go = Ui.Button(yesLabel, () => { w!.Close(); yes(); }, ButtonKind.Primary);
        go.IsDefault = true;
        var cancel = Ui.Button(L.T("Cancel"), () => w!.Close(), ButtonKind.Ghost);
        cancel.IsCancel = true;
        w = new Window
        {
            Title = ProductInfo.Name, Icon = AppIcons.App, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel { Margin = new Thickness(22, 20, 22, 16), Spacing = 8, Children =
            {
                Ui.Text(heading, 15, FontWeight.SemiBold),
                Ui.Text(message, 13),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, go } },
            } },
        };
        w.Show();
    }

    public static void ReportProblem(TopLevel? from, IAppHost host) =>
        Ask(L.T("Report a problem"),
            L.T("This opens a new issue on GitHub with the diagnostics filled in: versions, what was found and the recent log, with your folder paths, computer name and user name replaced by placeholders. Nothing is sent until you check it, describe what happened and click Submit. You need a free GitHub account."),
            L.T("Open GitHub"), () =>
            {
                string text = "";
                host.Run(() => text = host.Service.RedactedDiagnostics(), () => OpenLink(from, Feedback.IssueUrl(text)));
            });

    public static async void CopyDiagnostics(TopLevel? from, IAppHost host)
    {
        if (from?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(host.Service.Diagnostics());
        Dialogs.Message(L.T("Paste them into your message or GitHub issue. They include your folder paths."), L.T("Diagnostics copied")).Show();
    }

    public static void ShowWhatsNew(TopLevel? from, string? notes = null)
    {
        notes ??= ReleaseNotes.Current;
        if (notes == null) { OpenLink(from, Feedback.ReleasesPage); return; }
        new TextWindow(L.F("What's new in {0} {1}", ProductInfo.Name, ReleaseNotes.BaseVersion(ProductInfo.DisplayVersion)), ReleaseNotes.ToPlainText(notes), false,
            (L.T("All releases"), () => OpenLink(from, Feedback.ReleasesPage))).Show();
    }

    public static void ViewLog(TopLevel? from, IAppHost host)
    {
        var lines = host.Service.RecentLog(500).Reverse().Select(l => l.ToString());
        TextWindow? w = null;
        w = new TextWindow(L.T("Log (newest first)"), string.Join("\n", lines), true, (L.T("Open log folder"), () => OpenFolder(w, host.Service.Paths.Logs)));
        if (from is Window owner) w.Show(owner); else w.Show();
    }

    /// <summary>"Settings changed on two computers": the side-by-side choice, then the chosen copy is kept.</summary>
    public static void Resolve(Window? owner, IAppHost host, VersionSync w)
    {
        if (w.State.Conflict is not { } c) return;
        var dialog = Dialogs.ChooseKeep(w.Version.FolderName, c, keep =>
        {
            if (keep is { } k) host.Run(() => host.Service.Resolve(w, k), () => (owner as MainWindow)?.Refresh());
        });
        if (owner != null) dialog.Show(owner); else dialog.Show();
    }

    /// <summary>Choose what syncs, in the main window's side panel.</summary>
    public static void ChooseWhatSyncs(MainWindow shell, VersionSync worker)
    {
        var host = shell.Host;
        var view = new SyncChoicesView(host.Service, worker,
            save: (rules, leaveOut) =>
            {
                shell.CloseDrawer();
                host.Run(() =>
                {
                    host.Service.SetRules(worker, rules);
                    if (leaveOut != host.Service.LeavesOutUninstalledAddons(worker.Version.FolderName))
                        host.Service.SetLeaveOutUninstalledAddons(worker, leaveOut);
                }, () =>
                {
                    shell.Refresh();
                    shell.ShowToast(rules.Describe() ?? L.T("Everything syncs on this computer."));
                });
            },
            cancel: shell.CloseDrawer,
            cleanUp: size => CleanUpUninstalledAddons(shell, worker, size));
        shell.OpenDrawer(L.F("Choose what syncs: {0}", worker.Version.FolderName), view);
    }

    /// <summary>Removes this computer's settings of addons that aren't installed, with Undo in the note that follows.</summary>
    public static void CleanUpUninstalledAddons(MainWindow shell, VersionSync worker, string size)
    {
        var host = shell.Host;
        CleanUpResult? result = null;
        host.Run(() => result = host.Service.CleanUpUninstalledAddons(worker), () =>
        {
            shell.CloseDrawer();
            shell.Refresh();
            if (result is not { } r) return;
            if (r.Problem != null) { shell.ShowToast(L.F("Nothing was removed: {0}", r.Problem)); return; }
            if (r.FileCount == 0) { shell.ShowToast(L.T("There was nothing to clean up.")); return; }
            var backup = r.Backup;
            shell.ShowToast(L.N(r.Addons.Count, "Removed the settings of {0} addon that isn't installed", "Removed the settings of {0} addons that aren't installed") + $" ({size}).",
                backup == null ? null : () => host.Run(() => host.Service.Restore(worker, backup), () => { shell.Refresh(); shell.ShowToast(L.T("Put back.")); }),
                seconds: 10);
        });
    }

    /// <summary>Switches syncing of a game version on or off on this computer.</summary>
    public static void SetVersionEnabled(IAppHost host, GameVersion v, bool on, Action after)
    {
        if (host.Service.IsEnabled(v) == on) return;
        host.Run(() =>
        {
            var settings = host.Service.Settings;
            settings.EnabledVersions[v.FolderName] = on;
            host.Service.SaveSettings(settings);
            host.Service.Log.Info($"{v.FolderName}: syncing switched {(on ? "on" : "off")} on this {Os.Machine}.");
        }, after);
    }

    /// <summary>Start with Windows / Open at Login on or off, explaining when it can't be switched on from here.</summary>
    public static void SetStartAtLogin(IAppHost host, bool on)
    {
        if (host.StartAtLogin is not { } startup) return;
        try
        {
            if (on && !startup.IsEnabled)
            {
                if (startup.CannotEnableReason is { } why) Dialogs.Message(why).Show();
                else { startup.Enable(); host.Service.Log.Info($"{startup.Label}: on."); }
            }
            else if (!on && startup.IsRegistered) { startup.Disable(); host.Service.Log.Info($"{startup.Label}: off."); }
        }
        catch (Exception e)
        {
            host.Service.Log.Warn($"Couldn't change {startup.Label}: {e.Message}");
            Dialogs.Message(L.F("Couldn't change {0}: {1}", L.T(startup.Label), e.Message), warning: true).Show();
        }
    }

    /// <summary>Setup again: choose game versions and the cloud folder step by step.</summary>
    public static void RunSetup(Window owner, IAppHost host)
    {
        var setup = new SetupWindow(host, host.StartAtLogin);
        setup.Closed += (_, _) => (owner as MainWindow)?.Refresh();
        setup.Show(owner);
    }

    /// <summary>Pause choices as a menu under a button (or Resume straight away when paused).</summary>
    public static void Pause(Control anchor, IAppHost host, Action refresh)
    {
        if (host.Service.Settings.Paused) { host.Run(() => host.Service.SetPaused(false), refresh); return; }
        var menu = new MenuFlyout();
        foreach (var (label, until) in StatusWords.PauseChoices(DateTime.UtcNow))
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => host.Run(() => host.Service.SetPaused(true, until), refresh);
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
}

/// <summary>A read-only text window: What's new, the log, one activity entry's files.</summary>
internal sealed class TextWindow : Window
{
    public TextWindow(string title, string text, bool monospace, (string Label, Action Click)? extra = null)
    {
        Title = $"{ProductInfo.Name}: {title}";
        Icon = AppIcons.App;
        Width = 680;
        Height = 540;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = monospace ? TextWrapping.NoWrap : TextWrapping.Wrap,
            FontFamily = monospace ? new FontFamily("Menlo, monospace") : FontFamily.Default, FontSize = monospace ? 11 : 13,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(box, title);
        var close = Ui.Button(L.T("Close"), Close, ButtonKind.Primary);
        close.IsDefault = close.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        if (extra is { } e) buttons.Children.Add(Ui.Button(e.Label, e.Click, ButtonKind.Ghost));
        buttons.Children.Add(close);
        var heading = Ui.Text(title, 18, FontWeight.SemiBold, bottom: 10);
        var frame = Ui.CardOf(new ScrollViewer { Content = box });
        frame.Margin = new Thickness(0);
        var dock = new DockPanel { Margin = new Thickness(22, 18) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(heading);
        dock.Children.Add(buttons);
        dock.Children.Add(frame);
        Content = dock;
    }

    internal string Body => ((TextBox)((ScrollViewer)((Border)((DockPanel)Content!).Children[2]).Child!).Content!).Text ?? "";
}

/// <summary>Asks for a snapshot's name.</summary>
internal sealed class InputWindow : Window
{
    private readonly TextBox _box = new() { MaxLength = Core.Backup.BackupStore.MaxSnapshotNameLength };

    public InputWindow(string heading, string message, string ok, string initial, Action<string> done)
    {
        Title = ProductInfo.Name;
        Icon = AppIcons.App;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _box.Text = initial;
        AutomationProperties.SetName(_box, heading);
        var save = Ui.Button(ok, () => { var v = Value; if (v.Length == 0) return; Close(); done(v); }, ButtonKind.Primary);
        save.IsDefault = true;
        var cancel = Ui.Button(L.T("Cancel"), Close, ButtonKind.Ghost);
        cancel.IsCancel = true;
        _box.TextChanged += (_, _) => save.IsEnabled = Value.Length > 0;
        Content = new StackPanel { Margin = new Thickness(22, 20, 22, 16), Spacing = 8, Children =
        {
            Ui.Text(heading, 15, FontWeight.SemiBold),
            Ui.Text(message, 12.5, brush: "InterfaceSyncMuted"),
            _box,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, save } },
        } };
        Opened += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public string Value => (_box.Text ?? "").Trim();
}

