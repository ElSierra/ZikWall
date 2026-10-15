param([switch]$Launch, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'ui\ZikWall.WinUI\ZikWall.WinUI.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\ZikWall' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
Push-Location $root
try {
    & dotnet restore $project -p:Platform=x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked package restore failed.' }
    & dotnet build $project -p:Platform=x64 "-p:OutDir=$output\" -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    & (Join-Path $PSScriptRoot 'test-location-payload.ps1') -Directory $output
    & (Join-Path $PSScriptRoot 'build-engine.ps1') -OutputDirectory $output
    & dotnet run --project tests\WinUI.Models.Tests -c Release -- bin\profile_internal.xml
    if ($LASTEXITCODE -ne 0) { throw 'Profile-reader tests failed.' }
    & (Join-Path $PSScriptRoot 'test-supply-chain.ps1')
    $app = Join-Path $output 'ZikWall.exe'
    Write-Host "Built: $app"
    if ($Launch) { Start-Process -FilePath $app }
} finally { Pop-Location }
