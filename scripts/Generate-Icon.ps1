# Original geometric icon. No external artwork or fonts.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconRepoRoot = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $iconRepoRoot 'src\LinkLauncher\Assets\LinkLauncher.ico'
$iconSizes = @(16, 32, 48, 256)
$iconImages = @()
foreach ($iconSize in $iconSizes) {
    $iconBitmap = New-Object System.Drawing.Bitmap($iconSize, $iconSize)
    $iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
    $iconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $iconGraphics.Clear([System.Drawing.Color]::Transparent)
    $iconGraphics.ScaleTransform(($iconSize / 256.0), ($iconSize / 256.0))
    $iconShape = New-Object System.Drawing.Drawing2D.GraphicsPath
    $iconShape.AddArc(0, 0, 116, 116, 180, 90)
    $iconShape.AddArc(140, 0, 116, 116, 270, 90)
    $iconShape.AddArc(140, 140, 116, 116, 0, 90)
    $iconShape.AddArc(0, 140, 116, 116, 90, 90)
    $iconShape.CloseFigure()
    $iconFill = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#16232b'))
    $iconGraphics.FillPath($iconFill, $iconShape)
    $iconPen = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#8fe0c5'), 19)
    $iconPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $iconPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $iconPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $iconGraphics.DrawLines($iconPen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF(65, 61)), (New-Object System.Drawing.PointF(65, 188)), (New-Object System.Drawing.PointF(187, 188))))
    $iconGraphics.DrawLines($iconPen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF(128, 62)), (New-Object System.Drawing.PointF(193, 62)), (New-Object System.Drawing.PointF(193, 126))))
    $iconGraphics.DrawLine($iconPen, 120, 142, 192, 63)
    $iconMemory = New-Object System.IO.MemoryStream
    $iconBitmap.Save($iconMemory, [System.Drawing.Imaging.ImageFormat]::Png)
    $iconImages += ,($iconMemory.ToArray())
    $iconMemory.Dispose(); $iconPen.Dispose(); $iconFill.Dispose(); $iconShape.Dispose(); $iconGraphics.Dispose(); $iconBitmap.Dispose()
}
$iconFile = [System.IO.File]::Create($iconPath)
$iconWriter = New-Object System.IO.BinaryWriter($iconFile)
try {
    $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]$iconSizes.Count)
    $iconOffset = 6 + 16 * $iconSizes.Count
    for ($iconIndex = 0; $iconIndex -lt $iconSizes.Count; $iconIndex++) {
        $iconDimension = if ($iconSizes[$iconIndex] -eq 256) { 0 } else { $iconSizes[$iconIndex] }
        $iconWriter.Write([byte]$iconDimension); $iconWriter.Write([byte]$iconDimension)
        $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$iconImages[$iconIndex].Length); $iconWriter.Write([uint32]$iconOffset)
        $iconOffset += $iconImages[$iconIndex].Length
    }
    foreach ($iconImage in $iconImages) { $iconWriter.Write([byte[]]$iconImage) }
} finally { $iconWriter.Dispose(); $iconFile.Dispose() }
Write-Output $iconPath
