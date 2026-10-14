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
        $row.StringData(3) -ne 'Main' -or $row.StringData(4) -ne 'ZikWall.exe' -or $row.StringData(5) -ne 'INSTALLFOLDER' -or
        ($row.StringData(6) -split '\|')[-1] -ne 'ZikWall') { throw 'Programs shortcut must launch the installed ZikWall executable with its app icon.' }
    $view.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    $view = $database.OpenView('SELECT `Target`, `Attributes`, `Timeout`, `TerminateExitCode`, `Sequence` FROM `Wix4CloseApplication`')
    $view.Execute(); $closeTargets = @{}
    while ($row = $view.Fetch()) {
        $closeTargets[$row.StringData(1)] = @($row.IntegerData(2), $row.IntegerData(3), $row.IntegerData(4), $row.IntegerData(5))
    }
    $view.Close()
    foreach ($target in @('ZikWall.exe', 'ZikWall.Broker.exe')) {
        $close = $closeTargets[$target]
        if (-not $close -or ($close[0] -band 37) -ne 37 -or ($close[0] -band 2) -ne 0 -or $close[1] -ne 5000 -or $close[2] -ne 0) {
            throw "MSI must close and terminate remaining $target processes with an elevated fallback."
        }
    }
    if ($closeTargets.Count -ne 2 -or $closeTargets['ZikWall.exe'][3] -ge $closeTargets['ZikWall.Broker.exe'][3]) { throw 'MSI must close only ZikWall and its broker, in that order.' }
} finally {
    if ($view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
if (-not $xml.SelectSingleNode('//w:StandardDirectory[@Id="ProgramFiles64Folder"]/w:Directory[@Id="INSTALLFOLDER"]', $ns)) { throw 'Missing Program Files install location.' }
$service = $xml.SelectSingleNode('//w:ServiceInstall[@Name="ZikWallService"]', $ns)
if (-not $service -or $service.Type -ne 'ownProcess' -or $service.Start -ne 'auto' -or $service.Account -ne 'LocalSystem' -or
    $service.ParentNode.SelectSingleNode('w:File', $ns).Name -ne 'ZikWall.Service.exe') { throw 'Missing automatic LocalSystem service bound to its executable component.' }
$control = $service.ParentNode.SelectSingleNode('w:ServiceControl[@Name="ZikWallService"]', $ns)
if (-not $control -or $control.Start -ne 'install' -or $control.Stop -ne 'both' -or $control.Remove -ne 'uninstall' -or $control.Wait -ne 'yes') { throw 'Service install/upgrade/uninstall lifecycle is incomplete.' }
if (-not $xml.SelectSingleNode('//w:CustomAction[@Id="RequireSecureServiceDirectory"]', $ns)) { throw 'Service installation must reject an unsafe custom directory.' }
Write-Host 'PASS: automatic service, transactional start/stop/removal and required Program Files location.'
if ($xml.SelectSingleNode('//w:RemoveFile | //w:RemoveFolder | //w:RemoveRegistryKey | //w:RemoveRegistryValue', $ns)) { throw 'Setup must not add cleanup actions that could erase user settings.' }
if ($xml.SelectSingleNode('//w:StandardDirectory[@Id="LocalAppDataFolder" or @Id="AppDataFolder"]', $ns)) { throw 'User settings must remain outside MSI component ownership.' }
if (-not $xml.SelectSingleNode('//w:Shortcut[@Name="ZikWall"]', $ns)) { throw 'Missing Start menu shortcut.' }
$icon = $xml.SelectSingleNode('//w:Icon[@Id="ZikWall.exe"]', $ns)
if (-not $icon) { throw 'Shortcut icon cache filename must retain the executable extension.' }
$iconFile = Join-Path $extracted 'Icon\ZikWall.exe'
$expectedExecutable = $manifest.files | Where-Object path -eq 'ZikWall.exe'
if ((Get-FileHash -LiteralPath $iconFile -Algorithm SHA256).Hash -ne $expectedExecutable.sha256) { throw 'Shortcut icon must embed the executable containing the app icon.' }
if (-not ('ZikWallSetupShellIcon' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ZikWallSetupShellIcon {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct Info {
        public IntPtr hIcon; public int iIcon; public uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string display;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string type;
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, out Info info, uint size, uint flags);
    public static int Index(string path) {
        Info info;
        if (SHGetFileInfo(path, 0, out info, (uint)Marshal.SizeOf<Info>(), 0x4000) == IntPtr.Zero)
            throw new InvalidOperationException("Windows could not read the shell icon: " + path);
        return info.iIcon;
    }
}
'@
}
$genericFile = Join-Path $work 'generic.unknown-zikwall-icon-test'
Set-Content -LiteralPath $genericFile -Value ''
$shell = New-Object -ComObject WScript.Shell
try {
    $linkPath = Join-Path $work 'ZikWall.lnk'
    $link = $shell.CreateShortcut($linkPath)
    $link.TargetPath = $iconFile
    $link.IconLocation = "$iconFile,0"
    $link.Save()
    if ([ZikWallSetupShellIcon]::Index($linkPath) -eq [ZikWallSetupShellIcon]::Index($genericFile)) { throw 'Windows displays the packaged shortcut icon as a generic document.' }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
Write-Host 'PASS: Windows Shell resolves the packaged shortcut icon instead of a generic document.'
$launch = $xml.SelectSingleNode('//w:CustomAction[@Id="LaunchZikWall"]', $ns)
if (-not $launch -or $launch.DllEntry -ne 'WixShellExec') { throw 'Missing post-install app launch action.' }
$finishLaunch = $xml.SelectSingleNode('//w:Publish[@Dialog="ZikFinish" and @Control="Finish" and @Event="DoAction" and @Value="LaunchZikWall"] | //w:Dialog[@Id="ZikFinish"]/w:Control[@Id="Finish"]/w:Publish[@Event="DoAction" and @Value="LaunchZikWall"]', $ns)
if (-not $finishLaunch -or $finishLaunch.Condition -notmatch 'REMOVE' -or $finishLaunch.Condition -notmatch 'ReplacedInUseFiles') { throw 'MSI must launch on Finish while excluding removal and pending file replacement.' }
if ($package.Manufacturer -ne 'isaacojo.me') { throw 'Missing publisher branding.' }
if ($xml.SelectSingleNode('//w:Property[@Id="ARPURLINFOABOUT"]', $ns).Value -ne 'https://isaacojo.me') { throw 'Missing publisher website.' }
foreach ($dialog in @('ZikWelcome','LicenseAgreementDlg','VerifyReadyDlg','ProgressDlg','ZikFinish','MaintenanceTypeDlg')) {
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
    $installedService = Get-CimInstance Win32_Service -Filter "Name='ZikWallService'"
    if (-not $installedService -or $installedService.State -ne 'Running' -or $installedService.StartMode -ne 'Auto' -or $installedService.StartName -ne 'LocalSystem' -or
        $installedService.PathName.Trim('"') -ne (Join-Path $InstallDirectory 'ZikWall.Service.exe')) { throw 'Installed background service is missing, stopped or misconfigured.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'ZikWall.lnk'
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw "Installed Start menu shortcut is missing: $shortcut" }
    if ([ZikWallSetupShellIcon]::Index($shortcut) -eq [ZikWallSetupShellIcon]::Index($genericFile)) { throw 'Installed shortcut still displays a generic document icon.' }
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
