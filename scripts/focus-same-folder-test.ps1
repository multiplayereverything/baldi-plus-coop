param([int]$AwaySeconds = 25)
# The user's setup: host and client are two BALDI.exe instances from the SAME game folder (same BepInEx, mod config
# and logs). The host starts a run; then focus moves to the client window (Alt+Tab) for -AwaySeconds and the host is
# also minimized, then restored. Prints both sides' link messages. Only test-owned processes are stopped.
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path $PSScriptRoot -Parent
$gameRoot = Split-Path $modRoot -Parent
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SameWin {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
}
'@
$hostLog = Join-Path $modRoot 'private/same-host.log'; $clientLog = Join-Path $modRoot 'private/same-client.log'
$common = @('-bt-diagnostics', '-bt-lab', '-bt-soak') # soak keeps the run alive (catches suppressed)
$hostProcess = Start-Process -FilePath (Join-Path $gameRoot 'BALDI.exe') -ArgumentList (@('-logFile', ('"' + $hostLog + '"'), '-bt-host', '-bt-auto-start', '-bt-name', 'Tu') + $common) -PassThru
Start-Sleep -Seconds 3 # two instances from one folder cannot start in the same instant
$clientProcess = Start-Process -FilePath (Join-Path $gameRoot 'BALDI.exe') -ArgumentList (@('-logFile', ('"' + $clientLog + '"'), '-bt-join', '127.0.0.1', '-bt-name', 'Ford') + $common) -PassThru
"Host PID $($hostProcess.Id), client PID $($clientProcess.Id)"
try {
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline -and !((Get-Content $clientLog -Raw -ErrorAction SilentlyContinue) -match 'Level matched')) { Start-Sleep 1 }
    if (!((Get-Content $clientLog -Raw) -match 'Level matched')) { throw 'The client never matched the host level.' }
    Start-Sleep -Seconds 6
    $hostProcess.Refresh(); $clientProcess.Refresh()
    $hostWindow = $hostProcess.MainWindowHandle; $clientWindow = $clientProcess.MainWindowHandle
    [SameWin]::SwitchToThisWindow($hostWindow, $true); Start-Sleep -Milliseconds 800
    "host foreground: $([SameWin]::GetForegroundWindow() -eq $hostWindow)"
    # Alt+Tab to the client: its full-screen window now covers the host, which keeps running behind it.
    [SameWin]::SwitchToThisWindow($clientWindow, $true); Start-Sleep -Milliseconds 800
    "client foreground: $([SameWin]::GetForegroundWindow() -eq $clientWindow)"
    $hostDiag = Join-Path $modRoot "private/runtime-$($hostProcess.Id).json"
    $stamps = @()
    for ($i = 0; $i -lt $AwaySeconds; $i++) { Start-Sleep -Seconds 1; $stamps += (Get-Item $hostDiag -ErrorAction SilentlyContinue).LastWriteTime.ToString('HH:mm:ss') }
    "host diagnostics written at: $($stamps -join ' ')"
    [SameWin]::SwitchToThisWindow($hostWindow, $true)
    "host restored"
    Start-Sleep -Seconds 12
}
finally {
    foreach ($p in @($hostProcess, $clientProcess)) { $r = Get-Process -Id $p.Id -ErrorAction SilentlyContinue; if ($r -and $r.ProcessName -eq 'BALDI') { Stop-Process -Id $p.Id } }
}
$pattern = 'Baldi Together\].*(left|migrat|Disconnect|Run ended|timed out|Host |lost|closed|Taking over|Reconnect|joined|matched|failed|Loading)'
"--- host (Tu)"; Select-String -Path $hostLog -Pattern $pattern | Select-Object -Last 15 | ForEach-Object { $_.Line }
"--- client (Ford)"; Select-String -Path $clientLog -Pattern $pattern | Select-Object -Last 15 | ForEach-Object { $_.Line }
