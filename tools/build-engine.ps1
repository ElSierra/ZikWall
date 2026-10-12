param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\temp\engine'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -products '*' -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio C++ build tools are required.' }
& $msbuild (Join-Path $root 'engine\ZikWall.Engine\ZikWall.Engine.vcxproj') /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows SDK WFP engine build failed.' }
& dotnet publish (Join-Path $root 'engine\ZikWall.Broker\ZikWall.Broker.csproj') -c Release -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Broker build failed.' }
$source = [IO.Path]::GetFullPath((Join-Path $root 'temp\engine\ZikWall.Engine.dll'))
$target = [IO.Path]::GetFullPath((Join-Path $OutputDirectory 'ZikWall.Engine.dll'))
if ($source -ne $target) { Copy-Item -LiteralPath $source -Destination $target -Force }
Write-Host "Built broker and Windows SDK engine in $OutputDirectory"
