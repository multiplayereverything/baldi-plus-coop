# Dot-sourced by the lab scripts. Test copies of the game must never get in the user's way:
# - small and windowed (480x270), with the mod's -bt-lab-light (lowest quality, 60 FPS cap; launch-lab -Soak adds -bt-lab-uncapped);
# - started minimized without activation and kept minimized in the taskbar (SetWindowPlacement, SW_SHOWMINNOACTIVE),
#   titled ("Baldi lab - host"), with the restore spot parked at the right edge of the primary monitor, stacked by
#   slot, so clicking one brings it up small at the side; never topmost, never activated, never re-minimized;
# - BelowNormal priority.
# Nothing here sends keys or mouse input or takes focus. -Hidden still starts a copy fully hidden.
if (-not ('LabWindow' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class LabWindow {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct STARTUPINFO { public int cb; public string r, d, t; public int x, y, w, h, cx, cy, fill, flags; public short show, r2; public IntPtr r3, i, o, e; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
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
$labWindowWidth = 480; $labWindowHeight = 270
$labWindowArgs = @('-screen-fullscreen', '0', '-screen-width', "$labWindowWidth", '-screen-height', "$labWindowHeight", '-bt-lab-light')

# Starts BALDI.exe as a parked lab window and returns its Process. Slot 0 is the top of the right edge.
function Start-LabGame([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory, [int]$Slot = 0, [string]$Title = 'Baldi lab', [switch]$Hidden) {
    if (!$WorkingDirectory) { $WorkingDirectory = Split-Path $FilePath -Parent }
    $id = [LabWindow]::Start($FilePath, ((@($ArgumentList) + $labWindowArgs) -join ' '), $WorkingDirectory, [bool]$Hidden)
    $process = Get-Process -Id $id
    if (!$Hidden) {
        # Place it once from a detached hidden PowerShell (launch-lab exits right after starting the copies):
        # minimized without activation, titled, and with its restore spot parked at the right edge, stacked by slot.
        # It is never touched again, so a window the user restores stays where they put it.
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
    }
    $process
}
