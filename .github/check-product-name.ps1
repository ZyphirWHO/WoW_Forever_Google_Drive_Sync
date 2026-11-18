# Fails unless the publish folder holds only <ProductId>.exe and its version info carries the product name
# set in Directory.Build.props (SignPath requires the product name attributes to match in every build).
param([string]$Folder = "publish")
$ErrorActionPreference = "Stop"
$props = [xml](Get-Content (Join-Path $PSScriptRoot "../Directory.Build.props"))
$name = $props.Project.PropertyGroup.ProductDisplayName | Where-Object { $_ } | Select-Object -First 1
$id = $props.Project.PropertyGroup.ProductId | Where-Object { $_ } | Select-Object -First 1
$files = @(Get-ChildItem $Folder)
if ($files.Count -ne 1 -or $files[0].Name -ne "$id.exe") { throw "Publish must produce only $id.exe, found: $($files.Name -join ', ')" }
$info = (Get-Item (Join-Path $Folder "$id.exe")).VersionInfo
"ProductName: $($info.ProductName)  FileDescription: $($info.FileDescription)  CompanyName: $($info.CompanyName)  Version: $($info.ProductVersion)"
if ($info.ProductName -ne $name) { throw "ProductName '$($info.ProductName)' differs from '$name'" }
if ($info.FileDescription -ne $name) { throw "FileDescription '$($info.FileDescription)' differs from '$name'" }
"SHA-256: $((Get-FileHash (Join-Path $Folder "$id.exe") -Algorithm SHA256).Hash)"
