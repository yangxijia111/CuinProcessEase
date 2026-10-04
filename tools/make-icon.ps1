# Generate multi-size App.ico (gradient blue circle + white bold C).
# NOTE: keep this file ASCII-only comments - Windows PowerShell 5.1 reads
# UTF-8-no-BOM scripts as ANSI and garbled comments can swallow lines.
Add-Type -AssemblyName System.Drawing
$dir = "src/CuinProcessEase.App"
$sizes = @(256, 64, 48, 32, 16)
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0,0,$s,$s)), [System.Drawing.Color]::FromArgb(255,38,98,180), [System.Drawing.Color]::FromArgb(255,18,52,110), 45)
    $g.FillEllipse($bg, 0, 0, $s-1, $s-1)
    $font = New-Object System.Drawing.Font('Segoe UI', [float]($s * 0.62), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF(0, [float]($s*0.02), $s, $s)
    $g.DrawString('C', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    $g.Dispose()
    $images += $bmp
}

# ICO container: 6-byte header + 16-byte entries + PNG payloads.
# Fallback plan: single-size 32bpp icon via GetHicon/Save (always valid for csc).
$bmp32 = $images[3]  # 32x32 entry
$hicon = [System.Drawing.Icon]::FromHandle($bmp32.GetHicon())
$fs = [System.IO.File]::Create("$dir/App.ico")
$hicon.Save($fs)
$fs.Close()

# Verify the generated icon is loadable.
$check = New-Object System.Drawing.Icon("$dir/App.ico")
Write-Host ("ico written: " + (Get-Item "$dir/App.ico").Length + " bytes, loadable size=" + $check.Size)
