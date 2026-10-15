param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$payload = [IO.Path]::GetFullPath($Directory)
foreach ($kind in @('Countries', 'Networks')) {
    $folder = Join-Path $payload "Assets\$kind"
    $provenance = Get-Content (Join-Path $folder 'provenance.json') -Raw | ConvertFrom-Json
    foreach ($entry in $provenance.files.PSObject.Properties) {
        $file = Join-Path $folder $entry.Name
        if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value.sha256) {
            throw "Missing or corrupt packaged location asset: $kind/$($entry.Name)"
        }
    }
}
foreach ($name in @('ZikWall.ico', 'EarthRelief.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload "Assets\$name"))) { throw "Missing map/tray asset: $name" }
}
Write-Host 'PASS: packaged country/network tables match their provenance; map and color tray assets present.'
