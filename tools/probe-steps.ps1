$code = @'
using System;
using System.Runtime.InteropServices;
public static class Probe7 {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@
Add-Type -TypeDefinition $code

function Alive($id) { if (Get-Process -Id $id -ErrorAction SilentlyContinue) { 'ALIVE' } else { 'GONE' } }

Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$np = Start-Process notepad -PassThru
Start-Sleep -Milliseconds 2000
$h = $np.MainWindowHandle
Write-Host ("t0   hwnd=$h  " + (Alive $np.Id))

[void][Probe7]::ShowWindow($h, 5)      # SW_SHOW
Write-Host ("after SW_SHOW(5)    " + (Alive $np.Id))

[void][Probe7]::ShowWindow($h, 9)      # SW_RESTORE
Write-Host ("after SW_RESTORE(9) " + (Alive $np.Id))

Start-Sleep -Milliseconds 300
Write-Host ("after 300ms         " + (Alive $np.Id))

[void][Probe7]::ShowWindow($h, 1)      # SW_SHOWNORMAL
Write-Host ("after SW_SHOWNORMAL(1) " + (Alive $np.Id))

[void][Probe7]::SetWindowPos($h, [IntPtr](-1), 100, 100, 800, 500, 0x0040)
Write-Host ("after SetWindowPos TOPMOST " + (Alive $np.Id))

Start-Sleep -Seconds 3
Write-Host ("after 3s            " + (Alive $np.Id))
$p = Get-Process -Id $np.Id -ErrorAction SilentlyContinue
if ($p) { Write-Host ("   main hwnd now = " + $p.MainWindowHandle + " title = '" + $p.MainWindowTitle + "'") }
