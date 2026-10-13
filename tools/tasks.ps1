param([Parameter(Mandatory)][string]$Task, [string]$Version, [string]$InputFile, [ValidateRange(15,3600)][int]$Seconds = 60)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dev = Join-Path $root 'artifacts\ZikWall'
$app = Join-Path $dev 'ZikWall.exe'
$installed = Join-Path $env:ProgramFiles 'ZikWall\ZikWall.exe'
$registeredLocation = @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -eq 'ZikWall' -and $_.InstallLocation } | Select-Object -ExpandProperty InstallLocation -Unique)
if ($registeredLocation.Count -eq 1) { $installed = Join-Path $registeredLocation[0] 'ZikWall.exe' }
function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}
function Require-Dev {
    if (-not (Test-Path -LiteralPath $app)) { throw 'No development build. Run make build first.' }
}
function Assert-NoOtherInstance {
    foreach ($process in @(Get-Process -Name ZikWall -ErrorAction SilentlyContinue)) {
        if ($process.Path -ne $app) { throw 'Exit the installed ZikWall through its tray menu before starting a development session.' }
    }
}
function Stop-Dev {
    Get-Process -Name ZikWall -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app } | Stop-Process
}
function Remove-BuildDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $full -eq $root) { throw "Unsafe cleanup path: $full" }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked directory: $full" }
        if (Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint) { throw "Refusing directory containing links: $full" }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
function Run-Diagnostic([string]$Mode) {
    Require-Dev
    $started = [DateTime]::UtcNow
    $process = Start-Process -FilePath $app -ArgumentList $Mode -WorkingDirectory $dev -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(180000)) { throw "$Mode did not finish within three minutes. Check its window or Windows UAC prompt." }
        if ($process.ExitCode -ne 0) { throw "$Mode failed with exit code $($process.ExitCode)." }
    } finally { $process.Dispose() }
    $reportName = switch ($Mode) {
        '--test-broker' { 'broker-self-test.json' }
        '--test-usage' { 'usage-selftest.json' }
        '--test-lifecycle' { 'lifecycle-selftest.txt' }
        '--test-map' { 'map-selftest.json' }
    }
    $reportPath = Join-Path $dev $reportName
    if (-not (Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) { throw "No fresh diagnostic result: $reportPath" }
    $content = Get-Content -LiteralPath $reportPath -Raw
    if ($Mode -eq '--test-lifecycle') {
        if ($content -notmatch '^PASS:') { throw $content }
    } else {
        $result = $content | ConvertFrom-Json
        if ($result.Error -or $result.Failure -or $result.CoverageFailure) { throw "Diagnostic failed: $content" }
        if ($Mode -eq '--test-broker' -and -not $result.Success) { throw 'Broker diagnostic did not pass.' }
        if ($Mode -eq '--test-usage' -and (-not $result.StopAcknowledged -or @('TCPv4','TCPv6','UDPv4','UDPv6').Where({ -not $result.$_.Covered }).Count)) { throw 'ETW coverage or collector cleanup failed.' }
        if ($Mode -eq '--test-map' -and (-not $result.ExecutableIconDecoded -or $result.FailedFlagDecodes.Count -or $result.RenderedWidth -le 0)) { throw 'Map rendering diagnostic failed.' }
    }
    Write-Host "PASS: $Mode ($reportPath)"
}
Push-Location $root
try {
    switch ($Task) {
        help {
            @'
ZikWall / isaacojo.me

Build & develop
  make build                 Build portable app + engine; run core checks
  make dev                   Build and launch a development session
  make run / run-installed   Launch the existing dev / installed app
  make stop                  Stop only the workspace dev app
  make restore / engine      Restore locked UI packages / build native broker
  make setup                 Build and verify WinUI setup.exe and setup.msi
  make setup VERSION=1.0.0   Build with an explicit MSI release version
  make install / uninstall   Open setup / uninstall the installed MSI

Checks & diagnostics
  make test / lint / check   Model/service tests / integrity checks / both
  make test-ui               Native UI churn and context-menu checks
  make test-domains          Read-only cache/provider diagnostic (requests UAC)
  make verify-msi            Extract MSI and verify payload hashes
  make verify-installed      Also compare the installed files with this MSI
  make test-lifecycle        Tray lifecycle diagnostic on the dev build
  make test-map              Offline map render diagnostic
  make test-engine           Elevated loopback-only native WFP checks
  make test-broker           Broker test (requests UAC; scoped loopback rules)
  make test-throughput       WFP throughput test (requests UAC)
  make test-usage            ETW usage diagnostic (requests UAC)

Performance & maintenance
  make benchmark            Model/service benchmark suite
  make benchmark-ui         Foreground app benchmark (SECONDS=60)
  make benchmark-background Background app benchmark (SECONDS=60)
  make benchmark-tray       Existing configured dev tray (SECONDS=60, max 600)
  make startup              Explicitly register dev startup and test tray
  make assets               Regenerate installer artwork and GPL license page
  make brand-assets         Regenerate logo/ICO files (Python + Pillow)
  make world-map            Rebuild map from temp/natural-earth-land.geojson
  make country-points       Rebuild positions (INPUT=<pinned GeoJSON>)
  make country-data         Rebuild country tables (INPUT=<pinned CSV.gz>)
  make network-data         Rebuild network owner tables (INPUT=<pinned ASN CSV.gz>)
  make flags                Extract flags (INPUT=<pinned flag-icons ZIP>)
  make doctor               Check build tools and installed app location
  make clean                Remove dev/compiler output; retain MSI and archives
  make clean-msi            Remove MSI output and temporary MSI staging

Exit the installed app through tray Exit before make dev or UI benchmarks.
make install opens the existing WinUI setup.exe; run make setup after source changes.
No make installation? Use powershell -File tools/tasks.ps1 -Task <target>.
'@ | Write-Host
        }
        build { & "$PSScriptRoot\build-winui.ps1" }
        dev { Assert-NoOtherInstance; Stop-Dev; & "$PSScriptRoot\build-winui.ps1"; Start-Process -FilePath $app -WorkingDirectory $dev }
        run { Require-Dev; Assert-NoOtherInstance; Start-Process -FilePath $app -WorkingDirectory $dev }
        run-installed { if (-not (Test-Path -LiteralPath $installed)) { throw 'ZikWall is not installed.' }; Start-Process -FilePath $installed }
        stop { Stop-Dev }
        restore { Invoke-Native dotnet @('restore','ui\ZikWall.WinUI\ZikWall.WinUI.csproj','-p:Platform=x64','--locked-mode','--force') }
        engine { & "$PSScriptRoot\build-engine.ps1" }
        setup { & "$PSScriptRoot\build-setup-exe.ps1" -Version $Version }
        install {
            & "$PSScriptRoot\test-setup.ps1"
            $setupExe = Join-Path $root 'artifacts\installer\setup.exe'
            $setupRecord = Get-Content (Join-Path $root 'artifacts\installer\setup-exe-manifest.json') -Raw | ConvertFrom-Json
            $msiRecord = Get-Content (Join-Path $root 'artifacts\installer\manifest.json') -Raw | ConvertFrom-Json
            if ($setupRecord.exeSha256 -ne (Get-FileHash $setupExe -Algorithm SHA256).Hash -or $setupRecord.msiSha256 -ne $msiRecord.sha256) { throw 'Setup EXE is out of date or changed. Run make setup.' }
            Start-Process -FilePath $setupExe
        }
        uninstall {
            $entry = @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -eq 'ZikWall' -and $_.PSChildName -match '^\{[0-9A-F-]{36}\}$' })
            if ($entry.Count -ne 1) { throw 'Expected one installed ZikWall MSI.' }
            Start-Process -FilePath msiexec.exe -ArgumentList @('/x', $entry[0].PSChildName)
        }
        test { Invoke-Native dotnet @('run','--project','tests\WinUI.Models.Tests','-c','Release','--','bin\profile_internal.xml') }
        test-ui { Require-Dev; & "$PSScriptRoot\test-monitor-ui.ps1" -Directory $dev }
        test-domains { Require-Dev; & "$PSScriptRoot\test-domain-hints.ps1" -Directory $dev }
        test-engine {
            & "$PSScriptRoot\build-engine.ps1"
            Invoke-Native dotnet @('build','tests\ZikWall.Engine.Tests','-c','Release')
            $testExe = Join-Path $root 'tests\ZikWall.Engine.Tests\bin\Release\net8.0-windows\win-x64\ZikWall.Engine.Tests.exe'
            $process = Start-Process -FilePath $testExe -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            if ($process.ExitCode -ne 0) { throw "Engine diagnostic failed: $($process.ExitCode)" }
        }
        test-throughput { Require-Dev; & "$PSScriptRoot\test-throughput.ps1" -Directory $dev }
        test-broker { Run-Diagnostic '--test-broker' }
        test-usage { Run-Diagnostic '--test-usage' }
        test-lifecycle { Require-Dev; Assert-NoOtherInstance; Stop-Dev; Run-Diagnostic '--test-lifecycle' }
        test-map { Run-Diagnostic '--test-map' }
        lint {
            Invoke-Native git @('-c','core.whitespace=cr-at-eol','diff','--check')
            & "$PSScriptRoot\test-supply-chain.ps1"
            foreach ($file in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1')) {
                $errors = $null; $tokens = $null
                [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw ($errors | Out-String) }
            }
            Write-Host 'PASS: PowerShell scripts parse.'
        }
        verify-msi { & "$PSScriptRoot\test-setup.ps1" }
        verify-installed { & "$PSScriptRoot\test-setup.ps1" -InstallDirectory (Split-Path $installed) }
        benchmark { Invoke-Native dotnet @('run','--project','tests\ZikWall.Benchmarks','-c','Release','--','artifacts\benchmarks\core.json') }
        benchmark-ui { Require-Dev; Assert-NoOtherInstance; & "$PSScriptRoot\benchmark-app.ps1" -DurationSeconds $Seconds }
        benchmark-background { Require-Dev; Assert-NoOtherInstance; & "$PSScriptRoot\benchmark-app.ps1" -DurationSeconds $Seconds -Background }
        benchmark-tray { & "$PSScriptRoot\benchmark-running-tray.ps1" -DurationSeconds $Seconds }
        startup { Require-Dev; Assert-NoOtherInstance; Stop-Dev; & "$PSScriptRoot\configure-tray-startup.ps1" }
        assets { & "$PSScriptRoot\generate-installer-assets.ps1" }
        brand-assets { Invoke-Native python @('tools\generate-brand-assets.py') }
        world-map { Invoke-Native python @('tools\generate-world-map.py') }
        { $_ -in @('country-points','country-data','network-data','flags') } {
            if (-not $InputFile -or -not (Test-Path -LiteralPath $InputFile -PathType Leaf)) { throw 'Supply an existing pinned input: make <target> INPUT="path". See docs/MAP-DATA.md and asset provenance.' }
            $script = switch ($Task) { 'country-points' { 'generate-country-points.py' }; 'country-data' { 'build-country-data.py' }; 'network-data' { 'build-network-data.py' }; 'flags' { 'extract-monitor-flags.py' } }
            Invoke-Native python @((Join-Path $PSScriptRoot $script), $InputFile)
        }
        doctor {
            foreach ($name in @('dotnet','make','powershell')) { Get-Command $name -ErrorAction Stop | Select-Object Name,Source }
            Invoke-Native dotnet @('--version')
            $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
            Invoke-Native $vswhere @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath')
            Write-Host "Installed app: $(Test-Path -LiteralPath $installed) ($installed)"
            Write-Host "Pinned WiX: $(Test-Path -LiteralPath (Join-Path $root 'temp\wix\wix.exe')) (downloaded on first make setup)"
        }
        clean {
            if (Get-Process -Name ZikWall -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app }) { throw 'Run make stop before make clean.' }
            Remove-BuildDirectory $dev
            foreach ($project in (Get-ChildItem ui,engine,tests -Recurse -Filter '*.csproj')) {
                foreach ($name in @('bin','obj')) { Remove-BuildDirectory (Join-Path $project.DirectoryName $name) }
            }
            Remove-BuildDirectory (Join-Path $root 'temp\engine')
        }
        clean-msi {
            Remove-BuildDirectory (Join-Path $root 'artifacts\installer')
            Get-ChildItem (Join-Path $root 'temp') -Directory -Filter 'msi-*' | ForEach-Object { Remove-BuildDirectory $_.FullName }
        }
        default { throw "Unknown task: $Task. Run make help." }
    }
} finally { Pop-Location }
