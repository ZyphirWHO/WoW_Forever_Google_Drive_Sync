# Interface Sync: help and questions

## How does it work?

Interface Sync copies your World of Warcraft `WTF\Account` folder (keybindings, macros and addon settings) to a
folder called `InterfaceSync` in your own cloud folder (Google Drive, OneDrive, Dropbox or iCloud Drive), and from
there to your other computers. Your cloud app does the uploading; Interface Sync never connects to it directly.

- It syncs **only while the game is closed**: when you quit, it uploads what changed; when another computer
  uploaded something newer, it downloads it before you next play.
- Every file it replaces is **backed up first** (the newest 5 backups per game version are kept).
- It never touches your game files, your addons or `Config.wtf` (graphics and sound, which differ per computer).

## The one rule

**Quit the game on one computer before playing on another.** If you play on two computers at once, both change the
same settings and Interface Sync can't merge them; it then asks you which copy to keep (the other is kept as a
backup).

## "Settings changed in two places"

Both computers changed the settings since they last synced. Open Interface Sync, click **Choose which to keep**,
and pick the copy you want. The newer one is suggested. The other copy is saved as a backup, so nothing is lost.

## How do I undo a sync, or a game update that reset my settings?

Click **Backups…** next to the game version. Pick a backup or snapshot and click **Restore**. Your current settings
are backed up first, and the restored ones are uploaded to your other computers.

When the game is updated, Interface Sync saves a copy of your settings first ("Before game update …").

## What is a snapshot?

A copy of your settings you save yourself, with a name, from **Backups… → Save snapshot**. Snapshots are kept
until you delete them.

## My cloud app keeps files "online only"

Interface Sync needs the files on your computer. Make the `InterfaceSync` folder available offline:

- **OneDrive:** right-click the folder → **Always keep on this device**.
- **iCloud Drive (Mac):** right-click → **Keep Downloaded**.
- **Dropbox:** right-click → **Make available offline**.
- **Google Drive:** right-click → **Offline access → Available offline**, or use "Mirror files".

## It found a copy of the InterfaceSync folder

Cloud apps sometimes make a copy ("InterfaceSync (1)") when two computers save at the same moment. Interface Sync
only uses the original and never deletes anything in your cloud folder. Look inside the copy, and delete it in your
cloud app when you're sure you don't need it.

## Windows says it doesn't recognise the app / macOS won't open it

The app isn't code-signed yet. On Windows, click **More info → Run anyway**. On a Mac, open System Settings →
Privacy & Security and click **Open Anyway**. See the "How to open" guide that comes with the download.

## How do I report a problem?

Open Interface Sync → **Settings → Advanced → Report a problem**. It opens a new GitHub issue with the diagnostics
filled in (folder names only, no personal paths). Check it, describe what happened, and click Submit.

## How do I uninstall it?

- **Windows:** Settings → Apps → Interface Sync → Uninstall. You can choose to also remove its backups and settings.
- **Mac:** quit it from the menu bar, then drag it from Applications to the Bin.

Your cloud folder is never touched by uninstalling.
