# Interface Sync

**Syncs your World of Warcraft® interface settings between your Windows PCs** (keybindings, macros and
addon settings) through your own Google Drive, OneDrive or Dropbox.

Interface Sync is an independent, free, open-source tool. It is not affiliated with, endorsed by or
sponsored by Blizzard Entertainment, Inc.

> **Status: 2.0 in development.** Detection, the sync engine and the tray app are built and tested.
> There is no release yet.

## How it works

- The game only ever reads its own local files, so it loads as fast as without Interface Sync.
- When you close a game version, Interface Sync copies its `WTF\Account` folder to
  `InterfaceSync\<version>\WTF-Account` in your cloud folder (Google Drive's "My Drive", OneDrive or Dropbox).
  The cloud app carries it to your other PCs. Use the same cloud app on every PC.
- On your other PC, Interface Sync copies the newer settings in, but only while that game version is closed.
- Every overwrite, on your PC or in the cloud, is preceded by a backup in `%LOCALAPPDATA%\InterfaceSync\Backups`.
- `WTF\Config.wtf` (graphics and machine settings) is never synced. Addon files themselves are not synced:
  keep them the same with CurseForge (or your addon manager); their settings sync with `WTF\Account`.
- It never asks for administrator rights, runs no scripts and starts no other programs.

## Repository layout

```
src/InterfaceSync.App/            tray app, setup and status windows (WinForms)
src/InterfaceSync.Core/           detection, game watcher, sync engine, backups (no UI)
tests/InterfaceSync.Core.Tests/   unit tests and simulated two-PC tests
tests/InterfaceSync.App.Tests/    window smoke tests (run on Windows)
assets/icon/                      icon master (SVG) and the script that builds the .ico files
```

The product name is set once, in `Directory.Build.props` (`ProductDisplayName`, `ProductId`); everything
else derives from it, and CI fails if the built exe's product name differs.

## Trying a development build

Every push to `main` builds `InterfaceSync.exe` on Windows. Open the latest green run under
**Actions → build**, download the `InterfaceSync-dev-...` artifact, unzip it outside your cloud folders
and run `InterfaceSync.exe`. Development builds don't install themselves or start at login yet.

## Docs

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

The shipped program depends only on the .NET runtime (MIT). Development-only dependencies, all open source:

| Used for | Component | Licence |
|---|---|---|
| Tests | xUnit, xunit.runner.visualstudio | Apache-2.0 |
| Tests | Microsoft.NET.Test.Sdk, coverlet.collector | MIT |
| Rebuilding the icon (not shipped) | CairoSVG | LGPL-3.0 |
| Rebuilding the icon (not shipped) | Pillow | MIT-CMU |

## Trademarks

World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc.,
in the U.S. and/or other countries. Interface Sync is not affiliated with or endorsed by Blizzard
Entertainment. The names are used only to describe what the program works with.
