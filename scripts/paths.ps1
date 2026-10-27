# Dot-sourced by the lab scripts. The repo may sit inside the game folder (as MultiplayerMod) or anywhere else.
# Game folder: BT_GAME_ROOT, else the first folder above the repo that holds BALDI.exe, else the default Steam install.
$modRoot = Split-Path $PSScriptRoot -Parent
$gameRoot = $env:BT_GAME_ROOT
if (!$gameRoot) { $gameRoot = $modRoot; while ($gameRoot -and !(Test-Path -LiteralPath (Join-Path $gameRoot 'BALDI.exe'))) { $gameRoot = Split-Path $gameRoot -Parent } }
if (!$gameRoot) { $gameRoot = Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\Baldi's Basics Plus" }
if (!(Test-Path -LiteralPath (Join-Path $gameRoot 'BALDI.exe'))) { throw "BALDI.exe was not found in $gameRoot; set BT_GAME_ROOT to the game folder." }
# Test-owned game processes write their lab files (results, logs, saves) into this repo, wherever it is.
$workspaceArgs = @('-bt-workspace', ('"' + $modRoot + '"'))
# The MelonLoader lab client reads the host's command files from the repo's private/ folder.
$sharedArgs = @('-bt-shared', ('"' + $modRoot + '"'))
