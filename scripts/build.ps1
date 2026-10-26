param([ValidateSet('All','BepInEx','MelonLoader')][string]$Loader = 'All', [string]$OutDir)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
# Works from the main checkout or a git worktree nested inside it: find the game and dependency folders above.
$gameRoot = $modRoot; while ($gameRoot -and !(Test-Path -LiteralPath (Join-Path $gameRoot 'BALDI.exe'))) { $gameRoot = Split-Path $gameRoot -Parent }
if (!$gameRoot -and $env:BT_GAME_ROOT) { $gameRoot = $env:BT_GAME_ROOT }
if (!$gameRoot) { throw 'BALDI.exe was not found above this folder; set BT_GAME_ROOT to the game folder.' }
$depsRoot = $modRoot; while ($depsRoot -and !(Test-Path -LiteralPath (Join-Path $depsRoot 'deps/BepInEx-5.4.23.5'))) { $depsRoot = Split-Path $depsRoot -Parent }
if (!$depsRoot -and $env:BT_GAME_ROOT) { $depsRoot = Join-Path $env:BT_GAME_ROOT 'MultiplayerMod' }
if (!$depsRoot) { throw 'deps/BepInEx-5.4.23.5 was not found above this folder; set BT_GAME_ROOT to the game folder.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler is required.' }
$managed = Join-Path $gameRoot 'BALDI_Data/Managed'
$common = @('Transport.cs','SteamLink.cs','SteamOnline.cs','SteamLab.cs','LinkHealth.cs','CoopState.cs','Runtime.cs','Replication.cs','NpcInteractions.cs','NpcPresentation.cs','PrincipalSync.cs','NpcSyncLab.cs','HidingSync.cs','ShopSync.cs','ShopPatches.cs','FocusedSyncLab.cs','PlayerSprites.cs','SpriteDepthLab.cs','MenuUi.cs','NativeMenu.cs','CoopFeatures.cs','Patches.cs','Lab.cs') | ForEach-Object { Join-Path $modRoot "src/$_" }
$references = @((Join-Path $managed 'Assembly-CSharp.dll'), (Join-Path $managed 'Unity.TextMeshPro.dll'), (Join-Path $managed 'com.rlabrecque.steamworks.net.dll')) + @(Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine*.dll' | ForEach-Object FullName)
$common += Join-Path $modRoot 'src/MultiplayerAudio.cs'
$common += @('Spectator.cs','HostMigration.cs','Compatibility.cs','NpcExpansionLab.cs','RemoteElevators.cs') | ForEach-Object {Join-Path $modRoot "src/$_"}
$common += @('VendingSync.cs','ResultsSync.cs','DebugConsole.cs','CountdownSpawn.cs','BaldiContact.cs','LanDiscovery.cs','SpectatorHud.cs','TeamSounds.cs','LobbyLab.cs','NpcFeatures.cs','BeansSync.cs','CraftersSync.cs','PompSync.cs','TheTestSync.cs','ReflexSync.cs','ChalklesSync.cs','ItemsSync.cs','SpectatorUi.cs','EncounterLab.cs','PerfProbe.cs','HallMonitor.cs','HallMonitorSync.cs','HostOnlyStart.cs','FocusLab.cs') | ForEach-Object {Join-Path $modRoot "src/$_"}
$netstandard = Join-Path $managed 'netstandard.dll'
if (Test-Path -LiteralPath $netstandard) { $references += $netstandard }
foreach ($target in @('BepInEx','MelonLoader')) {
    if ($Loader -ne 'All' -and $Loader -ne $target) { continue }
    $destination = if ($OutDir) { Join-Path $OutDir $target } else { Join-Path $modRoot "artifacts/$target" }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    if ($target -eq 'BepInEx') {
        $core = Join-Path $depsRoot 'deps/BepInEx-5.4.23.5/BepInEx/core'
        $extra = @((Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll'))
        $entry = Join-Path $modRoot 'src/BepInExEntry.cs'
    } else {
        $core = Join-Path $depsRoot 'deps/MelonLoader-0.7.3/MelonLoader/net472'
        $extra = @((Join-Path $core 'MelonLoader.dll'), (Join-Path $core '0Harmony.dll'))
        $entry = Join-Path $modRoot 'src/MelonEntry.cs'
    }
    foreach ($reference in ($references + $extra)) { if (!(Test-Path -LiteralPath $reference)) { throw "Missing reference: $reference" } }
    $arguments = @('/nologo','/target:library','/optimize+','/langversion:5',"/out:$destination/BaldiTogether.$target.dll")
    $arguments += "/resource:$(Join-Path $modRoot 'assets/player-sheet.png'),BaldiTogether.PlayerSheet.png"
    $arguments += "/resource:$(Join-Path $modRoot 'assets/multiplayer-button.png'),BaldiTogether.MultiplayerButton.png"
    $arguments += "/resource:$(Join-Path $modRoot 'assets/BAL_HideSeek.wav'),BaldiTogether.BAL_HideSeek.wav"
    $arguments += "/resource:$(Join-Path $modRoot 'assets/connection-indicator.png'),BaldiTogether.ConnectionIndicator.png"
    foreach ($sound in @('ping','join','leave','caught','switch','found','hm-whistle','hm-scribble','hm-norunning','hm-together')) { $arguments += "/resource:$(Join-Path $modRoot "assets/sfx/$sound.wav"),BaldiTogether.Sfx.$sound.wav" }
    foreach ($art in @('patrol','writing','whistle','poster')) { $arguments += "/resource:$(Join-Path $modRoot "assets/npc/hall-monitor-$art.png"),BaldiTogether.Npc.hall-monitor-$art.png" }
    $arguments += ($references + $extra | ForEach-Object { "/reference:$_" })
    & $compiler @arguments @common $entry
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $target" }
    Write-Output "Built $target -> $destination"
}

