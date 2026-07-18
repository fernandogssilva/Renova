<#
    Gera Wintal.ico multi-resolução (16/24/32/48/64/128/256) desenhando o
    escudo + pulso vital via GDI+ (System.Drawing). Não requer ImageMagick.
#>

param(
    [string]$OutPath = "$(Split-Path $PSScriptRoot -Parent)\src\SysMaintenanceHub\Assets\Wintal.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$bitmaps = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Escala normalizada (icon original 160x160)
    $s = $size / 160.0

    # Shield gradient (aproximado usando LinearGradientBrush)
    $shieldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shieldPath.AddPolygon(@(
        [System.Drawing.PointF]::new(80*$s,   8*$s),
        [System.Drawing.PointF]::new(152*$s, 32*$s),
        [System.Drawing.PointF]::new(147*$s, 100*$s),
        [System.Drawing.PointF]::new(120*$s, 138*$s),
        [System.Drawing.PointF]::new(80*$s,  152*$s),
        [System.Drawing.PointF]::new(40*$s,  138*$s),
        [System.Drawing.PointF]::new(13*$s,  100*$s),
        [System.Drawing.PointF]::new(8*$s,   32*$s)
    ))
    $rect = [System.Drawing.RectangleF]::new(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 56, 189, 248),   # #38BDF8
        [System.Drawing.Color]::FromArgb(255, 124, 58, 237),   # #7C3AED
        45.0)
    $g.FillPath($brush, $shieldPath)
    $brush.Dispose()

    # Contorno preto sutil
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 11, 18, 32), [Math]::Max(1, 3*$s))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $shieldPath)
    $pen.Dispose()

    # Pulso vital em cima do escudo (gradiente verde -> azul -> roxo)
    $pulse = @(
        [System.Drawing.PointF]::new(24*$s,  88*$s),
        [System.Drawing.PointF]::new(48*$s,  88*$s),
        [System.Drawing.PointF]::new(58*$s,  58*$s),
        [System.Drawing.PointF]::new(72*$s, 120*$s),
        [System.Drawing.PointF]::new(86*$s,  48*$s),
        [System.Drawing.PointF]::new(100*$s,120*$s),
        [System.Drawing.PointF]::new(114*$s, 80*$s),
        [System.Drawing.PointF]::new(140*$s, 80*$s)
    )
    $pulseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 34, 197, 94),   # #22C55E
        [System.Drawing.Color]::FromArgb(255, 129, 140, 248), # #818CF8
        0.0)
    $pulsePen = New-Object System.Drawing.Pen($pulseBrush, [Math]::Max(1.2, 6*$s))
    $pulsePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pulsePen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pulsePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pulsePen, $pulse)
    $pulsePen.Dispose()
    $pulseBrush.Dispose()

    $g.Dispose()
    $shieldPath.Dispose()
    $bitmaps += $bmp
}

# Escreve o .ico multi-resolução manualmente (formato ICONDIR + ICONDIRENTRY + BMPs PNG)
$outDir = Split-Path $OutPath -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$fs = [System.IO.File]::Create($OutPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([UInt16]0)                # reserved
$bw.Write([UInt16]1)                # type = 1 (icon)
$bw.Write([UInt16]$bitmaps.Count)  # count

# Cada imagem é embutida como PNG (formato válido em .ico para >=Vista)
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

    $bw.Write([byte]$wPx)
    $bw.Write([byte]$hPx)
    $bw.Write([byte]0)   # colors
    $bw.Write([byte]0)   # reserved
    $bw.Write([UInt16]1) # planes
    $bw.Write([UInt16]32) # bpp
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}

foreach ($bytes in $pngData) { $bw.Write($bytes) }

$bw.Flush()
$fs.Close()

foreach ($bmp in $bitmaps) { $bmp.Dispose() }

$sha = (Get-FileHash -Algorithm SHA256 -Path $OutPath).Hash
Write-Host "Gerado: $OutPath" -ForegroundColor Green
Write-Host ("Tamanho: {0:N1} KB" -f ((Get-Item $OutPath).Length / 1KB)) -ForegroundColor Gray
Write-Host "SHA-256: $sha" -ForegroundColor DarkGray
Write-Host "Resolucoes: $($sizes -join ', ')" -ForegroundColor Cyan
