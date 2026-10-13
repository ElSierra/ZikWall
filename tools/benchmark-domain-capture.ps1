param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\ZikWall'), [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\benchmarks\domain-capture.json'))
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($Directory)
$report = Join-Path $directory 'domain-capture-benchmark.json'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath (Join-Path $directory 'ZikWall.exe') -WorkingDirectory $directory -ArgumentList '--test-domain-benchmark' -Verb RunAs -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(300000)) { $null = $process.CloseMainWindow(); throw "DNS capture benchmark timed out. Check task-owned ZikWall PID $($process.Id); an administrator may need to close it before another run." }
} finally { $process.Dispose() }
if (-not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'Missing fresh DNS capture benchmark.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
[IO.Directory]::CreateDirectory((Split-Path ([IO.Path]::GetFullPath($OutputPath)))) | Out-Null
Copy-Item -LiteralPath $report -Destination $OutputPath -Force
$result | Select-Object Completed,CleanupVerified,StartupMs,QueriesSent,ValidReplies,QueryTimeouts,DnsRoundTrip,HintIpc,LastStatus,Failure | ConvertTo-Json -Depth 5
$result.Phases | Select-Object Scenario,Seconds,CombinedCpuOneCorePercent,SumOfPeakPrivateMiB,Processes | ConvertTo-Json -Depth 5
if (-not $result.Completed -or -not $result.CleanupVerified) { throw 'DNS capture benchmark failed; inspect its report.' }
