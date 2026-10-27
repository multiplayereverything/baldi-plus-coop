param([Parameter(Mandatory=$true)][string]$Tour, [Parameter(Mandatory=$true)][string]$Result, [int]$TimeoutSeconds = 240, [int]$Retries = 1, [string[]]$ExtraArgs = @(), [switch]$Steam, [switch]$Hidden)
# Run one single-process lab tour on the BepInEx game copy, wait for its results file, stop only that process.
#   ./scripts/run-tour.ps1 -Tour spectator-tour -Result spectator-results.json
# A tour that writes no results (for example a stall on the warning screen) is relaunched up to -Retries times;
# a tour that writes results with failures is never retried.
$ErrorActionPreference = 'Stop'
# -Steam: start under the game's Steam app (1275890) the way Steam itself does, through SteamAppId; no steam_appid.txt is written.
if ($Steam) { $env:SteamAppId = '1275890'; $env:SteamGameId = '1275890' }
. (Join-Path $PSScriptRoot 'paths.ps1')
. (Join-Path $PSScriptRoot 'lab-window.ps1') # minimized small window, restores at the right edge; -Hidden hides it
$resultPath = Join-Path $modRoot "artifacts/tests/$Result"
$log = Join-Path $modRoot "private/$Tour-player.log"
for ($attempt = 0; $attempt -le $Retries; $attempt++) {
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
    $process = Start-LabGame -FilePath (Join-Path $gameRoot 'BALDI.exe') -ArgumentList (@('-logFile',('"'+$log+'"'),'-bt-diagnostics','-bt-lab',"-bt-$Tour") + $workspaceArgs + $ExtraArgs) -Slot 0 -Title "Baldi lab - $Tour" -Hidden:$Hidden
    $process.Id | Set-Content (Join-Path $modRoot "private/$Tour-pid.txt")
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    try {
        while (!(Test-Path -LiteralPath $resultPath) -and (Get-Date) -lt $deadline -and !$process.HasExited) { Start-Sleep -Milliseconds 500 }
        Start-Sleep -Milliseconds 500
    }
    finally {
        $running = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($running -and $running.ProcessName -eq 'BALDI') { Stop-Process -Id $process.Id; Start-Sleep -Seconds 2 }
    }
    if (Test-Path -LiteralPath $resultPath) { break }
    & (Join-Path $PSScriptRoot 'save-failure.ps1') -Name "$Tour-attempt$($attempt + 1)" -Tour $Tour | Write-Output
    Write-Output "$Tour (PID $($process.Id)): no $Result within $TimeoutSeconds s (exited: $($process.HasExited)); attempt $($attempt + 1) of $($Retries + 1)."
}
if (!(Test-Path -LiteralPath $resultPath)) { throw "No $Result after $($Retries + 1) attempts." }
$report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
"$Tour (PID $($process.Id)): $($report.passed) passed, $($report.failed) failed"
$report.checks.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object { "  FAIL $($_.Name)" }
if ($report.failed -gt 0) { exit 1 }
exit 0
