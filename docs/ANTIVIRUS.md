# If your browser, Windows or antivirus warns about Interface Sync

Interface Sync is a small free tool, built in public on GitHub. It isn't code-signed yet, so browsers,
Windows SmartScreen and some antivirus programs may warn about it, especially in the first weeks of each
new version. Here's how to check the file is genuine and what to do about each warning.

## 1. Check the file is genuine first

- Download Interface Sync **only** from the project's GitHub **Releases** page.
- Each release lists the SHA-256 of `InterfaceSync.exe` and of the zip. In PowerShell, in the folder where
  you unzipped it, run:

  ```
  Get-FileHash .\InterfaceSync.exe
  ```

  The `Hash` must match the release notes exactly. If it doesn't, delete the file and don't run it.
- Unzip it **outside** Google Drive, OneDrive and Dropbox (for example in your Downloads folder). Keeping the
  program inside a synced folder can make antivirus scan and flag it again whenever the cloud app re-downloads it.

## 2. Your browser blocked the download

- **Chrome:** open `chrome://downloads`, find the file and choose **Download dangerous file** (or **Keep**).
- **Edge:** open the Downloads panel, select the **...** next to the file, choose **Keep**, then
  **Show more → Keep anyway**.

Then check the SHA-256 as in step 1.

## 3. "Windows protected your PC" (SmartScreen)

Click **More info**, then **Run anyway**.

## 4. Microsoft Defender quarantined it

Open **Windows Security → Virus & threat protection → Protection history**, select the Interface Sync item,
then **Actions → Allow on device**. Download and unzip it again, and check the SHA-256.

## 5. Other antivirus (Norton, Bitdefender, Avast and similar)

Restore it from quarantine and add an exception for **that one file**, `InterfaceSync.exe` in the folder
where you put it. Don't exclude a whole drive or your Downloads folder.

## 6. Work or school PC with managed antivirus (such as Sophos or CrowdStrike)

Don't try to get around it. Ask your IT team to allow the file by its SHA-256 hash; the release notes give
them what they need. Exclusions by folder often don't cover the machine-learning detections these products use.

## 7. Never turn your antivirus off to run Interface Sync

---

World of Warcraft and Warcraft are trademarks or registered trademarks of Blizzard Entertainment, Inc., in the
U.S. and/or other countries. Interface Sync is not affiliated with or endorsed by Blizzard Entertainment.
