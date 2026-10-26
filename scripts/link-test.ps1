param([string]$NetProfile = 'bad_wifi', [switch]$Mutant)
# Link-health run (tests/verify-link.py): host and client through the TCP chaos proxy, a 25 s host game-loop
# stall, a 10 s outage, then a cut link. -Mutant runs it with keepalives off and passes only if the check fails.
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$python = Join-Path $modRoot '.venv/Scripts/python.exe'
$launch = @{ Chaos = $NetProfile; StallHost = 25 }
if ($Mutant) { $launch.NoKeepAlive = $true }
for ($attempt = 1; $attempt -le 2; $attempt++) {
    if ($attempt -gt 1) { Write-Output "link-test: the pair never connected; retrying (attempt $attempt of 2)" }
    & (Join-Path $PSScriptRoot 'launch-lab.ps1') @launch | Select-Object -Last 1
    try {
        $verifyArgs = @('-I', (Join-Path $modRoot 'tests/verify-link.py')); if ($Mutant) { $verifyArgs += '--expect-mutant' }
        & $python @verifyArgs
        $code = $LASTEXITCODE
        if ($code -ne 0 -and !$Mutant) { & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name "link-attempt$attempt" }
    }
    finally {
        foreach ($role in 'host','client','chaos') {
            $file = Join-Path $modRoot "private/$role-pid.txt"
            if (!(Test-Path -LiteralPath $file)) { continue }
            $id = [int](Get-Content -LiteralPath $file)
            $process = Get-Process -Id $id -ErrorAction SilentlyContinue
            if ($process -and ($process.ProcessName -eq 'BALDI' -or $process.ProcessName -eq 'python')) { Stop-Process -Id $id }
        }
        Start-Sleep -Seconds 3
    }
    if ($code -ne 3) { break }
}
exit $code
