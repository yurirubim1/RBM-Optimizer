# Gera o ícone do RBM Optimizer (ICO multi-tamanho com entradas PNG). Uso: powershell -File build/tools/make-icon.ps1 -Out <arquivo.ico>
param([Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName System.Drawing
function New-Png([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $r = [Math]::Max(2, [int]($s * 0.22)); $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2; $w = $s - 1
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
  $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)), ([System.Drawing.Point]::new($s,$s)), ([System.Drawing.Color]::FromArgb(255,47,123,255)), ([System.Drawing.Color]::FromArgb(255,139,92,246))
  $g.FillPath($brush, $path)
  $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.58)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
  $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
  $g.DrawString('R', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.02), $s, $s), $sf)
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  return ,$ms.ToArray()
}
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { ,(New-Png $_) }
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); "ícone gerado: $Out"
