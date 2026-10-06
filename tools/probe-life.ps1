$np = Start-Process notepad -PassThru
Start-Sleep -Milliseconds 2000
Write-Host ("started pid={0} exited={1}" -f $np.Id, $np.HasExited)
Get-Process notepad | ForEach-Object { Write-Host ("  proc {0} main={1} '{2}'" -f $_.Id, $_.MainWindowHandle, $_.MainWindowTitle) }

Start-Process -FilePath 'C:\Users\user\win-caption-menu\out\CaptionMenu.exe' | Out-Null
Start-Sleep -Seconds 3
Write-Host '--- after starting CaptionMenu ---'
Get-Process notepad -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("  proc {0} main={1} '{2}' exited={3}" -f $_.Id, $_.MainWindowHandle, $_.MainWindowTitle, $_.HasExited) }
Write-Host ("original np.Id still={0}" -f $np.Id)
