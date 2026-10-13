param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\ZikWall'))
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($Directory)
$app = Join-Path $directory 'ZikWall.exe'
$report = Join-Path $directory 'domain-cache-selftest.json'
if (-not (Test-Path -LiteralPath $app)) { throw 'Build ZikWall before running this diagnostic.' }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $app -WorkingDirectory $directory -ArgumentList '--test-domain-cache' -Verb RunAs -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(60000)) { throw 'Domain-cache diagnostic timed out.' }
    if ($process.ExitCode -ne 0) { throw "Domain-cache diagnostic failed: $($process.ExitCode)" }
} finally { $process.Dispose() }
if (-not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'Missing fresh domain-cache report.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.Passed -or $result.Error) { throw "Domain-cache diagnostic failed: $($result.Error)" }
$result | ConvertTo-Json
Write-Host 'PASS: read-only Windows DNS cache provider and isolated broker request validation. No DNS requests or settings changes.'
