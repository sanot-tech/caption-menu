$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Probe5 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint="GetWindowTextW", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static string Dump(uint target) {
        var sb = new StringBuilder();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != target) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            RECT r; GetWindowRect(h, out r);
            sb.AppendLine("hwnd=" + h + " vis=" + IsWindowVisible(h) + " cls=" + c + " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B + " title=" + t);
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }
}
'@
Add-Type -TypeDefinition $code
$np = Start-Process notepad -PassThru
Write-Host ("t=0    pid={0} main={1}" -f $np.Id, $np.MainWindowHandle)
foreach ($ms in 300, 700, 1000, 1000, 2000) {
    Start-Sleep -Milliseconds $ms
    $p = Get-Process -Id $np.Id -ErrorAction SilentlyContinue
    Write-Host ("t+{0} main={1}" -f $ms, $p.MainWindowHandle)
    Write-Host ([Probe5]::Dump([uint32]$np.Id))
}
