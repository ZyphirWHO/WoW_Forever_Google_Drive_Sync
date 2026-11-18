# Later distribution: Microsoft Store (MSIX) and winget

Plan only; nothing here is built yet.

## Why MSIX through the Microsoft Store

An MSIX package published through the Microsoft Store is re-signed by Microsoft, so players get no
SmartScreen warning and antivirus products trust it like any Store app. It is the only route that needs no
code-signing certificate of our own and doesn't start every new version with zero reputation. Store
listing is free for individual developers.

winget (`winget install InterfaceSync`) can point at either the Store package or the GitHub Release.

## Steps

1. Reserve the name "Interface Sync" in Partner Center (individual developer account).
2. Add a packaging project or a `makeappx`-based CI job that wraps the same `InterfaceSync.exe` published by
   `release.yml` (no second build path).
3. Package manifest: `runFullTrust` desktop app (Windows.FullTrustApplication entry point), identity from
   Partner Center, logos generated from `assets/icon/app.svg` by the existing icon script.
4. Submit to the Store (they sign it); keep the zip on GitHub Releases for players who prefer it.
5. winget: submit a manifest to `microsoft/winget-pkgs`, either `msstore` source (Store ID) or a portable
   installer pointing at the release zip with its SHA-256.

## What in the current design would block or change under MSIX

| Current design | Under MSIX | Change needed |
|---|---|---|
| Start at login via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (planned) | Packaged apps can't rely on Run keys; they use a **StartupTask** extension, which the user can turn off in Settings → Apps → Startup | Add `windows.startupTask` to the manifest; in the app, use `StartupTask` APIs when packaged, the Run key when not |
| Self-install: copy itself to `%LOCALAPPDATA%\Programs\InterfaceSync`, Start-menu shortcut (planned) | The package installs to WindowsApps; the Store creates the Start-menu entry | Skip self-install when running packaged |
| "Uninstall" button (planned) | Uninstall is done by Windows (Settings → Apps) | Hide the button when packaged |
| Own data in `%LOCALAPPDATA%\InterfaceSync` (settings, state, backups, logs) | Writes to the user's AppData are redirected to the package's private folder and **deleted on uninstall** | Fine for settings/state; tell players backups go with the app, or keep backups in `Documents\InterfaceSync Backups` |
| Reading other apps' files: `%LOCALAPPDATA%\Google\DriveFS\...`, `%APPDATA%\Dropbox\info.json`, `C:\ProgramData\Battle.net\Agent\product.db` | Reads of real AppData/ProgramData still work (merged view) | Verify on a packaged build; no change expected |
| Registry reads (HKCU DriveFS/OneDrive keys, HKLM hints) | Reads work; HKCU **writes** are virtualized per package | Only the Run key writes to HKCU today, which StartupTask replaces |
| Writing to the game folder and the cloud folder | Allowed for full-trust apps (user files outside the package) | None |
| Process list (`QueryFullProcessImageName`) to see the game running | Allowed for full-trust apps | None |
| Single-instance mutex / named event in the `Local\` namespace | Works | None |
| Update check against GitHub Releases (planned) | The Store updates the app; a second updater is confusing and against Store policy | Disable the update check when packaged |
| `runFullTrust` restricted capability | Needs a short justification in the Store submission | Explain: tray app that copies the user's game settings between folders |

Rule for all of the above: detect "running packaged" once at start
(`Windows.ApplicationModel.Package.Current` succeeds) and switch these behaviours, so the same exe serves the
zip and the Store.
