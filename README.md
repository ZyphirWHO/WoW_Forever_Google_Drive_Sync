# WoWSync

Keeps World of Warcraft account settings (keybinds, macros, addon settings in `WTF\Account`)
the same on all your Windows PCs, through your own Google Drive.

> **Status: 2.0 in development.** Detection, the sync engine and a minimal tray app are built.
> There is no release yet. v1.1 users: keep using v1.1 until a 2.0 release is published.

## How it works

- WoW only ever reads its own local files, so the game loads as fast as without WoWSync.
- When you close a game version, WoWSync copies its `WTF\Account` folder to
  `My Drive\WoWSync\<version>\WTF-Account`. Google Drive carries it to your other PCs.
- On your other PC, WoWSync copies the newer settings in, but only while that game version is closed.
- Every overwrite is preceded by a backup you can restore from the status window.
- `WTF\Config.wtf` (graphics and machine settings) is never synced. Addons themselves are not synced in 2.0.

## Repository layout

```
src/WoWSync.App/           tray app, first-run window, status window (WinForms)
src/WoWSync.Core/          detection, game watcher, sync engine, backups (no UI)
tests/WoWSync.Core.Tests/  unit tests and simulated two-PC tests
```

## Trying a development build

Every push to `main` builds `WoWSync.exe` on Windows. Open the latest green run under
**Actions → build**, download the `WoWSync-dev-...` artifact, unzip it and run `WoWSync.exe`.
Stop the v1.1 agent first. Development builds don't install themselves or start at login yet.

## Building

```
dotnet test
```

## Licence

MIT, see [LICENSE](LICENSE).
