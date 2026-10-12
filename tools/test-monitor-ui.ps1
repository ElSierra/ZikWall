param([ValidateRange(1, 10)][int]$Runs = 2, [switch]$RenderAndNotify, [string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\ZikWall'))
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($Directory)
$app = Join-Path $directory 'ZikWall.exe'
function Run-Diagnostic([string]$Mode) {
    $process = Start-Process -FilePath $app -WorkingDirectory $directory -ArgumentList $Mode -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) {
            $null = $process.CloseMainWindow()
            throw "$Mode timed out."
        }
        if ($process.ExitCode -ne 0) { throw "$Mode crashed: $($process.ExitCode)" }
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
