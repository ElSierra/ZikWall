param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\ZikWall'))
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($Directory)
$app = Join-Path $directory 'ZikWall.exe'
$report = Join-Path $directory 'throughput-selftest.json'
$started = [DateTime]::UtcNow
# The UI process stays unelevated. Its existing broker requests UAC itself.
# No user policy is loaded; every rule targets this test executable and loopback.
$process = Start-Process -FilePath $app -WorkingDirectory $directory -ArgumentList '--test-throughput' -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(60000)) {
        Write-Host 'Waiting for the scoped broker test to finish (including Windows UAC approval).'
        if (!$process.WaitForExit(60000)) { throw 'The diagnostic is still waiting/running. No result is accepted; inspect the Windows UAC prompt and the test process.' }
    }
    if (!(Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) { throw 'No fresh throughput report was produced.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $result.Comparisons | Format-Table -AutoSize
    if (!$result.Completed) { throw "Throughput diagnostic incomplete: $($result.Failure)" }
    if ($process.ExitCode -ne 0 -or !$result.ThroughputBudgetPassed) { throw 'Paired throughput fell below the 95% comparison threshold. Inspect all raw samples before drawing conclusions.' }
    Write-Host 'PASS: scoped enforcement/cleanup and paired allowed TCP loopback throughput with 2 and 1,000 rules.'
    Write-Host "Report: $report"
} finally { $process.Dispose() }
