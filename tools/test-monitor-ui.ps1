param([ValidateRange(1, 10)][int]$Runs = 2, [switch]$RenderAndNotify, [string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\ZikWall'))
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($Directory)
$app = Join-Path $directory 'ZikWall.exe'
function Run-Diagnostic([string]$Mode) {
    $startedUtc = [DateTime]::UtcNow
    $reportName = switch ($Mode) {
        '--test-monitor-churn' { 'monitor-churn.txt' }
        '--test-monitor' { 'monitor-selftest.json' }
        default { $Mode.Replace('--test-', '') + '-selftest.txt' }
    }
    $process = Start-Process -FilePath $app -WorkingDirectory $directory -ArgumentList $Mode -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) {
            $null = $process.CloseMainWindow()
            throw "$Mode timed out."
        }
        if ($process.ExitCode -ne 0) { throw "$Mode crashed: $($process.ExitCode)" }
        $report = Get-Item (Join-Path $directory $reportName)
        if ($report.LastWriteTimeUtc -lt $startedUtc) { throw "$Mode did not produce a fresh result." }
    } finally { $process.Dispose() }
}
for ($i = 0; $i -lt $Runs; $i++) {
    Run-Diagnostic '--test-monitor-churn'
    if ((Get-Content (Join-Path $directory 'monitor-churn.txt') -Raw) -notmatch '^PASS: 500') { throw 'Missing native churn result.' }
}
Write-Host "PASS: $Runs runs of 500 native list updates with icons, flags and alert-banner resizing."
if ($RenderAndNotify) {
    # Explicit opt-in emits one labeled silent Windows notification and a local render.
    Run-Diagnostic '--test-monitor'
    $result = Get-Content (Join-Path $directory 'monitor-selftest.json') -Raw | ConvertFrom-Json
    if ($result.Error -or !$result.ExecutableIconDecoded -or $result.FailedFlagDecodes.Count -ne 0 -or !$result.WindowsNotificationSubmitted -or $result.RenderedWidth -le 0) { throw 'Native monitor diagnostic failed; inspect monitor-selftest.json.' }
    Write-Host 'PASS: native icon/flag decoding, notification submission and XAML render.'
}

Run-Diagnostic '--test-context-menu'
$context = Get-Content (Join-Path $directory 'context-menu-selftest.txt') -Raw
if ($context -notmatch '^PASS: 8 native context-menu' -or $context -notmatch 'PASS: native block-review cancellation') { throw "Context-menu checks failed: $context" }
Write-Host 'PASS: native context-menu routing, invocation, pinned targets and block-review cancellation.'

Run-Diagnostic '--test-monitor-options'
$options = Get-Content (Join-Path $directory 'monitor-options-selftest.txt') -Raw
if ($options -notmatch '^PASS: separate native options window') { throw "Options layout checks failed: $options" }
Write-Host $options

Run-Diagnostic '--test-usage-view'
$usage = Get-Content (Join-Path $directory 'usage-view-selftest.txt') -Raw
if ($usage -notmatch '^PASS: native usage') { throw "Usage column checks failed: $usage" }
Write-Host $usage

Run-Diagnostic '--test-system-apps'
$systemApps = Get-Content (Join-Path $directory 'system-apps-selftest.txt') -Raw
if ($systemApps -notmatch '^PASS: native toggle') { throw "System-app toggle checks failed: $systemApps" }
Write-Host $systemApps

Run-Diagnostic '--test-preferences'
$preferences = Get-Content (Join-Path $directory 'preferences-selftest.txt') -Raw
if ($preferences -notmatch '^PASS:') { throw "Settings restart checks failed: $preferences" }
Write-Host $preferences

Run-Diagnostic '--test-policy-layout'
$policyLayout = Get-Content (Join-Path $directory 'policy-layout-selftest.txt') -Raw
if ($policyLayout -notmatch '^PASS:' -or ($policyLayout | Select-String 'PASS:' -AllMatches).Matches.Count -ne 4) { throw "Policy layout checks failed: $policyLayout" }
Write-Host $policyLayout

foreach ($page in @('activity-overview', 'log-analysis')) {
    $started = Get-Date
    Run-Diagnostic "--test-$page"
    $report = Get-Item (Join-Path $directory "$page-selftest.txt")
    $result = Get-Content $report.FullName -Raw
    if ($report.LastWriteTime -lt $started -or $result -notmatch '^PASS:') { throw "$page checks failed: $result" }
    Write-Host $result
}

Run-Diagnostic '--test-rules'
$rules = Get-Content (Join-Path $directory 'rules-selftest.txt') -Raw
if ($rules -notmatch '^PASS:') { throw "Separate rule editor checks failed: $rules" }
Write-Host $rules
