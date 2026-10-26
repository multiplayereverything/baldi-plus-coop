param([ValidateSet('BepInEx','MelonLoader')][string]$Loader = 'BepInEx', [string]$GameRoot)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
if (!$GameRoot) { $GameRoot = Split-Path $modRoot -Parent }
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (!(Test-Path -LiteralPath (Join-Path $GameRoot 'BALDI.exe'))) { throw 'Target must contain BALDI.exe.' }
if (Get-Process BALDI -ErrorAction SilentlyContinue) { throw 'Close Baldi before installing or switching loaders.' }
$binary = Join-Path $modRoot "artifacts/$Loader/BaldiTogether.$Loader.dll"
if (!(Test-Path -LiteralPath $binary)) { throw 'Run scripts/build.ps1 first.' }
if ($Loader -eq 'BepInEx' -and (Test-Path -LiteralPath (Join-Path $GameRoot 'version.dll'))) { throw 'MelonLoader or another version.dll proxy is present. Use a separate game copy, or uninstall that loader first.' }
if ($Loader -eq 'MelonLoader' -and (Test-Path -LiteralPath (Join-Path $GameRoot 'winhttp.dll'))) { throw 'BepInEx or another winhttp.dll proxy is present. Use a separate game copy, or uninstall that loader first.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $modRoot "private/backups/$stamp-$Loader"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$saveRoot = Join-Path $env:USERPROFILE "AppData/LocalLow/Basically Games/Baldi's Basics Plus"
if (Test-Path -LiteralPath $saveRoot) { Copy-Item -LiteralPath $saveRoot -Destination (Join-Path $backupRoot 'saves') -Recurse }
@{ gameRoot=$GameRoot; loader=$Loader; saveRoot=$saveRoot; savePresent=(Test-Path -LiteralPath $saveRoot); timestamp=$stamp } | ConvertTo-Json | Set-Content (Join-Path $backupRoot 'installation.json')
if ($Loader -eq 'BepInEx') {
    $source = Join-Path $modRoot 'deps/BepInEx-5.4.23.5'
    $destination = Join-Path $GameRoot 'BepInEx/plugins/BaldiTogether'
} else {
    $source = Join-Path $modRoot 'deps/MelonLoader-0.7.3'
    $destination = Join-Path $GameRoot 'Mods'
}
# Preserve every pre-existing loader file that this installation will replace.
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = $file.FullName.Substring($source.Length + 1)
    $target = Join-Path $GameRoot $relative
    if (Test-Path -LiteralPath $target) {
        $backup = Join-Path $backupRoot "loader/$relative"
        New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
        Copy-Item -LiteralPath $target -Destination $backup
    }
}
foreach ($item in Get-ChildItem -LiteralPath $source -Force) { Copy-Item -LiteralPath $item.FullName -Destination $GameRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$pluginTarget = Join-Path $destination "BaldiTogether.$Loader.dll"
if (Test-Path -LiteralPath $pluginTarget) { Copy-Item -LiteralPath $pluginTarget -Destination (Join-Path $backupRoot 'previous-plugin.dll') }
Copy-Item -LiteralPath $binary -Destination $pluginTarget -Force
Write-Output "Installed $Loader and Baldi Together in $GameRoot"
Write-Output "Backup: $backupRoot"
