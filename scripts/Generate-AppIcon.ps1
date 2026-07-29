[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\S3Explorer.App\Assets\S3Explorer.ico")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [Parameter(Mandatory)][System.Drawing.RectangleF]$Rectangle,
        [Parameter(Mandatory)][float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([Parameter(Mandatory)][int]$Size)

    $scale = $Size / 64.0
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $backgroundBounds = [System.Drawing.RectangleF]::new(2 * $scale, 2 * $scale, 60 * $scale, 60 * $scale)
        $backgroundPath = New-RoundedRectanglePath -Rectangle $backgroundBounds -Radius (14 * $scale)
        $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $backgroundBounds,
            [System.Drawing.Color]::FromArgb(37, 99, 235),
            [System.Drawing.Color]::FromArgb(14, 116, 144),
            45.0)
        try {
            $graphics.FillPath($background, $backgroundPath)
        }
        finally {
            $background.Dispose()
            $backgroundPath.Dispose()
        }

        $storagePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.0, 4.0 * $scale))
        $storagePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $storagePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $storagePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        try {
            $graphics.DrawEllipse($storagePen, 14 * $scale, 14 * $scale, 36 * $scale, 12 * $scale)
            $graphics.DrawArc($storagePen, 14 * $scale, 24 * $scale, 36 * $scale, 12 * $scale, 0, 180)
            $graphics.DrawArc($storagePen, 14 * $scale, 36 * $scale, 36 * $scale, 12 * $scale, 0, 180)
            $graphics.DrawLine($storagePen, 14 * $scale, 20 * $scale, 14 * $scale, 42 * $scale)
            $graphics.DrawLine($storagePen, 50 * $scale, 20 * $scale, 50 * $scale, 42 * $scale)
        }
        finally {
            $storagePen.Dispose()
        }

        $statusBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(34, 197, 94))
        $statusPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.0, 2.0 * $scale))
        try {
            $graphics.FillEllipse($statusBrush, 43 * $scale, 43 * $scale, 15 * $scale, 15 * $scale)
            $graphics.DrawEllipse($statusPen, 43 * $scale, 43 * $scale, 15 * $scale, 15 * $scale)
        }
        finally {
            $statusBrush.Dispose()
            $statusPen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output -NoEnumerate $stream.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $images.Add((New-IconPng -Size $size))
}
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated application icon: $((Resolve-Path -LiteralPath $OutputPath).Path)"
