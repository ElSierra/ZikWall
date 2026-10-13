param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\ZikWall'))
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($Directory)
$app = Join-Path $directory 'ZikWall.exe'
$report = Join-Path $directory 'passive-dns-selftest.json'
if (-not (Test-Path -LiteralPath $app)) { throw 'Build ZikWall before running this diagnostic.' }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $app -WorkingDirectory $directory -ArgumentList '--test-passive-dns' -Verb RunAs -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(90000)) { throw 'Passive DNS diagnostic timed out.' }
    if ($process.ExitCode -ne 0) { throw "Passive DNS diagnostic failed: $($process.ExitCode)" }
} finally { $process.Dispose() }
if (-not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'Missing fresh passive DNS report.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$result | ConvertTo-Json
if (-not $result.Passed -or $result.Error -or -not $result.OwnedTraceStopped) { throw "Passive DNS diagnostic failed: $($result.Error) $($result.CleanupError)" }
Write-Host 'PASS: direct UDP DNS reply captured, hostname enriched, requests validated, owned capture stopped. No DNS settings or firewall rules changed.'
