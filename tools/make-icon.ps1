# Renders Pitstop's app icon (dark rounded tile, cyan "P") at 16-256 px and packs it into one .ico file.
# Frames under 256 px are stored as 32-bit bitmaps and the 256 px frame as PNG: the standard layout that
# every Windows component (and System.Drawing) can read.
# Usage (from the repo root, in PowerShell): .\tools\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Pitstop.App\pitstop.ico'))

Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$bg = [System.Drawing.Color]::FromArgb(0x0b, 0x0f, 0x14)
$accent = [System.Drawing.Color]::FromArgb(0x4c, 0xc9, 0xf0)

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $pad = [Math]::Max(1, $s * 0.04); $r = $s * 0.30; $w = $s - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $r, $r, 180, 90); $path.AddArc($pad + $w - $r, $pad, $r, $r, 270, 90)
    $path.AddArc($pad + $w - $r, $pad + $w - $r, $r, $r, 0, 90); $path.AddArc($pad, $pad + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $path)
    $g.DrawPath((New-Object System.Drawing.Pen $accent, ([Math]::Max(1.2, $s * 0.075))), $path)
    $font = New-Object System.Drawing.Font 'Segoe UI', ($s * 0.47), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $size = $g.MeasureString('P', $font)
    $g.DrawString('P', $font, (New-Object System.Drawing.SolidBrush $accent), (($s - $size.Width) / 2 + $s * 0.02), (($s - $size.Height) / 2))
    $g.Dispose()
    $bmp
}

$frames = foreach ($s in $sizes) {
    $bmp = Render $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    else {
        # BITMAPINFOHEADER (height doubled to include the AND mask), bottom-up BGRA rows, then a 1-bit AND mask.
        $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2)); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]0); $bw.Write([UInt32]($s * $s * 4)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
        , $ms.ToArray()
    }
    $bmp.Dispose()
}

# ICO container: 6-byte header, a 16-byte directory entry per frame, then the frame data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Out), $out.ToArray())
"Wrote $Out ($($out.Length) bytes, sizes: $($sizes -join ', '))"
