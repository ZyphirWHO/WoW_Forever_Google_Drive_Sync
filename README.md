# WoWSync

Keeps World of Warcraft account settings (keybinds, macros, addon settings in `WTF\Account`)
the same on all your Windows PCs, through your own Google Drive.

> **Status: 2.0 in development.** The detection and sync engine are built and tested; the tray app,
> setup window and release downloads are coming next. v1.1 users: keep using v1.1 until a 2.0 release is published.

## How it works

- WoW only ever reads its own local files, so the game loads as fast as without WoWSync.
- When you close a game version, WoWSync copies its `WTF\Account` folder to
  `My Drive\WoWSync\<version>\WTF-Account`. Google Drive carries it to your other PCs.
- On your other PC, WoWSync copies the newer settings in, but only while that game version is closed.
- Every overwrite is preceded by a backup you can restore from the status window.
- `WTF\Config.wtf` (graphics and machine settings) is never synced. Addons themselves are not synced in 2.0.

## Repository layout

```
src/WoWSync.Core/          detection, game watcher, sync engine, backups (no UI)
tests/WoWSync.Core.Tests/  unit tests and simulated two-PC tests
```

## Building

```
dotnet test
```

## Licence

MIT, see [LICENSE](LICENSE).
