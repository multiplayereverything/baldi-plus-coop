$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$release = Join-Path $modRoot 'artifacts/release'
$versionMatch = [regex]::Match((Get-Content -LiteralPath (Join-Path $modRoot 'src/Runtime.cs') -Raw), 'ModVersion = "([^"]+)"')
if (!$versionMatch.Success) { throw 'Cannot determine mod version.' }
$modVersion = $versionMatch.Groups[1].Value
New-Item -ItemType Directory -Force -Path $release | Out-Null
foreach ($loader in @('BepInEx','MelonLoader')) {
    $stage = Join-Path $release "BaldiTogether-$loader-$modVersion"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $pluginDir = if ($loader -eq 'BepInEx') { Join-Path $stage 'BepInEx/plugins/BaldiTogether' } else { Join-Path $stage 'Mods' }
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $modRoot "artifacts/$loader/BaldiTogether.$loader.dll") -Destination $pluginDir -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'README.md') -Destination $stage -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'LICENSE') -Destination $stage -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'CHANGELOG.md') -Destination $stage -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'assets/README.md') -Destination (Join-Path $stage 'ARTWORK.md') -Force
    $publicDocs = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Force -Path $publicDocs | Out-Null
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/CONTACT-0.5.3.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/FIXES-0.5.4.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/LOBBY-0.6.0.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/TESTS-0.6.3.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/NPC-COVERAGE-0.7.0.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/PERFORMANCE-0.7.1.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/HALL-MONITOR-0.8.0.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/ONLINE-0.9.0.md') -Destination $publicDocs -Force
    foreach($document in @('VENDING-0.5.2.md','RESULTS-0.5.2.md','DEBUG-CONSOLE-0.5.2.md','COUNTDOWN-0.5.2.md')){Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force}
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/CAPTURE-0.5.1.md') -Destination $publicDocs -Force
    foreach($document in @('MULTIPLAYER-0.5.0.md','NPC-COVERAGE-0.5.0.md')){Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force}
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/HIDESEEK-AUDIO-0.4.13.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/LOCKER-VISIBILITY-0.4.12.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/PAGE-TRANSITIONS-0.4.11.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/SPRITE-DEPTH-0.4.3.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/SPRITE-STACK-0.4.5.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/ELEVATORS-0.4.6.md') -Destination $publicDocs -Force
    foreach($document in @('BACK-TRANSITION-0.4.10.md','back-dissolve-0.4.10.png')) {Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force}
    foreach($document in @('TITLE-TRANSITION-0.4.9.md','title-art-dissolve-0.4.9.png')) {Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force}
    foreach($document in @('BUTTON-ART-0.4.8.md','multiplayer-button-0.4.8.png')) {Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force}
    foreach($document in @('OPTIONS-0.4.7.md','options-player-0.4.7.png','options-lobby-0.4.7.png')) {Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force}
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/TRANSITION-0.4.4.md') -Destination $publicDocs -Force
    Copy-Item -LiteralPath (Join-Path $modRoot 'docs/multiplayer-dissolve-0.4.4.png') -Destination $publicDocs -Force
    foreach ($document in @('UI-0.2.1.md','menu-0.2.1.png','lobby-0.2.1.png','join-0.2.1.png','error-0.2.1.png','room-0.2.1.png','lobby-small-0.2.1.png','SPRITES-0.2.2.md','player-sprite-0.2.2.png','SYNC-0.3.md','SYNC-0.3.1.md','SHOP-SYNC.md','NPC-SYNC-0.4.md','UI-0.4.1.md','multiplayer-menu-0.4.1.png','multiplayer-host-0.4.1.png','multiplayer-join-0.4.1.png','multiplayer-small-0.4.1.png','ASPECT-0.4.2.md','multiplayer-menu-0.4.2.png','multiplayer-classic-0.4.2.png')) {
        Copy-Item -LiteralPath (Join-Path $modRoot "docs/$document") -Destination $publicDocs -Force
    }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $release "BaldiTogether-$loader-$modVersion.zip") -Force
}
Get-ChildItem -LiteralPath $release -Filter '*.zip' | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash
