using InterfaceSync.Core.Platform;
using InterfaceSync.Core.Service;
using InterfaceSync.Core.Updates;

namespace InterfaceSync.Desktop;

/// <summary>What the windows need from the app (lets tests build them without a tray or a real update check).</summary>
internal interface IAppHost
{
    SyncService Service { get; }

    /// <summary>Runs work in the background, one at a time with polling; then <paramref name="afterOnUi"/> on the UI thread.</summary>
    void Run(Action work, Action? afterOnUi = null);

    Updater? Updater => null;
    IStartAtLogin? StartAtLogin => null;

    /// <summary>Update now: Windows downloads and runs the checked installer; a Mac opens the disk image's download.</summary>
    void InstallUpdate() { }

    /// <summary>Check now (also brings the update message into view when the answer comes).</summary>
    void CheckForUpdates()
    {
        if (Updater is { } u) _ = u.CheckAsync();
    }

    /// <summary>Shows the main window on a page (the tray panel's links).</summary>
    void ShowMain(string? page = null) { }
}
