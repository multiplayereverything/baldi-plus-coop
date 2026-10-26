param([switch]$LateAck, [ValidateSet('','late-ack','glue')][string]$Mutant = '')
# Focused shop test (tests/verify-sync.py): real Johnny shelf purchase, insufficient funds, refund, duplicate request.
# -LateAck: the client holds each shop ack 15 s, past the host's 12 s deadline; the refund and the sale must still settle.
# -Mutant late-ack: as -LateAck, with the host ignoring late acks; -Mutant glue: one host-priced glue stick for everyone.
# Both are the 0.9.0 behaviour, and pass only if their check fails.
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$python = Join-Path $modRoot '.venv/Scripts/python.exe'
$launch = @{ FocusedSync = $true }
if ($LateAck -or $Mutant -eq 'late-ack') { $launch.LateShopAck = $true }
if ($Mutant -eq 'late-ack') { $launch.NoLateAck = $true }
if ($Mutant -eq 'glue') { $launch.NoOwnGlue = $true }
& (Join-Path $PSScriptRoot 'launch-lab.ps1') @launch | Select-Object -Last 1
try {
    $verifyArgs = @('-I', (Join-Path $modRoot 'tests/verify-sync.py')); if ($LateAck -or $Mutant -eq 'late-ack') { $verifyArgs += '--late-ack' }
    $output = & $python @verifyArgs
    $code = $LASTEXITCODE
    $output | Select-Object -Last 1
    if ($code -ne 0 -and !$Mutant) { & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name 'shop' }
}
finally {
    foreach ($role in 'host','client') {
        $id = [int](Get-Content -LiteralPath (Join-Path $modRoot "private/$role-pid.txt"))
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'BALDI') { Stop-Process -Id $id }
    }
    Start-Sleep -Seconds 3
}
if ($Mutant) {
    $result = Get-Content -LiteralPath (Join-Path $modRoot 'artifacts/tests/sync-results.json') -Raw | ConvertFrom-Json
    $check = if ($Mutant -eq 'glue') { 'glue_stick_own_price' } else { 'shop_late_acks_settled' }
    if ($result.checks.$check) { "mutant $Mutant NOT caught"; exit 1 } else { "mutant $Mutant caught"; exit 0 }
}
exit $code
