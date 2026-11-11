# claude-color.svg 위에 초록색 "RC" 를 얹어 app.ico (여러 크기 포함) 를 만든다.
#   powershell -ExecutionPolicy Bypass -File icon\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$svg = [xml](Get-Content (Join-Path $here 'claude-color.svg') -Raw)
$logo = [System.Windows.Media.Geometry]::Parse($svg.svg.path.d)
$logoBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($svg.svg.path.fill))

$green = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x0B, 0xA3, 0x3C))
$white = [System.Windows.Media.Brushes]::White
$typeface = New-Object System.Windows.Media.Typeface (
    (New-Object System.Windows.Media.FontFamily 'Arial Black'),
    [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Black, [System.Windows.FontStretches]::Normal)

function Render([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    # SVG 는 24x24 좌표계 → 아이콘 크기로 확대
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 24.0), ($size / 24.0)))
    $dc.DrawGeometry($logoBrush, $null, $logo)

    # "RC": 아래쪽에 가로로 꽉 차게, 흰 테두리를 둘러 주황 위에서도 읽히게 한다.
    $ft = New-Object System.Windows.Media.FormattedText ('RC', [cultureinfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight, $typeface, 13.0, $green, 1.0)
    $geo = $ft.BuildGeometry((New-Object System.Windows.Point 0, 0))
    $b = $geo.Bounds
    $scale = 19.0 / $b.Width
    $tg = New-Object System.Windows.Media.TransformGroup
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform (-$b.X), (-$b.Y)))
    $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform 4.0, (23.0 - $b.Height * $scale)))
    $dc.PushTransform($tg)
    $pen = New-Object System.Windows.Media.Pen $white, (2.0 / $scale)
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $dc.DrawGeometry($null, $pen, $geo)
    $dc.DrawGeometry($green, $null, $geo)
    $dc.Pop(); $dc.Pop()
    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    return $rtb
}

function ToPng($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

# 작은 프레임은 PNG 가 아니라 전통적인 32비트 DIB 로 넣는다 (System.Drawing 등 PNG 프레임을 못 읽는 로더 대비).
function ToDib($bmp) {
    $size = $bmp.PixelWidth
    $straight = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $size * 4
    $px = New-Object byte[] ($stride * $size)
    $straight.CopyPixels($px, $stride, 0)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))   # 높이는 XOR+AND 마스크 합
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0); $w.Write([uint32]($stride * $size))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($px, $y * $stride, $stride) }   # 아래에서 위로
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskStride * $size)))                            # AND 마스크: 알파를 쓰므로 0
    $w.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$bitmaps = $sizes | ForEach-Object { Render $_ }
$pngs = @(); for ($i = 0; $i -lt $sizes.Count; $i++) { if ($sizes[$i] -ge 256) { $pngs += ,(ToPng $bitmaps[$i]) } else { $pngs += ,(ToDib $bitmaps[$i]) } }
# ICO 컨테이너 (PNG 프레임)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'app.ico'), $out.ToArray())

# 확인용 미리보기
[System.IO.File]::WriteAllBytes((Join-Path $here 'preview-256.png'), (ToPng $bitmaps[$sizes.Count - 1]))
[System.IO.File]::WriteAllBytes((Join-Path $here 'preview-16.png'), (ToPng $bitmaps[0]))
[System.IO.File]::WriteAllBytes((Join-Path $here 'preview-32.png'), (ToPng $bitmaps[3]))
"app.ico written ($($out.Length) bytes, sizes: $($sizes -join ', '))"
