param([switch]$Windowed, [switch]$Lobby, [int]$TimeoutSeconds = 150)
# Alt+Tab regression test: a lab host starts a real run (-bt-focus-tour); this script then sends real Alt+Tab
# keystrokes away and back, and minimizes/restores the window. The game logs every scene load, focus change,
# pause, menu return and transition to artifacts/tests/focus-events.txt and saves before/after screenshots.
# The window is visible (focus cannot be tested on a hidden window). Keystrokes are only sent while the game
# window is the foreground window. Only the test-owned process is stopped.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'paths.ps1')
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class FocusWin {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static void AltTab() { keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0x09,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x09,0,2,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); }
}
'@
foreach ($file in @('private/focus-ready.txt','private/focus-done.txt','artifacts/tests/focus-results.json','artifacts/tests/focus-events.txt')) {
    $path = Join-Path $modRoot $file; if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
}
$arguments = @('-logFile', ('"' + (Join-Path $modRoot 'private/focus-player.log') + '"'), '-bt-diagnostics', '-bt-lab', '-bt-focus-tour') + $workspaceArgs
if ($Windowed) { $arguments += @('-screen-fullscreen', '0') }
if ($Lobby) { $arguments += '-bt-focus-lobby' }
$process = Start-Process -FilePath (Join-Path $gameRoot 'BALDI.exe') -ArgumentList $arguments -PassThru
$process.Id | Set-Content (Join-Path $modRoot 'private/focus-pid.txt')
$log = @()
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (!(Test-Path -LiteralPath (Join-Path $modRoot 'private/focus-ready.txt')) -and (Get-Date) -lt $deadline -and !$process.HasExited) { Start-Sleep -Milliseconds 500 }
    if (!(Test-Path -LiteralPath (Join-Path $modRoot 'private/focus-ready.txt'))) { throw 'The run never became ready.' }
    $process.Refresh(); $window = $process.MainWindowHandle
    [FocusWin]::SetForegroundWindow($window) | Out-Null; Start-Sleep -Milliseconds 800
    if ([FocusWin]::GetForegroundWindow() -eq $window) {
        [FocusWin]::AltTab(); $log += 'alt-tab away'; Start-Sleep -Seconds 3
        [FocusWin]::SetForegroundWindow($window) | Out-Null; $log += 'focus back'; Start-Sleep -Seconds 3
    } else { $log += 'game was not foreground; Alt+Tab skipped' }
    [FocusWin]::ShowWindow($window, 6) | Out-Null; $log += 'minimize'; Start-Sleep -Seconds 3
    [FocusWin]::ShowWindow($window, 9) | Out-Null; [FocusWin]::SetForegroundWindow($window) | Out-Null; $log += 'restore'; Start-Sleep -Seconds 3
    Set-Content -LiteralPath (Join-Path $modRoot 'private/focus-done.txt') -Value ($log -join "`n")
    $deadline = (Get-Date).AddSeconds(30)
    while (!(Test-Path -LiteralPath (Join-Path $modRoot 'artifacts/tests/focus-results.json')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}
finally {
    $running = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($running -and $running.ProcessName -eq 'BALDI') { Stop-Process -Id $process.Id }
}
$log
