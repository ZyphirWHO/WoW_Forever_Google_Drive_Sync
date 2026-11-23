# Interface Sync

**Syncs your World of Warcraft® interface settings between your Windows PCs and Macs** (keybindings, macros
and addon settings) through your own Google Drive, OneDrive or Dropbox.

Interface Sync is an independent, free, open-source tool. It is not affiliated with, endorsed by or
sponsored by Blizzard Entertainment, Inc.

> **Status: 3.0 in development.** Detection, the sync engine and the tray app are built and tested.
> There is no release yet.

## How it works

- The game only ever reads its own local files, so it loads as fast as without Interface Sync.
- When you close a game version, Interface Sync copies its `WTF\Account` folder to
  `InterfaceSync\<version>\WTF-Account` in your cloud folder (Google Drive's "My Drive", OneDrive or Dropbox).
  The cloud app carries it to your other PCs. Use the same cloud app on every PC.
- On your other PC, Interface Sync copies the newer settings in, but only while that game version is closed.
- Every overwrite, on your PC or in the cloud, is preceded by a backup in `%LOCALAPPDATA%\InterfaceSync\Backups`.
  At most 5 are kept per game version; older ones are deleted. The newest backup of this PC's own settings, of the
  cloud copy, and of a copy you set aside in a "changed in two places" choice are always among the 5.
- `WTF\Config.wtf` (graphics and machine settings) is never synced. Addon files themselves are not synced:
  keep them the same with CurseForge (or your addon manager); their settings sync with `WTF\Account`.
- It never asks for administrator rights and runs no scripts.
- Updates: once a day (and when it starts) it asks GitHub whether a newer release of this project is out. That
  request is anonymous. On Windows, **Update now** downloads the new installer, checks its SHA-256 against the one
  GitHub publishes for the release, then quits and lets the installer update it in place and reopen it. On a Mac,
  it downloads the new disk image in your browser. Switch the daily check off in Settings.

## Repository layout

```
src/InterfaceSync.App/            Windows tray app, setup and status windows (WinForms)
src/InterfaceSync.Mac/            macOS menu-bar app, setup and status windows (Avalonia)
src/InterfaceSync.Core/           detection, game watcher, sync engine, backups (no UI)
tests/InterfaceSync.Core.Tests/   unit tests and simulated two-PC tests
tests/InterfaceSync.App.Tests/    Windows window smoke tests (run on Windows)
tests/InterfaceSync.Mac.Tests/    Mac window tests (headless, run on every OS)
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
  can be removed in Settings → Apps. Your settings, backups and logs stay in `%LOCALAPPDATA%\InterfaceSync`.
  `InterfaceSync-dev-...` holds the same app as a single exe, for running without installing.
- **Mac:** download `InterfaceSync-macos-dev-...`, open the `.dmg` for your Mac (arm64 for Apple silicon, x64
  for Intel) and drag Interface Sync onto Applications. See [docs/MAC.md](docs/MAC.md) for the first-open steps.

## Docs

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
cloud app (Google Drive, OneDrive or Dropbox) does the uploading.

## Licence and dependencies

Interface Sync is licensed under the MIT License (OSI-approved), see [LICENSE](LICENSE).

The Windows program depends only on the .NET runtime (MIT). The Mac program also uses Avalonia (MIT), with
SkiaSharp and HarfBuzzSharp (MIT). Development-only dependencies, all open source:

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
