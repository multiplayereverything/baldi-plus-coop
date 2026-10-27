# Dot-sourced by the lab scripts. Test copies of the game must never get in the user's way:
# - hidden by default (the user's choice, 2026-10-27): each copy starts on its own Windows desktop ("BaldiLab", in the
#   user's window station but never switched to), so no window, flash or taskbar button ever reaches the user's desktop.
#   Hiding a window after it appears does not work: Unity shows it ~1.6 s after start and does not process the hide
#   until its loading stalls end about 3 s later. The soak is the exception (see Start-LabGame);
# - small and windowed (480x270), with the mod's -bt-lab-light (lowest quality, 60 FPS cap); the soak (-bt-lab-uncapped)
#   runs with the game's own settings so frame times compare;
# - -Minimized keeps the older behaviour: on the user's desktop, minimized without activation in the taskbar
#   (SW_SHOWMINNOACTIVE), titled ("Baldi lab - host"), with the restore spot parked at the right edge, stacked by slot;
# - BelowNormal priority.
# Nothing here sends keys or mouse input or takes focus. A window is placed once and never touched again.
if (-not ('LabWindow' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class LabWindow {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct STARTUPINFO { public int cb; public string r, d, t; public int x, y, w, h, cx, cy, fill, flags; public short show, r2; public IntPtr r3, i, o, e; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int action, int param, out RECT area, int ini);
  // BELOW_NORMAL_PRIORITY_CLASS; STARTF_USESHOWWINDOW with SW_SHOWMINNOACTIVE (7) or SW_HIDE (0).
  public static int Start(string exe, string args, string dir, bool hidden) {
    var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.flags = 1; si.show = (short)(hidden ? 0 : 7);
    PROCESS_INFORMATION pi;
    if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\" " + args), IntPtr.Zero, IntPtr.Zero, false, 0x4000, IntPtr.Zero, dir, ref si, out pi)) throw new System.ComponentModel.Win32Exception();
    CloseHandle(pi.thread); CloseHandle(pi.process); return pi.pid;
  }
  public static RECT WorkArea() { RECT r; SystemParametersInfo(0x30, 0, out r, 0); return r; }
}
'@
}
if (-not ('LabDesktop2' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class LabDesktop2 {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct STARTUPINFO { public int cb; public string r, d, t; public int x, y, w, h, cx, cy, fill, flags; public short show, r2; public IntPtr r3, i, o, e; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr sa);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
  [DllImport("user32.dll")] static extern int WaitForInputIdle(IntPtr process, int ms);
  // Starts the process on the "BaldiLab" desktop of this window station (created if needed, never switched to), at
  // BELOW_NORMAL_PRIORITY_CLASS. The desktop lives while any process uses it, so this handle stays open until the game
  // has attached (its first input idle), or an empty desktop would vanish under it.
  public static int Start(string exe, string args, string dir) {
    IntPtr desk = CreateDesktop("BaldiLab", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero); // GENERIC_ALL
    if (desk == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    try {
      var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.d = "WinSta0\\BaldiLab";
      PROCESS_INFORMATION pi;
      if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\" " + args), IntPtr.Zero, IntPtr.Zero, false, 0x4000, IntPtr.Zero, dir, ref si, out pi)) throw new System.ComponentModel.Win32Exception();
      WaitForInputIdle(pi.process, 30000);
      CloseHandle(pi.thread); CloseHandle(pi.process); return pi.pid;
    } finally { CloseDesktop(desk); }
  }
}
'@
}
$labWindowWidth = 480; $labWindowHeight = 270
$labWindowArgs = @('-screen-fullscreen', '0', '-screen-width', "$labWindowWidth", '-screen-height', "$labWindowHeight", '-bt-lab-light')

# Starts BALDI.exe for a lab test and returns its Process: on the hidden lab desktop, or with -Minimized as a parked
# minimized window on the user's desktop (slot 0 is the top of the right edge).
function Start-LabGame([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory, [int]$Slot = 0, [string]$Title = 'Baldi lab', [switch]$Hidden, [switch]$Minimized) {
    if (!$WorkingDirectory) { $WorkingDirectory = Split-Path $FilePath -Parent }
    # The soak (-bt-lab-uncapped) measures frame time with the game's own window settings, started hidden (SW_HIDE) on
    # the user's desktop. Both a small windowed copy and any copy on the lab desktop are paced at 60 Hz, so their frame
    # p95 sits at exactly 16.7 ms. Unity still shows a soak copy for 2 to 3 s at startup; it is the one exception.
    if (@($ArgumentList) -contains '-bt-lab-uncapped') { return Get-Process -Id ([LabWindow]::Start($FilePath, (@($ArgumentList) -join ' '), $WorkingDirectory, $true)) }
    $commandLine = (@($ArgumentList) + $labWindowArgs) -join ' '
    if (!$Minimized) { return Get-Process -Id ([LabDesktop2]::Start($FilePath, $commandLine, $WorkingDirectory)) }
    $id = [LabWindow]::Start($FilePath, $commandLine, $WorkingDirectory, $false)
    # Place it once from a detached hidden PowerShell (launch-lab exits right after starting the copies): minimized
    # without activation, titled, with its restore spot parked at the right edge, stacked by slot. Never touched again.
    $area = [LabWindow]::WorkArea()
    $x = $area.Right - $labWindowWidth - 32; $y = $area.Top + $Slot * ($labWindowHeight + 48)
    $park = @"
Add-Type -Namespace Park -Name W -MemberDefinition '
[StructLayout(LayoutKind.Sequential)] public struct PLACEMENT { public int length, flags, showCmd, minX, minY, maxX, maxY, left, top, right, bottom; }
[DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref PLACEMENT p);
[DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref PLACEMENT p);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowText(IntPtr h, string t);'
for (`$i = 0; `$i -lt 120; `$i++) {
    `$p = Get-Process -Id $id -ErrorAction SilentlyContinue; if (!`$p) { return }
    if (`$p.MainWindowHandle -ne [IntPtr]::Zero) {
        Start-Sleep -Seconds 2 # Unity sizes its window once after creating it
        `$h = (Get-Process -Id $id).MainWindowHandle
        `$w = New-Object Park.W+PLACEMENT; `$w.length = 44
        [Park.W]::GetWindowPlacement(`$h, [ref]`$w) | Out-Null
        `$width = `$w.right - `$w.left; `$height = `$w.bottom - `$w.top
        `$w.left = $x; `$w.top = $y; `$w.right = $x + `$width; `$w.bottom = $y + `$height
        `$w.showCmd = 7 # SW_SHOWMINNOACTIVE
        [Park.W]::SetWindowPlacement(`$h, [ref]`$w) | Out-Null
        [Park.W]::SetWindowText(`$h, '$Title') | Out-Null
        return
    }
    Start-Sleep -Milliseconds 500
}
"@
    Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($park))) | Out-Null
    Get-Process -Id $id
}
