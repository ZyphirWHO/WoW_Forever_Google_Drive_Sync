# Interface Sync on a Mac

The Mac version is a menu-bar app (no Dock icon). It runs the same sync engine as the Windows version and
uses the same cloud folder layout, so a Mac and a Windows PC can share settings through the same Google Drive,
OneDrive, Dropbox or iCloud Drive account (on Windows, iCloud Drive needs Apple's free iCloud for Windows app).

> **Status: in development.** It is built and tested automatically on macOS, but hasn't been tried by a
> player on a real Mac yet.

## Install

1. Download the zip for your Mac from the project's GitHub Releases page (or a development build from
   **Actions → build → macos**):
   - `InterfaceSync-…-macos-arm64.dmg` (or `.zip`) for Apple silicon (M1 and later),
   - `InterfaceSync-…-macos-x64.dmg` (or `.zip`) for Intel Macs.
2. Open the `.dmg` (or unzip the `.zip`). It holds **Interface Sync.app** and **How to open Interface Sync.html**,
   an illustrated copy of the steps below. Drag the app onto **Applications** (not into a cloud folder).
3. Open it. The first time, macOS will stop it (see below). After that, a window shows what was found:
   your World of Warcraft folder, the game versions, and your cloud folder. Check them and click
   **Start syncing**, then **Finish**. If your cloud folder already has settings from another computer, the
   window asks for each game version whether to use the cloud's settings or this Mac's (the newer is
   preselected; the other copy is backed up).
4. Interface Sync then runs in the menu bar, at the top right of the screen.

## "Interface Sync can't be opened" / "Apple could not verify…"

Interface Sync isn't notarised by Apple yet (that needs a paid Apple Developer account), so Gatekeeper
blocks it the first time:

1. Click **Done** (or **OK**) on the warning.
2. Open **System Settings → Privacy & Security**, scroll down to the message about Interface Sync, and click
   **Open Anyway**. Confirm with your password or Touch ID.
3. Open Interface Sync again. macOS remembers your choice for this version.

On macOS 14 or older: Control-click the app in Applications, choose **Open**, then **Open** again.

When it first reads your cloud folder, macOS asks whether Interface Sync may access files in Google Drive,
OneDrive, Dropbox or iCloud Drive: click **Allow**. (Changed later in System Settings → Privacy & Security → Files and Folders.)

Check the file first: the release notes list the SHA-256 of each zip; in Terminal, `shasum -a 256` followed by
the zip's path must show the same value.

## What it finds on a Mac

| | Where it looks |
|---|---|
| World of Warcraft | Battle.net's list in `/Users/Shared/Battle.net/Agent/product.db`, then `/Applications/World of Warcraft` and `~/Applications/World of Warcraft` |
| Game versions | Each `_retail_`, `_classic_era_`… folder with a `World of Warcraft….app` |
| Google Drive | Drive's settings in `~/Library/Application Support/Google/DriveFS`, then `~/Library/CloudStorage/GoogleDrive-…/My Drive` |
| OneDrive | `~/Library/CloudStorage/OneDrive-Personal`, then `OneDrive-<organisation>` |
| Dropbox | `~/.dropbox/info.json`, then `~/Library/CloudStorage/Dropbox` or `~/Dropbox` |
| Its own data | `~/Library/Application Support/InterfaceSync` (settings, backups, logs) |

On macOS, Google Drive, OneDrive and Dropbox can keep files "online-only". Interface Sync checks every file
before copying and waits rather than copy a placeholder. To avoid waiting, Control-click the InterfaceSync
folder in Finder and choose your app's "keep on this device" option (Google Drive: *Offline access →
Available offline*; OneDrive: *Always Keep on This Device*; Dropbox: *Make available offline*).

macOS may ask once whether Interface Sync may access files in your cloud folder. Allow it, or it can't sync.

## Open at Login

Setup ticks **Open Interface Sync when I log in** by default; it can be switched later in **Settings** (click the menu-bar icon, then the gear)
(**Open at Login**). It is a per-user Login Item (`~/Library/LaunchAgents/io.github.zyphirwho.interfacesync.plist`),
so macOS shows "Background Items Added" once, and lists it in System Settings → General → Login Items, where it can
also be switched off. Open Interface Sync from Applications at least once: a copy run from Downloads or a disk image
can't be registered. If you move the app, open it once from its new place and the Login Item follows it.

## Not done yet on the Mac

- macOS notifications: needs a notarised app. Until then, anything that needs you shows in the panel that opens
  from the menu-bar icon, the icon gets a warning badge, and a conflict opens the main window.
- Notarisation, so Gatekeeper doesn't warn.
