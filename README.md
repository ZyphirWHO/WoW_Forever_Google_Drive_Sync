# Interface Sync

Keeps World of Warcraft account settings (keybinds, macros, addon settings in `WTF\Account`)
the same on all your Windows PCs, through your own Google Drive, OneDrive or Dropbox.

> **Status: 2.0 in development.** Detection, the sync engine and a minimal tray app are built.
> There is no release yet. v1.1 users: keep using v1.1 until a 2.0 release is published.

## How it works

- WoW only ever reads its own local files, so the game loads as fast as without Interface Sync.
- When you close a game version, Interface Sync copies its `WTF\Account` folder to
  `InterfaceSync\<version>\WTF-Account` in your cloud folder (Google Drive's "My Drive", OneDrive or Dropbox).
  The cloud app carries it to your other PCs. Use the same cloud app on every PC.
- On your other PC, Interface Sync copies the newer settings in, but only while that game version is closed.
- Every overwrite is preceded by a backup you can restore from the status window.
- `WTF\Config.wtf` (graphics and machine settings) is never synced. Addon files themselves are not synced:
  keep them the same with CurseForge (or your addon manager); their settings sync with `WTF\Account`.

## Repository layout

```
src/InterfaceSync.App/           tray app, first-run window, status window (WinForms)
src/InterfaceSync.Core/          detection, game watcher, sync engine, backups (no UI)
tests/InterfaceSync.Core.Tests/  unit tests and simulated two-PC tests
```

## Trying a development build

Every push to `main` builds `InterfaceSync.exe` on Windows. Open the latest green run under
**Actions → build**, download the `InterfaceSync-dev-...` artifact, unzip it and run `InterfaceSync.exe`.
Stop the v1.1 agent first. Development builds don't install themselves or start at login yet.

## Building

```
dotnet test
```

## Licence

MIT, see [LICENSE](LICENSE).
