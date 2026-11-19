# Generates app.ico (16/32/48 BMP entries + 256 PNG entry) with GDI+
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)

  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [float]([Math]::Round($s * 0.44))
  $path.AddArc(0, 0, $d, $d, 180, 90)
  $path.AddArc(($s - $d), 0, $d, $d, 270, 90)
  $path.AddArc(($s - $d), ($s - $d), $d, $d, 0, 90)
  $path.AddArc(0, ($s - $d), $d, $d, 90, 90)
  $path.CloseFigure()
  $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 79, 110, 247))
  $g.FillPath($bg, $path)
  $bg.Dispose()
  $path.Dispose()

  $pad = $s * 0.20
  $bw2 = $s * 0.15
  $gap = ($s - 2 * $pad - 3 * $bw2) / 2
  $defs = @(@(0.18, 0.46), @(0.18, 0.64), @(0.18, 0.30))
  for ($i = 0; $i -lt 3; $i++) {
    $x = $pad + $i * ($bw2 + $gap)
    $y0 = $s * $defs[$i][0]
    $h = $s * $defs[$i][1]
    $dd = [Math]::Min([Math]::Max(2.0, $bw2 * 0.56), [Math]::Min($bw2, $h))
    $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bar.AddArc([float]$x, [float]$y0, [float]$dd, [float]$dd, 180, 90)
    $bar.AddArc([float]($x + $bw2 - $dd), [float]$y0, [float]$dd, [float]$dd, 270, 90)
    $bar.AddArc([float]($x + $bw2 - $dd), [float]($y0 + $h - $dd), [float]$dd, [float]$dd, 0, 90)
    $bar.AddArc([float]$x, [float]($y0 + $h - $dd), [float]$dd, [float]$dd, 90, 90)
    $bar.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $bar)
    $bar.Dispose()
  }
  $g.Dispose()
  return $bmp
}

function Convert-ToIcoBmp([System.Drawing.Bitmap]$bmp) {
  $s = $bmp.Width
  $maskRow = [int][Math]::Ceiling($s / 8.0)
  $maskRow = [int]([Math]::Ceiling($maskRow / 4.0) * 4)
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([uint32]40)
  $bw.Write([int32]$s)
  $bw.Write([int32]($s * 2))
  $bw.Write([uint16]1)
  $bw.Write([uint16]32)
  $bw.Write([uint32]0)
  $bw.Write([uint32](($s * $s * 4) + ($s * $maskRow)))
  $bw.Write([int32]0)
  $bw.Write([int32]0)
  $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $row = New-Object byte[] ($s * 4)
  for ($y = $s - 1; $y -ge 0; $y--) {
    [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $s * 4)
    $bw.Write($row)
  }
  $bmp.UnlockBits($data)
  $zeros = New-Object byte[] ($s * $maskRow)
  $bw.Write($zeros)
  $bw.Flush()
  return $ms
}

$sizes = @(16, 32, 48, 256)
$bitmaps = @()
$blobs = @()
foreach ($sz in $sizes) { $bitmaps += , (New-IconBitmap $sz) }
try {
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    if ($sizes[$i] -ge 256) {
      $ms = New-Object System.IO.MemoryStream
      $bitmaps[$i].Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
      $blobs += , $ms
    } else {
      $blobs += , (Convert-ToIcoBmp $bitmaps[$i])
    }
  }
  $out = New-Object System.IO.MemoryStream
  $bwOut = New-Object System.IO.BinaryWriter($out)
  $bwOut.Write([uint16]0)
  $bwOut.Write([uint16]1)
  $bwOut.Write([uint16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $wByte = $sz
    if ($sz -ge 256) { $wByte = 0 }
    $bytes = $blobs[$i].ToArray()
    $bwOut.Write([byte]$wByte)
    $bwOut.Write([byte]$wByte)
    $bwOut.Write([byte]0)
    $bwOut.Write([byte]0)
    $bwOut.Write([uint16]1)
    $bwOut.Write([uint16]32)
    $bwOut.Write([uint32]$bytes.Length)
    $bwOut.Write([uint32]$offset)
    $offset += $bytes.Length
  }
  foreach ($b in $blobs) { $bwOut.Write($b.ToArray()) }
  $bwOut.Flush()
  $dest = Join-Path $PSScriptRoot 'app.ico'
  [System.IO.File]::WriteAllBytes($dest, $out.ToArray())
  Write-Host ("ICON OK: " + $dest + " (" + $out.Length + " bytes)")
} finally {
  foreach ($bm in $bitmaps) { $bm.Dispose() }
}
