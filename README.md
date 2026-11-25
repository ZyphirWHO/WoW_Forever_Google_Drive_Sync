# Interface Sync

**Syncs your World of Warcraft® interface settings between your Windows PCs and Macs** (keybindings, macros
and addon settings) through your own Google Drive, OneDrive, Dropbox or iCloud Drive.

Interface Sync is an independent, free, open-source tool. It is not affiliated with, endorsed by or
sponsored by Blizzard Entertainment, Inc.

> **Download** the latest version from the [Releases page](https://github.com/ZyphirWHO/WoW_Forever_Google_Drive_Sync/releases/latest):
> `InterfaceSync-Setup-<version>.exe` for Windows, the `.dmg` for your Mac. Releases marked *Pre-release* are test builds.

## How it works

- The game only ever reads its own local files, so it loads as fast as without Interface Sync.
- When you close a game version, Interface Sync copies its `WTF\Account` folder to
  `InterfaceSync\<version>\WTF-Account` in your cloud folder (Google Drive's "My Drive", OneDrive, Dropbox or iCloud Drive).
  The cloud app carries it to your other PCs. Use the same cloud app on every PC.
- On your other PC, Interface Sync copies the newer settings in, but only while that game version is closed.
- Every overwrite, on your PC or in the cloud, is preceded by a backup in `%LOCALAPPDATA%\InterfaceSync\Backups`.
  Only the files about to change are backed up (so a download writes a few hundred KB, not the whole folder), and
  each game version's **Backups…** button puts the settings back as they were at any backup. At most 5 are kept per
  game version; older ones are deleted. The newest backup of this PC's own settings, of the
  cloud copy, and of a copy you set aside in a "changed in two places" choice are always among the 5.
  **Snapshots** you save yourself (with a name) are kept until you delete them, and a copy is saved automatically
  the first time a new game version is seen (the last 3 game updates). Any backup can be saved as a zip.
- `WTF\Config.wtf` (graphics and machine settings) is never synced. Addon files themselves are not synced:
  keep them the same with CurseForge (or your addon manager); their settings sync with `WTF\Account`.
- **Activity** shows what synced, in plain words ("Downloaded Details, WeakAuras and Key bindings from LAPTOP"),
  and **Your computers** lists every computer that syncs. Each computer writes a small file
  (`InterfaceSync\<version>\Computers\<name>.json`) after it syncs; nothing is written while idle.
- **Choose what syncs:** leave out characters or addons on one computer; they stay as they are in the cloud for
  the others. Addons you've removed stop syncing on that computer, and their leftover settings can be cleaned up.
- **Pause for a while** (1 hour, 4 hours, until tomorrow morning, or until you resume), **notifications** your way
  (all, only when needed, none), and the interface in **English, German, French, Spanish or Portuguese**.
- **Help → Report a problem** opens a GitHub issue with the diagnostics filled in (folder paths, computer and user
  names replaced), for you to check before sending. See [docs/FAQ.md](docs/FAQ.md) for help.
- It never asks for administrator rights and runs no scripts.
- Updates: once a day (and when it starts) it asks GitHub whether a newer release of this project is out. That
  request is anonymous. On Windows, **Update now** downloads the new installer, checks its SHA-256 against the one
  GitHub publishes for the release, then quits and lets the installer update it in place and reopen it. On a Mac,
  it downloads the new disk image in your browser. Switch the daily check off in Settings.

## Repository layout

```
src/InterfaceSync.Desktop/        the app for Windows and macOS (Avalonia): tray/menu-bar icon and panel,
                                  main window (Home, Activity, Backups, Computers, Settings), setup
src/InterfaceSync.Core/           detection, game watcher, sync engine, backups (no UI)
tests/InterfaceSync.Core.Tests/   unit tests and simulated two-PC tests
tests/InterfaceSync.Desktop.Tests/ window tests (headless, run on every OS; CI also saves screenshots)
packaging/windows/                Inno Setup script and wizard images for the per-user installer
packaging/macos/                  Info.plist template and the script that builds the .app and .dmg
packaging/guides/                 "How to open" pages shipped with the downloads
assets/icon/                      icon master (SVG) and the script that builds the .ico files
```

The product name is set once, in `Directory.Build.props` (`ProductDisplayName`, `ProductId`); everything
else derives from it, and CI fails if the built exe's product name differs.

## Trying a development build

Every push to `main` builds and tests everything. Open the latest green run under **Actions → build**:

- **Windows:** download `InterfaceSync-Setup-dev-...` and run `InterfaceSync-Setup-<version>.exe`. It installs for
  you only (no administrator rights) to `%LOCALAPPDATA%\Programs\Interface Sync`, adds a Start menu entry, and
  can be removed in Settings → Apps. Uninstalling asks whether to also remove your settings, backups and logs
  (`%LOCALAPPDATA%\InterfaceSync`); the answer defaults to keeping them.
  `InterfaceSync-dev-...` holds the same app as a single exe, for running without installing.
- **Mac:** download `InterfaceSync-macos-dev-...`, open the `.dmg` for your Mac (arm64 for Apple silicon, x64
  for Intel) and drag Interface Sync onto Applications. See [docs/MAC.md](docs/MAC.md) for the first-open steps.

## Docs

- [docs/FAQ.md](docs/FAQ.md): help and common questions
- [docs/MAC.md](docs/MAC.md): the Mac version: install, the Gatekeeper warning, what it finds
- [docs/ANTIVIRUS.md](docs/ANTIVIRUS.md): what to do if your browser, Windows or antivirus warns about the download
- [docs/DISTRIBUTION-PLAN.md](docs/DISTRIBUTION-PLAN.md): plan for a Microsoft Store (MSIX) package and winget

## Building

```
dotnet test
```

Release builds are made only by GitHub Actions from this repository (see the code signing policy below);
nothing is built by hand.

## Code signing policy

Releases are built by GitHub Actions from the public source in this repository, with a build-provenance
attestation for each exe. Code signing is being set up with the SignPath Foundation (application under
review); until then releases are unsigned.

Team roles:

| Role | Members |
|---|---|
| Authors (committers) | [ZyphirWHO](https://github.com/ZyphirWHO) |
| Reviewers | [ZyphirWHO](https://github.com/ZyphirWHO) |
| Approvers | [ZyphirWHO](https://github.com/ZyphirWHO) |

Every release requires manual approval by an approver (the `release` environment in GitHub Actions) before
it is published.

Privacy: this program does not transfer any information to other networked systems unless specifically
requested by the user. It copies your settings files only to the cloud folder you choose on your PC; your
cloud app (Google Drive, OneDrive, Dropbox or iCloud Drive) does the uploading.

## Licence and dependencies

Interface Sync is licensed under the MIT License (OSI-approved), see [LICENSE](LICENSE).

The program uses the .NET runtime (MIT) and Avalonia (MIT), with SkiaSharp and HarfBuzzSharp (MIT). On Windows it
also uses CommunityToolkit.WinUI.Notifications (MIT) for notifications with buttons. Development-only dependencies, all open source:

| Used for | Component | Licence |
|---|---|---|
| Tests | xUnit, xunit.runner.visualstudio | Apache-2.0 |
| Tests | Microsoft.NET.Test.Sdk, coverlet.collector | MIT |
| Mac window tests | Avalonia.Headless.XUnit | MIT |
| Rebuilding the icon (not shipped) | CairoSVG | LGPL-3.0 |
| Rebuilding the icon (not shipped) | Pillow | MIT-CMU |

## Trademarks

World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc.,
in the U.S. and/or other countries. Interface Sync is not affiliated with or endorsed by Blizzard
Entertainment. The names are used only to describe what the program works with.
