param([switch]$TestActions, [switch]$SpriteTour, [switch]$FocusedSync, [switch]$FlowSync, [switch]$NpcSync, [switch]$LobbyTour, [switch]$LanTour, [switch]$Encounter, [switch]$Soak, [switch]$Profile, [switch]$Visible, [string]$Seed, [string]$Chaos, [int]$StallHost, [switch]$NoKeepAlive)
# -Visible shows both windows (needed for focus and Alt+Tab tests; focus cannot move to a hidden window).
$windowStyle = if ($Visible) { 'Normal' } else { 'Hidden' }
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$gameRoot = Split-Path $modRoot -Parent
$labRoot = Join-Path $modRoot 'private/melon-lab'
if (!(Test-Path -LiteralPath (Join-Path $labRoot 'BALDI.exe'))) { throw 'Create the private MelonLoader lab first. See MODLOG.md.' }
foreach ($name in @('host','client')) {
    $file = Join-Path $modRoot "private/$name-pid.txt"
    if (Test-Path -LiteralPath $file) {
        $taskProcessId = [int](Get-Content -LiteralPath $file)
        $running = Get-Process -Id $taskProcessId -ErrorAction SilentlyContinue
        if ($running -and $running.ProcessName -eq 'BALDI') { Stop-Process -Id $taskProcessId }
    }
}
& (Join-Path $PSScriptRoot 'build.ps1')
Copy-Item -LiteralPath (Join-Path $modRoot 'artifacts/BepInEx/BaldiTogether.BepInEx.dll') -Destination (Join-Path $gameRoot 'BepInEx/plugins/BaldiTogether/BaldiTogether.BepInEx.dll') -Force
Copy-Item -LiteralPath (Join-Path $modRoot 'artifacts/MelonLoader/BaldiTogether.MelonLoader.dll') -Destination (Join-Path $labRoot 'Mods/BaldiTogether.MelonLoader.dll') -Force
$hostLog = Join-Path $modRoot 'private/host-player.log'
$clientLog = Join-Path $labRoot 'MultiplayerMod/private/client-player.log'
$hostArgs = @('-logFile',('"'+$hostLog+'"'),'-bt-host','-bt-name','Host','-bt-colour','1','-bt-diagnostics','-bt-lab','-bt-auto-start')
$clientArgs = @('-logFile',('"'+$clientLog+'"'),'-bt-join','127.0.0.1','-bt-name','MelonClient','-bt-colour','4','-bt-diagnostics','-bt-lab')
if ($TestActions) { $clientArgs += @('-bt-test-actions','-bt-delay-ready','3'); $hostArgs += '-bt-test-transition' }
if ($SpriteTour) {
    if (!$TestActions) { throw 'SpriteTour requires TestActions.' }
    $hostArgs += '-bt-sprite-tour'; $clientArgs += '-bt-sprite-tour'
}
if ($FocusedSync) {
    if ($TestActions -or $SpriteTour) { throw 'FocusedSync is a separate focused test mode.' }
    $hostArgs += '-bt-focused-sync'; $clientArgs += '-bt-focused-sync'
    $commandPath = Join-Path $modRoot 'private/focused-command.json'
    if (Test-Path -LiteralPath $commandPath) { Remove-Item -LiteralPath $commandPath }
}
if ($FlowSync) {
    if ($TestActions -or $SpriteTour -or $FocusedSync) { throw 'FlowSync is a separate focused test mode.' }
    $hostArgs += '-bt-flow-sync'; $clientArgs += @('-bt-flow-sync','-bt-delay-ready','8')
}
if ($NpcSync) {
    $hostArgs += '-bt-hall-monitor'; $clientArgs += '-bt-hall-monitor' # the NPC test always includes him (he is a random spawn)
    if ($TestActions -or $SpriteTour -or $FocusedSync -or $FlowSync) { throw 'NpcSync is a separate focused test mode.' }
    $hostArgs += '-bt-npc-sync'; $clientArgs += '-bt-npc-sync'
    $commandPath = Join-Path $modRoot 'private/npc-command.json'
    if (Test-Path -LiteralPath $commandPath) { Remove-Item -LiteralPath $commandPath }
}
if ($LobbyTour) {
    # Single BepInEx process: synthetic peers exercise sounds, colours, spectator HUD and LAN parsing.
    $hostArgs = @('-logFile',('"'+$hostLog+'"'),'-bt-name','Host','-bt-diagnostics','-bt-lab','-bt-lobby-tour')
    $hostProcess = Start-Process -FilePath (Join-Path $gameRoot 'BALDI.exe') -ArgumentList $hostArgs -WindowStyle $windowStyle -PassThru
    $hostProcess.Id | Set-Content (Join-Path $modRoot 'private/host-pid.txt')
    Write-Output "Lobby tour PID $($hostProcess.Id)"
    return
}
if ($LanTour) {
    # The client finds the host on FIND LAN GAMES instead of being told its address.
    $clientArgs = @('-logFile',('"'+$clientLog+'"'),'-bt-name','MelonClient','-bt-colour','4','-bt-diagnostics','-bt-lab','-bt-lan-tour')
}
if ($Encounter) {
    # Real host and real client meet each NPC and use items over the network; see src/EncounterLab.cs.
    $hostArgs += '-bt-encounter'; $clientArgs += '-bt-encounter'
    foreach ($file in @((Join-Path $modRoot 'private/encounter-command.json'), (Join-Path $modRoot 'artifacts/tests/encounter-host.json'), (Join-Path $labRoot 'MultiplayerMod/artifacts/tests/encounter-client.json'))) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
}
if ($Soak) {
    # Long two-player session with every NPC active, floor 1 -> Pitstop -> floor 2, profiled on both sides.
    $hostArgs += @('-bt-soak','-bt-profile'); $clientArgs += @('-bt-soak','-bt-profile')
    foreach ($file in @((Join-Path $modRoot 'artifacts/tests/perf-host.json'), (Join-Path $labRoot 'MultiplayerMod/artifacts/tests/perf-client.json'))) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
}
elseif ($Profile) { $hostArgs += '-bt-profile'; $clientArgs += '-bt-profile' }
if ($Chaos) {
    # -Chaos <profile>: the client reaches the host through tests/tcp-chaos.py (seeded delay, jitter, loss stalls,
    # bandwidth; live stall/cut on its control port 7802). Stop it with private/chaos-pid.txt.
    foreach ($port in 7801, 7802) { if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Port $port is in use: the lab won't share it." } }
    $python = Join-Path $modRoot '.venv/Scripts/python.exe'
    $chaosInfo = Join-Path $modRoot 'private/chaos-info.json'
    if (Test-Path -LiteralPath $chaosInfo) { Remove-Item -LiteralPath $chaosInfo }
    $chaosProcess = Start-Process -FilePath $python -ArgumentList @('-I',('"'+(Join-Path $modRoot 'tests/tcp-chaos.py')+'"'),'--listen','7801','--target','127.0.0.1:27015','--profile',$Chaos,'--control','7802','--info',('"'+$chaosInfo+'"')) -RedirectStandardOutput (Join-Path $modRoot 'private/chaos.log') -RedirectStandardError (Join-Path $modRoot 'private/chaos-err.log') -WindowStyle Hidden -PassThru
    $chaosProcess.Id | Set-Content (Join-Path $modRoot 'private/chaos-pid.txt')
    for ($i = 0; $i -lt 50 -and !(Test-Path -LiteralPath $chaosInfo); $i++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $chaosInfo)) { throw 'The chaos proxy did not start; see private/chaos-err.log.' }
    $clientArgs += @('-bt-port','7801')
}
# Always name the port: a chaos run saves Port=7801 in the lab copy's settings, and later runs must not inherit it.
if (($clientArgs -notcontains '-bt-port') -and ($clientArgs -contains '-bt-join')) { $clientArgs += @('-bt-port','27015') }
if (!$LobbyTour) { $hostArgs += @('-bt-port','27015') }
if ($StallHost -gt 0) { $hostArgs += @('-bt-lab-stall-host', "$StallHost") } # the host's game loop blocks once, mid-floor
if ($NoKeepAlive) { $hostArgs += '-bt-lab-no-keepalive'; $clientArgs += '-bt-lab-no-keepalive' } # mutation run: 0.8.x link liveness
if ($Seed) { $hostArgs += @('-bt-seed', $Seed) } # the host's run seed (lab); clients always take the host's
$hostProcess = Start-Process -FilePath (Join-Path $gameRoot 'BALDI.exe') -ArgumentList $hostArgs -WindowStyle $windowStyle -PassThru
$hostProcess.Id | Set-Content (Join-Path $modRoot 'private/host-pid.txt')
$clientProcess = Start-Process -FilePath (Join-Path $labRoot 'BALDI.exe') -WorkingDirectory $labRoot -ArgumentList $clientArgs -WindowStyle $windowStyle -PassThru
$clientProcess.Id | Set-Content (Join-Path $modRoot 'private/client-pid.txt')
Write-Output "Host PID $($hostProcess.Id), Melon client PID $($clientProcess.Id)"
