# Focused regression suite for the lobby, sound, spectator and LAN features.
#   ./scripts/lab-suite.ps1
# Builds both loaders, runs the transport checks, every single-process tour on the BepInEx copy,
# then the two-process LAN join test (BepInEx host + MelonLoader client). Only test-owned processes are stopped.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')
$python = Join-Path $modRoot '.venv/Scripts/python.exe'
if (Get-Process BALDI -ErrorAction SilentlyContinue) { throw 'Close Baldi before running the lab suite.' }
$failed = @()

& (Join-Path $PSScriptRoot 'build.ps1')
& (Join-Path $PSScriptRoot 'test.ps1') | Select-Object -Last 1
Copy-Item (Join-Path $modRoot 'artifacts/BepInEx/BaldiTogether.BepInEx.dll') (Join-Path $gameRoot 'BepInEx/plugins/BaldiTogether/BaldiTogether.BepInEx.dll') -Force

$tours = @(@('lobby-tour','lobby-results.json'), @('spectator-tour','spectator-results.json'), @('hideseek-audio-tour','hideseek-audio-results.json'),
    @('spectator-ui-tour','spectator-ui-results.json'), @('beans-tour','beans-results.json'), @('crafters-tour','crafters-results.json'),
    @('pomp-tour','pomp-results.json'), @('thetest-tour','thetest-results.json'), @('reflex-tour','reflex-results.json'),
    @('chalkles-tour','chalkles-results.json'), @('items-tour','items-results.json'), @('hall-monitor-tour','hall-monitor-results.json'), @('compat-fail-tour','compat-fail-results.json'),
    @('steam-tour','steam-results.json','steam'))
foreach ($tour in $tours) {
    try {
        # steam-tour needs Steam running and signed in; it starts under the game's Steam app (run-tour -Steam)
        $steamTour = $tour.Count -gt 2
        & (Join-Path $PSScriptRoot 'run-tour.ps1') -Tour $tour[0] -Result $tour[1] -Steam:$steamTour
        if ($LASTEXITCODE -ne 0) { $failed += $tour[0] }
    }
    catch { Write-Output "$($tour[0]): $($_.Exception.Message)"; $failed += $tour[0] }
}

foreach ($result in @((Join-Path $modRoot 'private/melon-lab/MultiplayerMod/artifacts/tests/lan-tour-results.json'), (Join-Path $modRoot 'artifacts/tests/lan-tour-results.json'))) {
    if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
}
# Two-process tests occasionally stall at game startup; each gets one retry, and a retry is always printed.
function Stop-LabPair {
    foreach ($role in 'host','client') {
        $id = [int](Get-Content (Join-Path $modRoot "private/$role-pid.txt"))
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'BALDI') { Stop-Process -Id $id }
    }
    Start-Sleep -Seconds 3
}
$lanPassed = $false
for ($attempt = 1; $attempt -le 2 -and !$lanPassed; $attempt++) {
    if ($attempt -gt 1) { Write-Output "lan-tour: retrying (attempt $attempt of 2)" }
    foreach ($result in @((Join-Path $modRoot 'private/melon-lab/MultiplayerMod/artifacts/tests/lan-tour-results.json'), (Join-Path $modRoot 'artifacts/tests/lan-tour-results.json'))) {
        if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
    }
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') -LanTour | Select-Object -Last 1
    try { & $python -I (Join-Path $modRoot 'tests/verify-lobby.py') lan | Where-Object { $_ -match '^FAIL|passed$' }; $lanPassed = $LASTEXITCODE -eq 0
        if (!$lanPassed) { & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name "lan-tour-attempt$attempt" } }
    finally { Stop-LabPair }
}
if (!$lanPassed) { $failed += 'lan-tour' }

# Remaining two-process tests: NPC sync, core co-op, the 0.7 encounter scenario, and the profiled two-floor soak.
foreach ($mode in @(@('NpcSync','verify-npcs.py','npc-sync'), @('TestActions','verify-live.py','core-live'), @('Encounter','verify-encounter.py','encounter'), @('Soak','verify-perf.py','soak-perf'))) {
    Start-Sleep -Seconds 3
    # Splat the switch: a string such as "-NpcSync" would bind as a positional value, not the switch.
    $launchMode = @{ $mode[0] = $true }
    foreach ($stale in @('artifacts/tests/encounter-host.json','artifacts/tests/encounter-verify.json','artifacts/tests/npc-results.json','artifacts/tests/live-results.json','private/melon-lab/MultiplayerMod/artifacts/tests/encounter-client.json')) {
        $path = Join-Path $modRoot $stale; if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    $modePassed = $false
    for ($attempt = 1; $attempt -le 2 -and !$modePassed; $attempt++) {
        if ($attempt -gt 1) { Write-Output "$($mode[2]): retrying (attempt $attempt of 2)" }
        & (Join-Path $PSScriptRoot 'launch-lab.ps1') @launchMode | Select-Object -Last 1
        try {
            if ($mode[0] -eq 'Encounter' -or $mode[0] -eq 'Soak') { & $python -I (Join-Path $modRoot "tests/$($mode[1])") 700 | Select-Object -Last 1 }
            else { & $python -I (Join-Path $modRoot "tests/$($mode[1])") | Select-Object -Last 1 }
            $modePassed = $LASTEXITCODE -eq 0
            if (!$modePassed) { & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name "$($mode[2])-attempt$attempt" }
        }
        finally { Stop-LabPair }
    }
    if (!$modePassed) { $failed += $mode[2] }
}

# Link health over the TCP chaos proxy (bad_wifi, 25 s host stall, 10 s outage, cut), then its keepalive-off mutant,
# which must fail; both relaunch once if the pair never connects.
Start-Sleep -Seconds 3
& (Join-Path $PSScriptRoot 'link-test.ps1') -NetProfile bad_wifi | Where-Object { $_ -match '^FAIL|passed$|retrying' }
if ($LASTEXITCODE -ne 0) { $failed += 'link-health' }
Start-Sleep -Seconds 3
& (Join-Path $PSScriptRoot 'link-test.ps1') -NetProfile bad_wifi -Mutant | Where-Object { $_ -match 'mutant|retrying' }
if ($LASTEXITCODE -ne 0) { $failed += 'link-health-mutant' }

# Johnny's shop over two processes: plain, acks held past the host's deadline, and the two mutants that must fail.
foreach ($shop in @(@{}, @{ LateAck = $true }, @{ Mutant = 'late-ack' }, @{ Mutant = 'glue' })) {
    Start-Sleep -Seconds 3
    $label = if ($shop.LateAck) { 'shop-late-ack' } elseif ($shop.Mutant) { "shop-mutant-$($shop.Mutant)" } else { 'shop' }
    & (Join-Path $PSScriptRoot 'shop-test.ps1') @shop | Select-Object -Last 1 | ForEach-Object { "${label}: $_" }
    if ($LASTEXITCODE -ne 0) { $failed += $label }
}

# Random events: the host runs a fog event and the client mirrors it; the mutant sends no event states and must fail.
foreach ($run in @(@{ Label = 'events'; Launch = @{ EventSync = $true } }, @{ Label = 'events-mutant'; Launch = @{ EventSync = $true; NoEventSync = $true } })) {
    Start-Sleep -Seconds 3
    $launch = $run.Launch
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') @launch | Select-Object -Last 1
    try {
        & $python -I (Join-Path $modRoot 'tests/verify-events.py') 150 | Select-Object -Last 1 | ForEach-Object { "$($run.Label): $_" }
        $ok = $LASTEXITCODE -eq 0
        if ($run.Label -eq 'events-mutant') { if ($ok) { $failed += 'events-mutant-not-caught' } else { 'events-mutant: caught' } }
        elseif (!$ok) { $failed += 'events'; & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name 'events' }
    }
    finally { Stop-LabPair }
}

# Challenges and Endless (src/ModeSync.cs): a challenge win for everyone, then Endless notebook resets, item respawns and
# results; each mutant must fail.
foreach ($run in @(@{ Label = 'challenge'; Mode = 'challenge'; Launch = @{ Scene = 'SpeedyChallenge'; ModeSync = $true } },
                   @{ Label = 'challenge-mutant'; Mode = 'challenge'; Launch = @{ Scene = 'SpeedyChallenge'; ModeSync = $true; NoModeSync = $true } },
                   @{ Label = 'endless'; Mode = 'endless'; Launch = @{ Scene = 'Endless_Schoolhouse_Medium'; ModeSync = $true } },
                   @{ Label = 'endless-mutant'; Mode = 'endless'; Launch = @{ Scene = 'Endless_Schoolhouse_Medium'; ModeSync = $true; ClientResets = $true } })) {
    Start-Sleep -Seconds 3
    $launch = $run.Launch
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') @launch | Select-Object -Last 1
    try {
        & $python -I (Join-Path $modRoot 'tests/verify-modes.py') $run.Mode 200 | Select-Object -Last 1 | ForEach-Object { "$($run.Label): $_" }
        $ok = $LASTEXITCODE -eq 0
        if ($run.Label -like '*-mutant') { if ($ok) { $failed += "$($run.Label)-not-caught" } else { "$($run.Label): caught" } }
        elseif (!$ok) { $failed += $run.Label; & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name $run.Label }
    }
    finally { Stop-LabPair }
}

if ($failed.Count) { Write-Output "FAILED: $($failed -join ', ')"; exit 1 }
Write-Output 'Lab suite passed.'
