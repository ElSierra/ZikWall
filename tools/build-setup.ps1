param([string]$Version, [string]$StagingRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# MSI versions have three bounded integer fields. UTC minutes within a month
# produce ordered versions without a mutable counter or a Git commit requirement.
if (-not $Version) {
    $now = [DateTime]::UtcNow
    $Version = '0.{0}.{1}' -f (($now.Year - 2026) * 12 + $now.Month), (($now.Day - 1) * 1440 + $now.Hour * 60 + $now.Minute)
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.build.' }
$parsed = [version]$Version
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) { throw 'Version exceeds MSI field limits.' }
$wix = Join-Path $root 'temp\wix\wix.exe'
if (-not (Test-Path -LiteralPath $wix)) {
    & dotnet tool install wix --tool-path (Split-Path $wix) --version 4.0.6
    if ($LASTEXITCODE -ne 0) { throw 'Installing pinned WiX 4.0.6 failed.' }
}
$toolVersion = & $wix --version
if ($LASTEXITCODE -ne 0 -or $toolVersion -notmatch '^4\.0\.6') { throw 'WiX 4.0.6 is required.' }
$uiExtension = Join-Path $root '.wix\extensions\WixToolset.UI.wixext\4.0.6\wixext4\WixToolset.UI.wixext.dll'
if (-not (Test-Path -LiteralPath $uiExtension)) {
    Push-Location $root
    try { & $wix extension add WixToolset.UI.wixext/4.0.6; if ($LASTEXITCODE -ne 0) { throw 'Installing the pinned WiX UI extension failed.' } }
    finally { Pop-Location }
}
& (Join-Path $PSScriptRoot 'generate-installer-assets.ps1')
$utilExtension = Join-Path $root '.wix\extensions\WixToolset.Util.wixext\4.0.6\wixext4\WixToolset.Util.wixext.dll'
if (-not (Test-Path -LiteralPath $utilExtension)) {
    Push-Location $root
    try { & $wix extension add WixToolset.Util.wixext/4.0.6; if ($LASTEXITCODE -ne 0) { throw 'Installing the pinned WiX utility extension failed.' } }
    finally { Pop-Location }
}
$work = Join-Path $StagingRoot ('ZikWall-msi-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$null = New-Item -ItemType Directory -Path $payload -Force
& (Join-Path $PSScriptRoot 'build-winui.ps1') -OutputDirectory $payload
# WinUI's native DLLs also need Microsoft's app-local release VC runtime.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $visualStudio) { throw 'Visual C++ redistributable files are required.' }
$crt = Get-ChildItem (Join-Path $visualStudio 'VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT') -Directory |
    Where-Object { $_.Parent.Parent.Name -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Parent.Parent.Name } -Descending | Select-Object -First 1
if (-not $crt) { throw 'Cannot locate the x64 release Visual C++ runtime.' }
Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $payload -Force
foreach ($required in @('ZikWall.exe', 'ZikWall.dll', 'ZikWall.Broker.exe', 'ZikWall.Engine.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'LICENSE.txt', 'UPSTREAM-NOTICES.md', 'Assets\ZikWall.ico', 'Assets\ZikWall-tray.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing payload: $required" }
}
# Harvest only this fresh build; never package live dev output or diagnostic data.
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -notin @('.pdb', '.lib', '.exp') } | Sort-Object FullName)
function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
function Stable-Id([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { 'I' + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())))).Replace('-', '').Substring(0, 32) }
    finally { $sha.Dispose() }
}
$xml = [Collections.Generic.List[string]]::new()
$xml.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment><DirectoryRef Id="INSTALLFOLDER">')
$directories = @{}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    $parent = Split-Path $relative -Parent
    if ($parent) { $directories[$parent] = $true }
}
# Directory chains may be repeated as DirectoryRef fragments, with stable IDs.
$allDirectories = @{}
foreach ($path in @($directories.Keys)) {
    while ($path) { $allDirectories[$path] = $true; $path = Split-Path $path -Parent }
}
$xml.Add('</DirectoryRef>')
foreach ($path in ($allDirectories.Keys | Sort-Object)) {
    $parent = Split-Path $path -Parent
    $parentId = if ($parent) { Stable-Id "dir:$parent" } else { 'INSTALLFOLDER' }
    $id = Stable-Id "dir:$path"
    $name = Escape-Xml (Split-Path $path -Leaf)
    $xml.Add("<DirectoryRef Id=`"$parentId`"><Directory Id=`"$id`" Name=`"$name`" /></DirectoryRef>")
}
$xml.Add('<ComponentGroup Id="PayloadComponents">')
$manifest = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($payload.Length + 1)
    $parent = Split-Path $relative -Parent
    $directoryId = if ($parent) { Stable-Id "dir:$parent" } else { 'INSTALLFOLDER' }
    $id = Stable-Id "file:$relative"
    $source = Escape-Xml $file.FullName
    $shortcut = if ($relative -eq 'ZikWall.exe') { '<Shortcut Id="ZikWallShortcut" Directory="ProgramMenuFolder" Name="ZikWall" Advertise="yes" WorkingDirectory="INSTALLFOLDER" Icon="ZikWall.exe" IconIndex="0" />' } else { '' }
    $service = if ($relative -eq 'ZikWall.Service.exe') {
        '<ServiceInstall Id="ZikWallServiceInstall" Name="ZikWallService" DisplayName="ZikWall Background Service" Description="Starts isolated ZikWall traffic, website-name and firewall sessions for the installed app." Type="ownProcess" Start="auto" ErrorControl="normal" Account="LocalSystem" Vital="yes" /><ServiceControl Id="ZikWallServiceControl" Name="ZikWallService" Start="install" Stop="both" Remove="uninstall" Wait="yes" />'
    } else { '' }
    $xml.Add("<Component Id=`"C$id`" Directory=`"$directoryId`" Guid=`"*`" Bitness=`"always64`"><File Id=`"$id`" Source=`"$source`" KeyPath=`"yes`">$shortcut</File>$service</Component>")
    $manifest += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$xml.Add('</ComponentGroup></Fragment></Wix>')
$harvest = Join-Path $work 'Payload.wxs'
$xml | Set-Content -LiteralPath $harvest -Encoding UTF8
$msi = Join-Path $work 'setup.msi'
& $wix build (Join-Path $root 'installer\Package.wxs') (Join-Path $root 'installer\UserInterface.wxs') $harvest -ext $uiExtension -ext $utilExtension -loc (Join-Path $root 'installer\Strings.en-us.wxl') -culture en-US -arch x64 -d "Version=$Version" -d "Payload=$payload" -d "InstallerAssets=$root\installer\Assets" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'MSI compilation or validation failed.' }
$output = Join-Path $root 'artifacts\installer'
$null = New-Item -ItemType Directory -Path $output -Force
$manifestPath = Join-Path $work 'manifest.json'
[ordered]@{ version = $Version; builtUtc = [DateTime]::UtcNow.ToString('o'); sha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash; files = $manifest } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
& (Join-Path $PSScriptRoot 'test-setup.ps1') -MsiPath $msi -ManifestPath $manifestPath -RemoveStaging
Copy-Item -LiteralPath $msi -Destination (Join-Path $output 'setup.msi') -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'manifest.json') -Force
Write-Host "Built: $(Join-Path $output 'setup.msi') (version $Version; $($files.Count) files)"
$resolvedWork = [IO.Path]::GetFullPath($work)
$stagingPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($StagingRoot))
if (-not $resolvedWork.StartsWith($stagingPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    ((Get-Item -LiteralPath $resolvedWork).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    (Get-ChildItem -LiteralPath $resolvedWork -Recurse -Force -Attributes ReparsePoint)) { throw 'Refusing unsafe MSI staging cleanup.' }
Remove-Item -LiteralPath $resolvedWork -Recurse -Force
