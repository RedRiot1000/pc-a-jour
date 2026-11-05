# Génère src\app.ico (coche sur fond dégradé vert → cyan) — à lancer une fois avant build.ps1
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = 16, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 256.0
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 112 * $k; $o = 8 * $k; $e = 240 * $k
    $p.AddArc($o, $o, $r, $r, 180, 90); $p.AddArc($o + $e - $r, $o, $r, $r, 270, 90)
    $p.AddArc($o + $e - $r, $o + $e - $r, $r, $r, 0, 90); $p.AddArc($o, $o + $e - $r, $r, $r, 90, 90)
    $p.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(16, 185, 129)), ([System.Drawing.Color]::FromArgb(14, 165, 201))
    $g.FillPath($bg, $p)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (30 * $k)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (68 * $k), (132 * $k)),
        (New-Object System.Drawing.PointF (112 * $k), (176 * $k)),
        (New-Object System.Drawing.PointF (190 * $k), (86 * $k)))
    $g.DrawLines($pen, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($b in $pngs) { $w.Write([byte[]]$b) }
[System.IO.File]::WriteAllBytes((Join-Path $here 'src\app.ico'), $out.ToArray())
