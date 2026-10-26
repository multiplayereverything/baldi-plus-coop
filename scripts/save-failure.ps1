param([Parameter(Mandatory=$true)][string]$Name, [string]$Tour)
# Copies the reports and game logs of a failed lab attempt to private/failures/<Name>-<time>/ before a retry or a
# later test overwrites them. Missing files are skipped. Prints the folder.
$modRoot = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $modRoot ("private/failures/$Name-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$files = if ($Tour) { @("private/$Tour-player.log","private/$Tour-pid.txt") } else {
    @('private/host-player.log','private/melon-lab/MultiplayerMod/private/client-player.log','private/melon-lab/MelonLoader/Latest.log','private/melon-lab/MultiplayerMod/artifacts/tests/sync-results.json','artifacts/tests/sync-results.json','private/host-pid.txt','private/client-pid.txt',
    'artifacts/tests/encounter-host.json','artifacts/tests/encounter-verify.json','private/melon-lab/MultiplayerMod/artifacts/tests/encounter-client.json',
    'artifacts/tests/npc-results.json','artifacts/tests/live-results.json','artifacts/tests/perf-verify.json','artifacts/tests/link-results.json') }
foreach ($relative in $files) {
    $path = Join-Path $modRoot $relative
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $folder (($relative -replace '[\/]','_'))) -Force }
}
foreach ($role in $(if ($Tour) { @($Tour) } else { @('host','client') })) {
    $pidFile = Join-Path $modRoot "private/$role-pid.txt"
    if (!(Test-Path -LiteralPath $pidFile)) { continue }
    $id = (Get-Content -LiteralPath $pidFile).Trim()
    foreach ($runtime in @("private/runtime-$id.json","private/melon-lab/MultiplayerMod/private/runtime-$id.json")) {
        $path = Join-Path $modRoot $runtime
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $folder "runtime-$role.json") -Force }
    }
    $capture = Join-Path $modRoot "private/captures/screen-$id.png"
    if (Test-Path -LiteralPath $capture) { Copy-Item -LiteralPath $capture -Destination (Join-Path $folder "screen-$role.png") -Force }
}
"saved failure evidence: $folder"
