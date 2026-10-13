param([switch]$UseExistingMsi, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $UseExistingMsi) { & "$PSScriptRoot\build-setup.ps1" -Version $Version }
$msi = Join-Path $root 'artifacts\installer\setup.msi'
$manifestPath = Join-Path $root 'artifacts\installer\manifest.json'
& "$PSScriptRoot\test-setup.ps1" -MsiPath $msi -ManifestPath $manifestPath -RemoveStaging
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$work = Join-Path $root ('temp\setup-exe-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$null = New-Item -ItemType Directory -Path $payload -Force
$project = Join-Path $root 'ui\ZikWall.Setup\ZikWall.Setup.csproj'
$properties = @('-p:Platform=x64', "-p:SetupMsiPath=$msi", "-p:PackageSha256=$($manifest.sha256)", "-p:PackageVersion=$($manifest.version)")
& dotnet restore $project @properties --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Setup dependency restore failed.' }
& dotnet build $project @properties "-p:OutDir=$payload\" -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'WinUI setup build failed.' }
# Use exactly the release VC runtime already verified in this MSI's fresh build.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
$crt = Get-ChildItem (Join-Path $visualStudio 'VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT') -Directory | Where-Object { $_.Parent.Parent.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Parent.Parent.Name } -Descending | Select-Object -First 1
if (-not $crt) { throw 'Cannot locate the x64 release Visual C++ runtime.' }
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $payload -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $work 'setup-payload.zip'
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object Extension -ne '.pdb')) {
        $relative = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$launcher = Join-Path $root 'tools\ZikWall.SetupLauncher\ZikWall.SetupLauncher.csproj'
$output = Join-Path $work 'launcher'
& dotnet publish $launcher -c Release -o $output "-p:IntermediateOutputPath=$work\launcher-obj\" "-p:SetupPayloadPath=$zipPath" "-p:PayloadSha256=$zipHash" "-p:Version=$($manifest.version)" -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Single-file setup launcher build failed.' }
$candidate = Join-Path $output 'setup.exe'
$started = Get-Date
$process = Start-Process -FilePath $candidate -ArgumentList '--self-test' -WorkingDirectory $output -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(60000)) { throw 'Setup self-test timed out.' }
    if ($process.ExitCode -ne 0) { throw "Setup self-test failed ($($process.ExitCode)); inspect $output." }
} finally { $process.Dispose() }
$reportPath = Join-Path $output 'setup-preview\setup-selftest.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.Passed -or $report.InstallationPerformed -or (Get-Item $reportPath).LastWriteTime -lt $started) { throw 'Setup verification failed.' }
$destination = Join-Path $root 'artifacts\installer'
Copy-Item -LiteralPath $candidate -Destination (Join-Path $destination 'setup.exe') -Force
$preview = Join-Path $destination 'setup-preview'
$null = New-Item -ItemType Directory -Path $preview -Force
Get-ChildItem (Join-Path $output 'setup-preview') -File | Copy-Item -Destination $preview -Force
$record = [ordered]@{ version = $manifest.version; exeSha256 = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash; msiSha256 = $manifest.sha256; payloadSha256 = $zipHash; checks = $report.Checks; installationPerformed = $false }
$record | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $destination 'setup-exe-manifest.json') -Encoding UTF8
Write-Host "PASS: self-contained setup.exe extraction, MSI integrity, native WinUI screens and $($report.Checks.Count) diagnostic checks. No installation performed."
Write-Host "Built: $(Join-Path $destination 'setup.exe')"
$resolvedWork = [IO.Path]::GetFullPath($work)
$tempRoot = [IO.Path]::GetFullPath((Join-Path $root 'temp'))
if (-not $resolvedWork.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    ((Get-Item -LiteralPath $resolvedWork).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    (Get-ChildItem -LiteralPath $resolvedWork -Recurse -Force -Attributes ReparsePoint)) { throw 'Refusing unsafe setup staging cleanup.' }
Remove-Item -LiteralPath $resolvedWork -Recurse -Force
