$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Probe6 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

    public static string Dump(uint target) {
        var sb = new StringBuilder();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != target) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            sb.AppendLine("  hwnd=" + h + " isWin=" + IsWindow(h) + " vis=" + IsWindowVisible(h) + " len=" + GetWindowTextLength(h) + " cls=" + c);
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }

    public static void Place(IntPtr h) {
        ShowWindow(h, 5); System.Threading.Thread.Sleep(200);
        ShowWindow(h, 9); System.Threading.Thread.Sleep(200);
        ShowWindow(h, 1); System.Threading.Thread.Sleep(200);
        SetWindowPos(h, new IntPtr(-1), 100, 100, 800, 500, 0x0040);
    }
}
'@
Add-Type -TypeDefinition $code
$np = Start-Process notepad -PassThru
Start-Sleep -Milliseconds 2000
$h = $np.MainWindowHandle
Write-Host ("before place: hwnd={0}" -f $h)
Write-Host ([Probe6]::Dump([uint32]$np.Id))
[Probe6]::Place($h)
Write-Host 'after place:'
Write-Host ([Probe6]::Dump([uint32]$np.Id))
Start-Sleep -Seconds 3
Write-Host 'after 3s:'
Write-Host ([Probe6]::Dump([uint32]$np.Id))
Write-Host ("process alive: " -f (-not (Get-Process -Id $np.Id -ErrorAction SilentlyContinue).HasExited))
