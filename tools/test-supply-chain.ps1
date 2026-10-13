$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Get-Content (Join-Path $root 'ZikWall.vcxproj') -Raw
if ($project -match 'APP_HAVE_UPDATES') { throw 'The unverified native startup updater was re-enabled.' }
$main = Get-Content (Join-Path $root 'src\main.c') -Raw
if ($main -match '_r_update_(check|enable|addcomponent)\s*\(') { throw 'An unverified native update entry point was reintroduced.' }
Get-ChildItem (Join-Path $root '.github\workflows') -File | ForEach-Object {
    foreach ($line in Get-Content $_.FullName) {
        if ($line -match '^\s*uses:\s*(?!\./)(\S+)') {
            if ($Matches[1] -notmatch '@[a-f0-9]{40}$') { throw "An action is not pinned to a commit: $line" }
        }
    }
}
$lock = Get-Content (Join-Path $root 'ui\ZikWall.WinUI\packages.lock.json') -Raw | ConvertFrom-Json
foreach ($framework in $lock.dependencies.PSObject.Properties) {
    foreach ($package in $framework.Value.PSObject.Properties) {
        if ($package.Value.type -ne 'Project' -and (!$package.Value.resolved -or !$package.Value.contentHash)) {
            throw "Package lacks a version/content hash: $($package.Name)"
        }
    }
}
Write-Host 'PASS: native updater entry points disabled, workflow actions commit-pinned, WinUI dependency hashes recorded.'
$countries = Join-Path $root 'ui\ZikWall.WinUI\Assets\Countries'
$provenance = Get-Content (Join-Path $countries 'provenance.json') -Raw | ConvertFrom-Json
foreach ($entry in $provenance.files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $countries $entry.Name) -Algorithm SHA256).Hash -ne $entry.Value.sha256) { throw "Country asset changed: $($entry.Name)" }
}
$networks = Join-Path $root 'ui\ZikWall.WinUI\Assets\Networks'
$provenance = Get-Content (Join-Path $networks 'provenance.json') -Raw | ConvertFrom-Json
foreach ($entry in $provenance.files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $networks $entry.Name) -Algorithm SHA256).Hash -ne $entry.Value.sha256) { throw "Network asset changed: $($entry.Name)" }
}
$flags = Join-Path $root 'ui\ZikWall.WinUI\Assets\Flags'
$hashes = Get-Content (Join-Path $flags 'hashes.json') -Raw | ConvertFrom-Json
foreach ($entry in $hashes.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $flags $entry.Name) -Algorithm SHA256).Hash -ne $entry.Value) { throw "Flag asset changed: $($entry.Name)" }
}
Write-Host 'PASS: bundled country tables, network owner tables and flag artwork match recorded content hashes.'
