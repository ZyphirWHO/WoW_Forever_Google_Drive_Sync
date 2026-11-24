# Installs the per-user setup silently, checks what it put where, then uninstalls and checks it all went,
# while settings in %LOCALAPPDATA%\InterfaceSync are kept.
param([string]$Folder = "installer")
$ErrorActionPreference = "Stop"
$props = [xml](Get-Content (Join-Path $PSScriptRoot "../Directory.Build.props"))
$name = $props.Project.PropertyGroup.ProductDisplayName | Where-Object { $_ } | Select-Object -First 1
$id = $props.Project.PropertyGroup.ProductId | Where-Object { $_ } | Select-Object -First 1
$setup = Get-ChildItem $Folder -Filter "$id-Setup-*.exe" | Select-Object -First 1
if (-not $setup) { throw "No installer in $Folder" }

# The in-app updater accepts an installer only if its product name is this product (padding ignored).
$product = $setup.VersionInfo.ProductName
"Installer product name: [$product] (char codes after the name: $((($product.ToCharArray() | Select-Object -Skip $name.Length | ForEach-Object { [int]$_ }) | Select-Object -Unique) -join ','))"
if (($product -replace '[\x00-\x1F]', '').Trim() -ne $name) { throw "Installer product name '$product' isn't '$name': the in-app updater would refuse it" }

$dir = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe = Join-Path $dir "$id.exe"
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{CA182A62-B7B7-4430-BA89-B0C0D2C05F61}_is1"
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$data = Join-Path $env:LOCALAPPDATA $id

# WaitForExit, not -Wait: -Wait also waits for child processes, and a silent install reopens the app.
$p = Start-Process $setup.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$PWD\install.log" -PassThru
if (-not $p.WaitForExit(180000)) { throw "Setup didn't finish within 3 minutes" }
if ($p.ExitCode -ne 0) { Get-Content install.log -Tail 40; throw "Setup exit code $($p.ExitCode)" }
foreach ($path in $exe, $shortcut, (Join-Path $dir "unins000.exe")) { if (-not (Test-Path $path)) { throw "Missing after install: $path" } }
# A silent install opens the app again (that's how in-app updates finish): close it before going on.
Start-Sleep -Seconds 3
$opened = Get-Process -Name $id -ErrorAction SilentlyContinue
if (-not $opened) { throw "A silent install should reopen $name (the in-app update relies on it)" }
$opened | Stop-Process -Force
Start-Sleep -Seconds 1
$entry = Get-ItemProperty $uninstallKey
"Installed: $($entry.DisplayName) $($entry.DisplayVersion) in $($entry.InstallLocation)"
if ($entry.DisplayName -ne $name) { throw "Apps & features shows '$($entry.DisplayName)'" }
if ((Get-Item $exe).VersionInfo.ProductName -ne $name) { throw "Installed exe has the wrong product name" }

# What the app itself writes: start with Windows, and a settings file the uninstaller must leave alone.
Set-ItemProperty $run -Name $id -Value "`"$exe`" --tray"
New-Item -ItemType Directory $data -Force | Out-Null
Set-Content (Join-Path $data "keep-me.txt") "settings"

$p = Start-Process (Join-Path $dir "unins000.exe") -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru
if (-not $p.WaitForExit(180000)) { throw "Uninstall didn't finish within 3 minutes" }
if ($p.ExitCode -ne 0) { throw "Uninstall exit code $($p.ExitCode)" }
# The uninstaller finishes from a temporary copy of itself: wait for it.
$deadline = (Get-Date).AddSeconds(60)
while ((Test-Path $exe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
foreach ($path in $exe, $shortcut) { if (Test-Path $path) { throw "Still there after uninstall: $path" } }
if (Test-Path $uninstallKey) { throw "Apps & features entry left behind" }
if ((Get-ItemProperty $run -ErrorAction SilentlyContinue).$id) { throw "Start-with-Windows entry left behind" }
if (-not (Test-Path (Join-Path $data "keep-me.txt"))) { throw "Uninstall removed the player's settings" }

# Asked to (the "also remove settings and backups" answer), uninstalling removes the data folder, and only that.
$p = Start-Process $setup.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru
if (-not $p.WaitForExit(180000)) { throw "Second setup didn't finish within 3 minutes" }
Start-Sleep -Seconds 3
Get-Process -Name $id -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$neighbour = Join-Path $env:LOCALAPPDATA "$id-neighbour-test"
New-Item -ItemType Directory $neighbour -Force | Out-Null
$p = Start-Process (Join-Path $dir "unins000.exe") -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/REMOVEDATA" -PassThru
if (-not $p.WaitForExit(180000)) { throw "Second uninstall didn't finish within 3 minutes" }
$deadline = (Get-Date).AddSeconds(60)
while ((Test-Path $data) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (Test-Path $data) { throw "Uninstall with /REMOVEDATA left $data" }
if (-not (Test-Path $neighbour)) { throw "Uninstall removed a folder next to the data folder" }
Remove-Item $neighbour -Recurse -Force
"Installer OK: installs per user, uninstalls cleanly, keeps settings unless asked to remove them."
