# Regenerate icon.ico from Claude.ico as proper square multi-resolution ICO.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$src = "D:\10. RED_TEAM\AI\AI Control\Claude.ico"
$dst = "D:\10. RED_TEAM\AI\AI Control\assets\icon.ico"
$appDst = "D:\10. RED_TEAM\AI\AI Control\src\ClaudeCodeManager.App\Assets\icon.ico"

# Source ICO contains DIB data; load via Icon class which handles both DIB and PNG entries.
$icon = New-Object System.Drawing.Icon($src, 256, 256)
$srcImg = $icon.ToBitmap()
Write-Output ("source: {0}x{1}" -f $srcImg.Width, $srcImg.Height)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $canvas = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    # Fit source preserving aspect, center on square canvas
    $ar = $srcImg.Width / $srcImg.Height
    if ($ar -ge 1.0) {
        $newW = $s
        $newH = [int]([math]::Round($s / $ar))
    } else {
        $newH = $s
        $newW = [int]([math]::Round($s * $ar))
    }
    $x = [int](($s - $newW) / 2)
    $y = [int](($s - $newH) / 2)
    $g.DrawImage($srcImg, $x, $y, $newW, $newH)
    $g.Dispose()
    $pngMs = New-Object System.IO.MemoryStream
    $canvas.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$pngMs.ToArray()
    $canvas.Dispose()
}
$srcImg.Dispose()
$icon.Dispose()

# Build ICO
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)
$dataOffset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $sz = $pngs[$i].Length
    $bw.Write([byte]($s -band 0xFF))
    $bw.Write([byte]($s -band 0xFF))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$sz)
    $bw.Write([uint32]$dataOffset)
    $dataOffset += $sz
}
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngs[$i])
}
$bw.Flush()
[System.IO.File]::WriteAllBytes($dst, $out.ToArray())
Copy-Item -LiteralPath $dst -Destination $appDst -Force
$bw.Dispose()
$out.Dispose()
Write-Output ("wrote {0} ({1} bytes, {2} sizes)" -f $dst, (Get-Item $dst).Length, $sizes.Count)
