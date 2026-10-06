$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Probe4 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", EntryPoint="GetWindowTextW", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    public static string Dump(uint target) {
        var sb = new StringBuilder();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != target) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            sb.AppendLine("hwnd=" + h + " vis=" + IsWindowVisible(h) + " len=" + GetWindowTextLength(h) + " class=" + c + " title=" + t);
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }
}
'@
Add-Type -TypeDefinition $code
$np = Get-Process notepad -ErrorAction SilentlyContinue | Select-Object -First 1
Write-Host ("pid={0} mainhwnd={1}" -f $np.Id, $np.MainWindowHandle)
Write-Host ([Probe4]::Dump([uint32]$np.Id))
