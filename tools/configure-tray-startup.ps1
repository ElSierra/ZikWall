# Explicit setup requested by the user: registers this build at sign-in and leaves it in the tray.
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\ZikWall'))
$app = Join-Path $directory 'ZikWall.exe'
if (-not ('ZikWallTrayCheck' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ZikWallTrayCheck {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
"@
}
$process = Start-Process -FilePath $app -ArgumentList '--enable-startup' -WindowStyle Hidden -PassThru
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
    if ($process.HasExited) { throw 'An existing instance was restored. Close it through tray Exit before running this setup diagnostic again.' }
    if ($process.MainWindowHandle -ne 0) { break }
}
$window = $process.MainWindowHandle
Start-Sleep -Seconds 6
$process.Refresh()
Write-Host "Testing window $window ($($process.MainWindowTitle)), PID $($process.Id)"
$owner = [uint32]0
$null = [ZikWallTrayCheck]::GetWindowThreadProcessId($window, [ref]$owner)
if ($window -eq [IntPtr]::Zero -or $owner -ne $process.Id) { throw 'Could not identify the newly launched app window.' }
# This is the same WM_CLOSE path as the window's X button, scoped to our own child.
$null = [ZikWallTrayCheck]::PostMessage($window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
    if ($process.HasExited -or -not [ZikWallTrayCheck]::IsWindowVisible($window)) { break }
}
if ($process.HasExited) { throw "X exited the process with code $($process.ExitCode)." }
if ([ZikWallTrayCheck]::IsWindowVisible($window)) { throw 'X left the window visible after ten seconds.' }
$duplicate = Start-Process -FilePath $app -ArgumentList '--tray' -WindowStyle Hidden -PassThru
if (-not $duplicate.WaitForExit(10000) -or $duplicate.ExitCode -ne 0) { throw 'Quiet single-instance launch failed.' }
$restore = Start-Process -FilePath $app -WindowStyle Hidden -PassThru
if (-not $restore.WaitForExit(10000) -or $restore.ExitCode -ne 0) { throw 'Single-instance restore failed.' }
Start-Sleep -Milliseconds 700
if (-not [ZikWallTrayCheck]::IsWindowVisible($window)) { throw 'The existing tray window did not restore.' }
$null = [ZikWallTrayCheck]::PostMessage($window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try { $registered = $key.GetValue('ZikWall') } finally { $key.Dispose() }
if ($registered -ne ('"' + $app + '" --tray')) { throw 'Startup registration does not match the current build.' }
$process.Refresh()
if ($process.HasExited -or [ZikWallTrayCheck]::IsWindowVisible($window)) { throw 'The final tray state is not running and hidden.' }
$result = [ordered]@{ Passed = $true; ProcessId = $process.Id; StartupCommand = $registered; CloseHides = $true; SingleInstance = $true; RestoreWorks = $true; RunningInTray = $true }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'tray-startup-selftest.json') -Encoding utf8
[pscustomobject]$result | Format-List
$duplicate.Dispose(); $restore.Dispose(); $process.Dispose()
