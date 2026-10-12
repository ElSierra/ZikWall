param(
    [string]$Label = 'current',
    [ValidateRange(15, 3600)][int]$DurationSeconds = 60,
    [ValidateRange(5, 120)][int]$WarmupSeconds = 10,
    [ValidateRange(0.01, 100)][double]$CpuBudgetOneCorePercent = 2,
    [ValidateRange(1, 4096)][double]$PrivateMemoryBudgetMiB = 200,
    [switch]$EnforceBudgets,
    [switch]$Background,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\artifacts\ZikWall\ZikWall.exe'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\benchmarks\app.json')
)
$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($ExecutablePath)
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $app)) { throw 'Build ZikWall before benchmarking.' }
# A normal fresh process starts monitoring but never enables filtering or DNS.
# Do not interact with its window during measurement; only this process is closed.
if ($Background -and -not ('ZikWallBenchmarkWindow' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ZikWallBenchmarkWindow {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
"@
}
$ownedWindow = [IntPtr]::Zero
$launch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -ArgumentList '--benchmark' -WindowStyle Hidden -PassThru
try {
    $windowReadyMs = $null
    while ($launch.Elapsed.TotalSeconds -lt $WarmupSeconds) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "App exited during warmup ($($process.ExitCode))." }
        if ($null -eq $windowReadyMs -and $process.MainWindowHandle -ne 0) {
            $windowReadyMs = $launch.Elapsed.TotalMilliseconds
            $ownedWindow = $process.MainWindowHandle
            if ($Background) { $null = [ZikWallBenchmarkWindow]::ShowWindow($ownedWindow, 0) }
        }
    }
    $process.Refresh()
    if ($Background -and ($ownedWindow -eq [IntPtr]::Zero -or [ZikWallBenchmarkWindow]::IsWindowVisible($ownedWindow))) { throw 'Background benchmark is invalid: the test window was not hidden.' }
    $startCpu = $process.TotalProcessorTime.TotalMilliseconds
    $previousCpu = $startCpu
    $previousMs = 0
    $samples = [Collections.Generic.List[object]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        Start-Sleep -Seconds 2
        $process.Refresh()
        if ($process.HasExited) { throw "App exited during measurement ($($process.ExitCode))." }
        if ($Background -and [ZikWallBenchmarkWindow]::IsWindowVisible($ownedWindow)) { throw 'Background benchmark is invalid: the test window became visible.' }
        $elapsed = $clock.Elapsed.TotalMilliseconds
        $cpu = $process.TotalProcessorTime.TotalMilliseconds
        $samples.Add([pscustomobject]@{
            Seconds = [Math]::Round($elapsed / 1000, 2)
            CpuOneCorePercent = [Math]::Round(100 * ($cpu - $previousCpu) / ($elapsed - $previousMs), 3)
            PrivateMiB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
            WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 2)
            Handles = $process.HandleCount
            Threads = $process.Threads.Count
        })
        $previousCpu = $cpu; $previousMs = $elapsed
    }
    $meanCpu = 100 * ($previousCpu - $startCpu) / $previousMs
    $cpuModel = $null; $memoryGiB = $null
    try {
        $cpuModel = (Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name) -join ', '
        $memoryGiB = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    } catch { } # Optional hardware metadata must not discard valid measurements.
    $result = [ordered]@{
        Label = $Label
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        AssemblySha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $app) 'ZikWall.dll') -Algorithm SHA256).Hash
        OS = [Environment]::OSVersion.VersionString
        LogicalProcessors = [Environment]::ProcessorCount
        CpuModel = $cpuModel
        PhysicalMemoryGiB = $memoryGiB
        Scenario = $(if ($Background) { 'Hidden window; 15-second background monitoring; collector/filtering/DNS off' } else { 'Fresh grouped Connections page; normal host traffic; collector/filtering/DNS off; no interactions' })
        WarmupSeconds = $WarmupSeconds
        MeasuredSeconds = [Math]::Round($previousMs / 1000, 2)
        WindowReadyMs = $windowReadyMs
        CpuOneCorePercent = [Math]::Round($meanCpu, 3)
        CpuMachinePercent = [Math]::Round($meanCpu / [Environment]::ProcessorCount, 3)
        PeakPrivateMiB = ($samples | Measure-Object PrivateMiB -Maximum).Maximum
        PeakWorkingSetMiB = ($samples | Measure-Object WorkingSetMiB -Maximum).Maximum
        PrivateGrowthMiB = [Math]::Round($samples[-1].PrivateMiB - $samples[0].PrivateMiB, 2)
        PeakHandles = ($samples | Measure-Object Handles -Maximum).Maximum
        CpuBudgetOneCorePercent = $CpuBudgetOneCorePercent
        PrivateMemoryBudgetMiB = $PrivateMemoryBudgetMiB
        BudgetsPassed = $meanCpu -le $CpuBudgetOneCorePercent -and ($samples | Measure-Object PrivateMiB -Maximum).Maximum -le $PrivateMemoryBudgetMiB
        Samples = $samples
    }
    [IO.Directory]::CreateDirectory((Split-Path $output)) | Out-Null
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
    [pscustomobject]$result | Select-Object Label, MeasuredSeconds, CpuOneCorePercent, CpuMachinePercent, PeakPrivateMiB, PeakWorkingSetMiB, PrivateGrowthMiB, PeakHandles | Format-List
    Write-Output "Results: $output"
    if ($EnforceBudgets -and -not $result.BudgetsPassed) { throw 'Resource budget exceeded. See the benchmark JSON for measurements.' }
}
finally {
    if (-not $process.HasExited) {
        if ($Background) {
            $owner = [uint32]0
            $null = [ZikWallBenchmarkWindow]::GetWindowThreadProcessId($ownedWindow, [ref]$owner)
            if ($owner -ne $process.Id) { throw 'Benchmark window ownership changed.' }
            if (-not [ZikWallBenchmarkWindow]::PostMessage($ownedWindow, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)) { throw 'Could not close the owned benchmark window.' }
        } elseif (-not $process.CloseMainWindow()) { throw 'Could not ask the benchmark app to close.' }
        if (-not $process.WaitForExit(10000)) { throw 'Benchmark app did not close normally.' }
    }
    $process.Dispose()
}
