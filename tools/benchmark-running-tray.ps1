param([ValidateRange(15, 600)][int]$DurationSeconds = 60)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'artifacts\ZikWall'
$setup = Get-Content (Join-Path $directory 'tray-startup-selftest.json') -Raw | ConvertFrom-Json
$target = Get-Process -Id $setup.ProcessId -ErrorAction Stop
if ($target.Path -ne (Join-Path $directory 'ZikWall.exe')) { throw 'Unexpected process path.' }
if ($target.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Tray benchmark is invalid: the app has a visible window. Leave it untouched and use benchmark-app.ps1 -Background for an isolated measurement.' }
# Read-only measurement of the actual configured tray instance. It stays running.
Start-Sleep -Seconds 10
$target.Refresh()
if ($target.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Tray benchmark is invalid: the app was restored during warmup.' }
$initialCpu = $target.TotalProcessorTime.TotalMilliseconds
$measure = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
while ($measure.Elapsed.TotalSeconds -lt $DurationSeconds) {
    Start-Sleep -Seconds 2
    $target.Refresh()
    if ($target.HasExited) { throw 'Tray app exited during measurement.' }
    if ($target.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Tray benchmark is invalid: the app was restored during measurement.' }
    $samples.Add([pscustomobject]@{ Seconds=$measure.Elapsed.TotalSeconds; PrivateMiB=[Math]::Round($target.PrivateMemorySize64/1MB,2); WorkingSetMiB=[Math]::Round($target.WorkingSet64/1MB,2); CpuMilliseconds=$target.TotalProcessorTime.TotalMilliseconds })
}
$meanCpu = 100 * ($target.TotalProcessorTime.TotalMilliseconds - $initialCpu) / $measure.Elapsed.TotalMilliseconds
$peak = ($samples | Measure-Object PrivateMiB -Maximum).Maximum
$result = [ordered]@{
    Label='final-native-tray'
    TimestampUtc=[DateTimeOffset]::UtcNow.ToString('O')
    Scenario='Actual configured sign-in instance hidden in native tray; 15-second connection snapshots; collector/filtering/DNS off'
    AssemblySha256=(Get-FileHash (Join-Path $directory 'ZikWall.dll') -Algorithm SHA256).Hash
    MeasuredSeconds=[Math]::Round($measure.Elapsed.TotalSeconds,2)
    CpuOneCorePercent=[Math]::Round($meanCpu,3)
    PeakPrivateMiB=$peak
    PeakWorkingSetMiB=($samples | Measure-Object WorkingSetMiB -Maximum).Maximum
    BudgetsPassed=($meanCpu -le 2 -and $peak -le 200)
    Samples=$samples
}
$result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'artifacts\benchmarks\final-native-tray.json') -Encoding utf8
[pscustomobject]$result | Select-Object Label,MeasuredSeconds,CpuOneCorePercent,PeakPrivateMiB,PeakWorkingSetMiB,BudgetsPassed | Format-List
if (-not $result.BudgetsPassed) { throw 'Tray instance exceeded the monitoring resource budget.' }
