$np = Get-Process notepad -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $np) { Write-Host 'no notepad'; exit 1 }
Write-Host ("notepad pid={0} hwnd={1}" -f $np.Id, $np.MainWindowHandle)

$code = @'
using System;
using System.Runtime.InteropServices;
public static class Probe3 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    public static string Scan(uint target) {
        int total=0, mine=0, vis=0, titled=0; IntPtr hit=IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            total++;
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == target) {
                mine++;
                if (IsWindowVisible(h)) { vis++; if (GetWindowTextLength(h) > 0) { titled++; hit = h; } }
            }
            return true;
        }, IntPtr.Zero);
        return "total=" + total + " mine=" + mine + " visible=" + vis + " titled=" + titled + " hit=" + hit;
    }
}
'@
Add-Type -TypeDefinition $code
Write-Host ([Probe3]::Scan([uint32]$np.Id))
