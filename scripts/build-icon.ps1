<#
    Gera Curupira.ico multi-resolucao (16..256) desenhando o escudo com
    gradiente verde-mata -> laranja-fogo e a PEGADA INVERTIDA via GDI+.
    Nao requer ImageMagick.
#>
param(
    [string]$OutPath = "$(Split-Path $PSScriptRoot -Parent)\src\Curupira\Assets\Curupira.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$bitmaps = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 160.0
    $rect = [System.Drawing.RectangleF]::new(0, 0, $size, $size)

    # ---- Escudo ----
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shield.AddPolygon(@(
        [System.Drawing.PointF]::new(80*$s,   6*$s),
        [System.Drawing.PointF]::new(150*$s, 30*$s),
        [System.Drawing.PointF]::new(145*$s, 96*$s),
        [System.Drawing.PointF]::new(120*$s,130*$s),
        [System.Drawing.PointF]::new(80*$s, 154*$s),
        [System.Drawing.PointF]::new(40*$s, 130*$s),
        [System.Drawing.PointF]::new(15*$s,  96*$s),
        [System.Drawing.PointF]::new(10*$s,  30*$s)
    ))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 52, 211, 153),
        [System.Drawing.Color]::FromArgb(255, 251, 146, 60),
        45.0)
    $g.FillPath($brush, $shield)
    $brush.Dispose()
    $shield.Dispose()

    # ---- Pegada invertida (dedos para BAIXO) ----
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 10, 20, 16))

    # calcanhar (topo)
    $g.FillEllipse($ink, (64*$s), (27*$s), (32*$s), (38*$s))

    # planta / metatarso
    $sole = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sole.AddPolygon(@(
        [System.Drawing.PointF]::new(63*$s,  80*$s),
        [System.Drawing.PointF]::new(97*$s,  80*$s),
        [System.Drawing.PointF]::new(102*$s,108*$s),
        [System.Drawing.PointF]::new(93*$s, 121*$s),
        [System.Drawing.PointF]::new(67*$s, 121*$s),
        [System.Drawing.PointF]::new(58*$s, 108*$s)
    ))
    $g.FillPath($ink, $sole)
    $sole.Dispose()

    # dedos (abaixo da planta) - so em tamanhos onde ficam legiveis
    if ($size -ge 32) {
        $toes = @(
            @(59, 133, 6.4), @(70, 137, 5.6), @(80, 138, 5.2),
            @(90, 137, 4.8), @(100, 133, 4.2)
        )
        foreach ($t in $toes) {
            $cx = $t[0]*$s; $cy = $t[1]*$s; $r = $t[2]*$s
            $g.FillEllipse($ink, ($cx-$r), ($cy-$r), ($r*2), ($r*2))
        }
    }

    $ink.Dispose()
    $g.Dispose()
    $bitmaps += $bmp
}

# ---- Escreve o .ico (ICONDIR + entradas + PNGs) ----
$outDir = Split-Path $OutPath -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$fs = [System.IO.File]::Create($OutPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$bitmaps.Count)

$pngData = @()
$offset = 6 + 16 * $bitmaps.Count

foreach ($bmp in $bitmaps) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $pngData += ,$bytes

    $wPx = if ($bmp.Width  -ge 256) { 0 } else { [byte]$bmp.Width  }
    $hPx = if ($bmp.Height -ge 256) { 0 } else { [byte]$bmp.Height }

    $bw.Write([byte]$wPx); $bw.Write([byte]$hPx)
    $bw.Write([byte]0);    $bw.Write([byte]0)
    $bw.Write([UInt16]1);  $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $pngData) { $bw.Write($bytes) }

$bw.Flush(); $fs.Close()
foreach ($bmp in $bitmaps) { $bmp.Dispose() }

Write-Host "Gerado: $OutPath" -ForegroundColor Green
Write-Host ("Tamanho: {0:N1} KB" -f ((Get-Item $OutPath).Length / 1KB)) -ForegroundColor Gray
Write-Host "Resolucoes: $($sizes -join ', ')" -ForegroundColor Cyan
