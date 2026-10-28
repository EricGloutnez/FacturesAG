# Génère l'icône Windows (app.ico, plusieurs tailles) et logo-256.png à partir du dessin de assets/logo-icone.svg.
# Le dessin est reproduit ici avec System.Drawing (mêmes coordonnées, grille 80 x 80).
# Usage : powershell -ExecutionPolicy Bypass -File assets\generer-icone.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$teal = [System.Drawing.Color]::FromArgb(31, 111, 92)
$amber = [System.Drawing.Color]::FromArgb(224, 162, 59)

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 80.0, $size / 80.0)
    $pen = New-Object System.Drawing.Pen $teal, 5; $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'

    # Feuille avec coin replié
    $sheet = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sheet.AddPolygon([System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 14, 6), (New-Object System.Drawing.PointF 48, 6),
        (New-Object System.Drawing.PointF 64, 22), (New-Object System.Drawing.PointF 64, 74), (New-Object System.Drawing.PointF 14, 74)))
    $g.FillPath([System.Drawing.Brushes]::White, $sheet); $g.DrawPath($pen, $sheet)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 48, 6), (New-Object System.Drawing.PointF 48, 22), (New-Object System.Drawing.PointF 64, 22)))
    # Lignes de texte
    $g.DrawLine($pen, 24, 32, 46, 32); $g.DrawLine($pen, 24, 43, 38, 43)
    # Horloge
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $amber), 39, 43, 34, 34)
    $white = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 3; $g.DrawEllipse($white, 39, 43, 34, 34)
    $hands = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 4.5; $hands.StartCap = 'Round'; $hands.EndCap = 'Round'; $hands.LineJoin = 'Round'
    $g.DrawLines($hands, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 56, 51), (New-Object System.Drawing.PointF 56, 60), (New-Object System.Drawing.PointF 63, 64)))
    $g.Dispose()
    return $bmp
}

$root = Split-Path $PSScriptRoot -Parent
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-LogoBitmap $s; $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); , $ms.ToArray()
}
(New-LogoBitmap 256).Save((Join-Path $PSScriptRoot 'logo-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Fichier .ico : en-tête, répertoire d'images, puis les images PNG (format accepté depuis Windows Vista).
$ico = Join-Path $root 'src\FacturesAG\Assets\app.ico'
$fs = [System.IO.File]::Create($ico); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"Icône : $ico ($((Get-Item $ico).Length) octets, $($sizes.Count) tailles)"
