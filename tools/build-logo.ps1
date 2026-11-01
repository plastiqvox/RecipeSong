param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\src\RecipeSong.Wpf\Assets'),
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$AssetDirectory = [System.IO.Path]::GetFullPath($AssetDirectory)
[xml]$logoDocument = [System.IO.File]::ReadAllText((Join-Path $AssetDirectory 'logo.svg'))
if ($logoDocument.DocumentElement.viewBox -ne '0 0 256 256') { throw 'Expected a 256 x 256 SVG viewBox.' }

# 원본의 단색 path를 WPF로 그린다. 외부 변환기나 사용자 앱 실행은 필요하지 않다.
$logoDrawing = [System.Windows.Media.DrawingGroup]::new()
foreach ($node in $logoDocument.GetElementsByTagName('path')) {
    $geometry = [System.Windows.Media.Geometry]::Parse($node.d)
    $fill = $null
    if ($node.fill -and $node.fill -ne 'none') { $fill = [System.Windows.Media.BrushConverter]::new().ConvertFromString($node.fill) }
    $pen = $null
    if ($node.stroke) {
        $brush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($node.stroke)
        $pen = [System.Windows.Media.Pen]::new($brush, [double]$node.'stroke-width')
    }
    $drawing = [System.Windows.Media.GeometryDrawing]::new($fill, $pen, $geometry)
    $group = [System.Windows.Media.DrawingGroup]::new()
    if ($node.opacity) { $group.Opacity = [double]$node.opacity }
    $group.Children.Add($drawing)
    $logoDrawing.Children.Add($group)
}
$logoDrawing.Freeze()

function New-RecipeSongLogoBitmap([int]$Size) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([System.Windows.Media.ScaleTransform]::new($Size / 256.0, $Size / 256.0))
        $context.DrawDrawing($logoDrawing)
        $context.Pop()
    }
    finally { $context.Close() }
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

function ConvertTo-RecipeSongPng([System.Windows.Media.Imaging.BitmapSource]$Bitmap) {
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}

[System.IO.File]::WriteAllBytes((Join-Path $AssetDirectory 'logo.png'), (ConvertTo-RecipeSongPng (New-RecipeSongLogoBitmap 512)))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(ConvertTo-RecipeSongPng (New-RecipeSongLogoBitmap $_)) })
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $AssetDirectory 'app.ico')))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }

if ($PreviewPath) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.DrawRectangle([System.Windows.Media.BrushConverter]::new().ConvertFromString('#101213'), $null, [System.Windows.Rect]::new(0, 0, 920, 360))
        $context.DrawImage((New-RecipeSongLogoBitmap 256), [System.Windows.Rect]::new(32, 48, 256, 256))
        $positions = @(@(332, 96, 128), @(492, 128, 64), @(596, 144, 32), @(668, 148, 24), @(732, 152, 16))
        foreach ($position in $positions) {
            $context.DrawImage((New-RecipeSongLogoBitmap $position[2]), [System.Windows.Rect]::new($position[0], $position[1], $position[2], $position[2]))
        }
    }
    finally { $context.Close() }
    $preview = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(920, 360, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $preview.Render($visual)
    [System.IO.File]::WriteAllBytes($PreviewPath, (ConvertTo-RecipeSongPng $preview))
}
Write-Output 'Generated logo.png (512 px) and app.ico (16-256 px).'
