# 스프라이트시트의 한 셀을 잘라 다중 크기 .ico 를 만든다.
# 사용: powershell -File tools\make-icon.ps1 [-SheetPath <png>] [-OutPath <ico>] [-Col 0] [-Row 0]
# 에셋 라이선스(재배포 금지) 때문에 결과 .ico 는 저장소에 넣지 않는다. clone 후 이 스크립트를 한 번 돌린다.
# 64px 이하는 비압축 DIB 엔트리(모든 GDI+ 경로에서 안전), 128/256 은 PNG 엔트리(Vista 이후 지원)로 넣는다.
param(
  [string]$SheetPath = "$PSScriptRoot\..\src\Catcretary\Assets\pet\Cat Sprite Sheet.png",
  [string]$OutPath   = "$PSScriptRoot\..\src\Catcretary\Assets\pet\app.ico",
  [int]$Col = 0,
  [int]$Row = 0,
  [int]$Cell = 32
)

Add-Type -AssemblyName System.Drawing

$sheetFull = (Resolve-Path $SheetPath).Path
$outFull = [System.IO.Path]::GetFullPath($OutPath)

$sheetBmp = [System.Drawing.Bitmap]::FromFile($sheetFull)
$cellRect = New-Object System.Drawing.Rectangle -ArgumentList ($Col * $Cell), ($Row * $Cell), $Cell, $Cell
$cellBmp = $sheetBmp.Clone($cellRect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sheetBmp.Dispose()

# 불투명 픽셀의 경계를 찾아 정사각형으로 잘라낸다. 여백이 큰 셀을 그대로 쓰면 아이콘이 작아 보인다.
$minX = $Cell; $minY = $Cell; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $Cell; $y++) {
  for ($x = 0; $x -lt $Cell; $x++) {
    if ($cellBmp.GetPixel($x, $y).A -gt 0) {
      if ($x -lt $minX) { $minX = $x }
      if ($x -gt $maxX) { $maxX = $x }
      if ($y -lt $minY) { $minY = $y }
      if ($y -gt $maxY) { $maxY = $y }
    }
  }
}
$w = $maxX - $minX + 1; $h = $maxY - $minY + 1
$side = [math]::Min($Cell, [math]::Max($w, $h) + 2)
$cx = [int](($minX + $maxX) / 2); $cy = [int](($minY + $maxY) / 2)
$sx = [math]::Max(0, [math]::Min($Cell - $side, $cx - [int]($side / 2)))
$sy = [math]::Max(0, [math]::Min($Cell - $side, $cy - [int]($side / 2)))

$square = New-Object System.Drawing.Bitmap -ArgumentList $side, $side
$gs = [System.Drawing.Graphics]::FromImage($square)
$dstRect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $side, $side
$srcRect = New-Object System.Drawing.Rectangle -ArgumentList $sx, $sy, $side, $side
$gs.DrawImage($cellBmp, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$gs.Dispose(); $cellBmp.Dispose()

function New-ScaledBitmap([System.Drawing.Bitmap]$src, [int]$s) {
  $bmp = New-Object System.Drawing.Bitmap -ArgumentList $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g.Clear([System.Drawing.Color]::Transparent)
  $target = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $s, $s
  $g.DrawImage($src, $target)
  $g.Dispose()
  return $bmp
}

# 32bpp BGRA DIB + 1bpp AND 마스크. BITMAPINFOHEADER 의 높이는 XOR+AND 라서 두 배로 적는다.
function ConvertTo-DibEntry([System.Drawing.Bitmap]$bmp) {
  $s = $bmp.Width
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter -ArgumentList $ms
  $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
  $maskStride = [int][math]::Ceiling($s / 32.0) * 4
  $bw.Write([uint32]($s * $s * 4 + $maskStride * $s))
  $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
  for ($y = $s - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $s; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  for ($y = $s - 1; $y -ge 0; $y--) {
    $rowBytes = New-Object byte[] $maskStride
    for ($x = 0; $x -lt $s; $x++) {
      # [int] 캐스트는 반올림이라 인덱스가 넘친다. 내림을 써야 한다.
      $i = [int][math]::Floor($x / 8)
      if ($bmp.GetPixel($x, $y).A -eq 0) { $rowBytes[$i] = $rowBytes[$i] -bor (0x80 -shr ($x % 8)) }
    }
    $bw.Write($rowBytes)
  }
  $bw.Flush()
  $bytes = $ms.ToArray()
  $bw.Dispose(); $ms.Dispose()
  return $bytes
}

function ConvertTo-PngEntry([System.Drawing.Bitmap]$bmp) {
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bytes = $ms.ToArray(); $ms.Dispose()
  return $bytes
}

$entries = New-Object System.Collections.ArrayList
foreach ($s in 16, 24, 32, 48, 64) {
  $bmp = New-ScaledBitmap $square $s
  [void]$entries.Add(@{ Size = $s; Data = (ConvertTo-DibEntry $bmp) })
  $bmp.Dispose()
}
foreach ($s in 128, 256) {
  $bmp = New-ScaledBitmap $square $s
  [void]$entries.Add(@{ Size = $s; Data = (ConvertTo-PngEntry $bmp) })
  $bmp.Dispose()
}
$square.Dispose()

# ICO 컨테이너: 헤더(6) + 엔트리(16×n) + 이미지 데이터.
$fs = [System.IO.File]::Create($outFull)
$bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $s = [int]$e.Size; $len = [int]$e.Data.Length
  $dim = 0; if ($s -lt 256) { $dim = $s }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
  $offset += $len
}
foreach ($e in $entries) { $bw.Write([byte[]]$e.Data) }
$bw.Dispose(); $fs.Dispose()

Write-Output ("wrote $outFull (" + $entries.Count + " sizes, crop " + $side + "px from cell " + $Col + "," + $Row + ")")
