$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'installer\Assets'
$null = New-Item -ItemType Directory -Path $output -Force
$logo = [Drawing.Image]::FromFile((Join-Path $root 'ui\ZikWall.WinUI\Assets\ZikWall.png'))
function Color([string]$Hex) { [Drawing.ColorTranslator]::FromHtml($Hex) }
function Draw-Text($Graphics, [string]$Text, [single]$Size, [single]$X, [single]$Y, [string]$Hex, [Drawing.FontStyle]$Style = [Drawing.FontStyle]::Regular) {
    $font = [Drawing.Font]::new('Segoe UI', $Size, $Style, [Drawing.GraphicsUnit]::Pixel)
    $brush = [Drawing.SolidBrush]::new((Color $Hex))
    try { $Graphics.DrawString($Text, $font, $brush, $X, $Y) } finally { $font.Dispose(); $brush.Dispose() }
}
try {
    foreach ($kind in @('Dialog','Banner')) {
        $height = if ($kind -eq 'Dialog') { 702 } else { 132 }
        $bitmap = [Drawing.Bitmap]::new(1110, $height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $graphics.Clear([Drawing.Color]::White)
            if ($kind -eq 'Dialog') {
                $rect = [Drawing.Rectangle]::new(0, 0, 366, $height)
                $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new($rect, (Color '#07162F'), (Color '#123B70'), [single]60)
                $graphics.FillRectangle($gradient, $rect); $gradient.Dispose()
                $pen = [Drawing.Pen]::new((Color '#24558A'), [single]2)
                $graphics.SetClip($rect)
                foreach ($radius in @(160, 240, 320)) { $graphics.DrawEllipse($pen, -$radius, 510 - $radius, $radius * 2, $radius * 2) }
                $graphics.ResetClip(); $pen.Dispose()
                $graphics.DrawImage($logo, 83, 128, 200, 200)
                Draw-Text $graphics 'ZikWall' 43 66 365 '#FFFFFF' ([Drawing.FontStyle]::Bold)
                Draw-Text $graphics 'Clarity. Control.' 23 68 423 '#B8D4F5'
                Draw-Text $graphics 'WINDOWS / x64' 18 68 595 '#94B8E3'
                Draw-Text $graphics 'isaacojo.me' 24 68 626 '#FFFFFF'
            } else {
                $graphics.DrawImage($logo, 990, 24, 80, 80)
                $pen = [Drawing.Pen]::new((Color '#DDE5F0'), [single]2)
                $graphics.DrawLine($pen, 0, 131, 1110, 131); $pen.Dispose()
            }
            $bitmap.Save((Join-Path $output "$kind.bmp"), [Drawing.Imaging.ImageFormat]::Bmp)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $logo.Dispose() }
$license = [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
$escaped = $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`r", '').Replace("`n", '\par ')
$rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $escaped + '}'
[IO.File]::WriteAllText((Join-Path $output 'License.rtf'), $rtf, [Text.Encoding]::ASCII)
Write-Host 'Generated installer dialog/banner artwork and the complete GPLv3 license page.'
