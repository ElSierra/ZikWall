param(
    [string]$MsiPath = (Join-Path $PSScriptRoot '..\artifacts\installer\setup.msi'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\artifacts\installer\manifest.json'),
    [string]$InstallDirectory,
    [switch]$RemoveStaging
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'MSI hash mismatch.' }
$work = Join-Path $root ('temp\msi-check-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work -Force
$source = Join-Path $work 'Package.wxs'
$extracted = Join-Path $work 'extracted'
& (Join-Path $root 'temp\wix\wix.exe') msi decompile $MsiPath -o $source -x $extracted
if ($LASTEXITCODE -ne 0) { throw 'MSI extraction failed.' }
[xml]$xml = Get-Content -LiteralPath $source -Raw
$ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
$ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
$package = $xml.SelectSingleNode('/w:Wix/w:Package', $ns)
if ($package.Version -ne $manifest.version) { throw 'MSI version mismatch.' }
if ($package.UpgradeCode.Trim('{}') -ne 'A73F13E3-53D4-4D63-96AD-85460794860F') { throw 'Upgrade identity changed.' }
# Read the MSI tables: WiX 4 decompilation loses IgnoreLanguage and can
# misrepresent the RemoveExistingProducts schedule in reconstructed XML.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase([IO.Path]::GetFullPath($MsiPath), 0)
try {
    $view = $database.OpenView('SELECT `Language`, `ActionProperty` FROM `Upgrade`')
    $view.Execute(); $foundUpgrade = $false
    while ($row = $view.Fetch()) {
        if ($row.StringData(2) -eq 'WIX_UPGRADE_DETECTED') {
            $foundUpgrade = $true
            if ($row.StringData(1)) { throw 'MSI must upgrade older language-neutral packages.' }
        }
    }
    $view.Close()
    if (-not $foundUpgrade) { throw 'Missing upgrade detection.' }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    $view = $database.OpenView('SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')
    $view.Execute(); $sequence = @{}
    while ($row = $view.Fetch()) { $sequence[$row.StringData(1)] = $row.IntegerData(2) }
    $view.Close()
    if ($sequence['RemoveExistingProducts'] -le $sequence['InstallInitialize'] -or $sequence['RemoveExistingProducts'] -ge $sequence['InstallFiles']) { throw 'Old products must be removed transactionally before files are installed.' }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ALLUSERS''')
    $view.Execute(); $row = $view.Fetch()
    if (-not $row -or $row.StringData(1) -ne '1') { throw 'Setup must install the Programs shortcut for all users.' }
    $view.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    $view = $database.OpenView('SELECT `Component_`, `FileName` FROM `File`')
    $view.Execute(); $appComponent = $null
    while ($row = $view.Fetch()) {
        if (($row.StringData(2) -split '\|')[-1] -eq 'ZikWall.exe') { $appComponent = $row.StringData(1) }
    }
    $view.Close()
    if (-not $appComponent) { throw 'Missing shortcut executable component.' }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    $view = $database.OpenView('SELECT `Directory_`, `Component_`, `Target`, `Icon_`, `WkDir`, `Name` FROM `Shortcut` WHERE `Shortcut` = ''ZikWallShortcut''')
    $view.Execute(); $row = $view.Fetch()
    if (-not $row -or $row.StringData(1) -ne 'ProgramMenuFolder' -or $row.StringData(2) -ne $appComponent -or
        $row.StringData(3) -ne 'Main' -or $row.StringData(4) -ne 'ZikWallIcon' -or $row.StringData(5) -ne 'INSTALLFOLDER' -or
        ($row.StringData(6) -split '\|')[-1] -ne 'ZikWall') { throw 'Programs shortcut must launch the installed ZikWall executable with its app icon.' }
    $view.Close()
} finally {
    if ($view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
if (-not $xml.SelectSingleNode('//w:StandardDirectory[@Id="ProgramFiles64Folder"]/w:Directory[@Id="INSTALLFOLDER"]', $ns)) { throw 'Missing Program Files install location.' }
if ($xml.SelectSingleNode('//w:RemoveFile | //w:RemoveFolder | //w:RemoveRegistryKey | //w:RemoveRegistryValue', $ns)) { throw 'Setup must not add cleanup actions that could erase user settings.' }
if ($xml.SelectSingleNode('//w:StandardDirectory[@Id="LocalAppDataFolder" or @Id="AppDataFolder"]', $ns)) { throw 'User settings must remain outside MSI component ownership.' }
if (-not $xml.SelectSingleNode('//w:Shortcut[@Name="ZikWall"]', $ns)) { throw 'Missing Start menu shortcut.' }
if ($package.Manufacturer -ne 'isaacojo.me') { throw 'Missing publisher branding.' }
if ($xml.SelectSingleNode('//w:Property[@Id="ARPURLINFOABOUT"]', $ns).Value -ne 'https://isaacojo.me') { throw 'Missing publisher website.' }
foreach ($dialog in @('ZikWelcome','LicenseAgreementDlg','InstallDirDlg','VerifyReadyDlg','ProgressDlg','ZikFinish','MaintenanceTypeDlg')) {
    if (-not $xml.SelectSingleNode("//w:Dialog[@Id='$dialog']", $ns)) { throw "Missing setup page: $dialog" }
}
foreach ($dialog in @('ZikWelcome','ZikFinish')) {
    $link = $xml.SelectSingleNode("//w:Dialog[@Id='$dialog']/w:Control[@Id='Site']", $ns)
    if (-not $link -or $link.Type -ne 'Hyperlink' -or $link.Text -notmatch 'https://isaacojo\.me') { throw "Missing website link on $dialog." }
}
$expected = @{}
foreach ($entry in $manifest.files) { $expected[$entry.path] = $entry.sha256 }
$files = @($xml.SelectNodes('//w:File', $ns))
if ($files.Count -ne $expected.Count) { throw 'MSI file count mismatch.' }
foreach ($file in $files) {
    $relative = $file.Name
    $node = $file.ParentNode
    while ($node -and $node.Id -ne 'INSTALLFOLDER') {
        if ($node.LocalName -eq 'Directory') { $relative = $node.Name + '\' + $relative }
        $node = $node.ParentNode
    }
    if (-not $node -or -not $expected.ContainsKey($relative)) { throw "Unexpected MSI file: $relative" }
    if ($relative -match '(?i)(selftest|self-test|\.pdb$|\.log$|\.etl$)') { throw "Development artifact in MSI: $relative" }
    $filePath = Join-Path $extracted ('File\' + $file.Id)
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $expected[$relative]) { throw "Cabinet payload mismatch: $relative" }
    if ($InstallDirectory -and (Get-FileHash -LiteralPath (Join-Path $InstallDirectory $relative) -Algorithm SHA256).Hash -ne $expected[$relative]) { throw "Installed file mismatch: $relative" }
    $expected.Remove($relative)
}
if ($expected.Count) { throw 'Manifest has files missing from the MSI.' }
Write-Host "PASS: MSI version $($manifest.version), upgrade identity, Program Files location, shortcut and $($files.Count) extracted file hashes."
Write-Host 'PASS: all-users Start Menu\Programs\ZikWall.lnk targets the app component and uses the ZikWall icon.'
Write-Host 'PASS: branded setup/maintenance dialogs and isaacojo.me publisher links.'
Write-Host 'PASS: no per-user settings components or cleanup actions; user settings stay outside MSI ownership.'
if ($InstallDirectory) {
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'ZikWall.lnk'
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw "Installed Start menu shortcut is missing: $shortcut" }
    $installedProducts = @(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -eq 'ZikWall' })
    if ($installedProducts.Count -ne 1 -or $installedProducts[0].DisplayVersion -ne $manifest.version) { throw 'Expected exactly one installed ZikWall MSI matching this version.' }
    Write-Host "PASS: exactly one installed product; every installed file matches the MSI in $InstallDirectory."
}
if ($RemoveStaging) {
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath((Join-Path $root 'temp'))
    if (-not $resolvedWork.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolvedWork).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $resolvedWork -Recurse -Force -Attributes ReparsePoint)) { throw 'Refusing unsafe verification staging cleanup.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
