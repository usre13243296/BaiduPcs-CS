<#
.SYNOPSIS
    生成 GitHub 社交预览图（1280x640）：docs/screenshot.png → docs/social-preview.png

.DESCRIPTION
    GitHub 的 Social preview 没有 API，只能在仓库 Settings 页上传。
    本脚本用 System.Drawing 把主界面截图合成成带标题的 1.91:1 预览图。
    兼容 Windows PowerShell 5.1 与 PowerShell 7。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/make-social-preview.ps1
#>

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$shotPath = Join-Path $root 'docs/screenshot.png'
$outPath = Join-Path $root 'docs/social-preview.png'

if (-not (Test-Path $shotPath)) {
    throw "找不到截图：$shotPath"
}

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Get-FontFamily {
    param([string[]]$Candidates)

    $installed = [System.Drawing.FontFamily]::Families | ForEach-Object { $_.Name }
    foreach ($name in $Candidates) {
        if ($installed -contains $name) {
            return $name
        }
    }

    return 'Arial'
}

$uiFont = Get-FontFamily @('Microsoft YaHei', 'Microsoft YaHei UI', 'Segoe UI')
$monoFont = Get-FontFamily @('Consolas', 'Cascadia Mono', 'Courier New')

$width = 1280
$height = 640
$bitmap = [System.Drawing.Bitmap]::new($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$canvas = [System.Drawing.Rectangle]::new(0, 0, $width, $height)

# 背景：深蓝渐变
$bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($canvas, [System.Drawing.ColorTranslator]::FromHtml('#0B1220'), [System.Drawing.ColorTranslator]::FromHtml('#1E2A5A'), 35.0)
$graphics.FillRectangle($bgBrush, $canvas)

# 右上角柔光
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(880, -220, 620, 520)
$glowBrush = [System.Drawing.Drawing2D.PathGradientBrush]::new($glowPath)
$glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(46, 77, 107, 254)
$glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 77, 107, 254))
$graphics.FillPath($glowBrush, $glowPath)

# 左侧品牌信息
$accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#4D6BFE'))
$graphics.FillRectangle($accentBrush, 80, 118, 6, 96)

$white = [System.Drawing.Color]::White
$softText = [System.Drawing.ColorTranslator]::FromHtml('#C7D2FE')
$mutedText = [System.Drawing.ColorTranslator]::FromHtml('#8FA0C8')
$faintText = [System.Drawing.ColorTranslator]::FromHtml('#6E7DA8')

$whiteBrush = [System.Drawing.SolidBrush]::new($white)
$softBrush = [System.Drawing.SolidBrush]::new($softText)
$mutedBrush = [System.Drawing.SolidBrush]::new($mutedText)
$faintBrush = [System.Drawing.SolidBrush]::new($faintText)

$titleFont = [System.Drawing.Font]::new($uiFont, 58, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subFont = [System.Drawing.Font]::new($uiFont, 32, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$bodyFont = [System.Drawing.Font]::new($uiFont, 20, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$chipFont = [System.Drawing.Font]::new($uiFont, 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$footFont = [System.Drawing.Font]::new($monoFont, 17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$graphics.DrawString('BaiduPcs-CS', $titleFont, $whiteBrush, 104, 104)
$graphics.DrawString('百度网盘 C# 开源客户端', $subFont, $softBrush, 106, 192)
$graphics.DrawString('C# / .NET 10 + Avalonia 12.1 · MVVM · MIT', $bodyFont, $mutedBrush, 106, 250)
$graphics.DrawString('多线程分片下载 · 断点续传 · 秒传上传', $bodyFont, $mutedBrush, 106, 288)
$graphics.DrawString('传输队列 · 多账号 · 跨平台单文件发布', $bodyFont, $mutedBrush, 106, 326)

# 平台标签
$chipY = 382
$chipX = 106
$chipBg = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#18234B'))
$chipText = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#A5B4FC'))
$chipPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#2E3C77'), 1)

foreach ($label in @('Windows', 'Linux', 'macOS', 'NAS / ARM64')) {
    $size = $graphics.MeasureString($label, $chipFont)
    $rect = [System.Drawing.RectangleF]::new($chipX, $chipY, [float]($size.Width + 26), 34)
    $path = New-RoundedPath $rect 17
    $graphics.FillPath($chipBg, $path)
    $graphics.DrawPath($chipPen, $path)
    $graphics.DrawString($label, $chipFont, $chipText, ($chipX + 13), ($chipY + 8))
    $path.Dispose()
    $chipX += [int]($size.Width + 26) + 10
}

$graphics.DrawString('github.com/usre13243296/BaiduPcs-CS', $footFont, $faintBrush, 106, 540)

# 右侧截图卡片（带阴影 + 圆角）
$shot = [System.Drawing.Image]::FromFile($shotPath)
$cardWidth = 560
$cardHeight = [int]($cardWidth * $shot.Height / $shot.Width)
$cardX = 660
$cardY = [int](($height - $cardHeight) / 2)

for ($i = 3; $i -ge 1; $i--) {
    $shadowRect = [System.Drawing.RectangleF]::new(($cardX - $i * 2), ($cardY - $i * 2), ($cardWidth + $i * 4), ($cardHeight + $i * 4))
    $shadowPath = New-RoundedPath $shadowRect (18 + $i * 2)
    $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(18, 0, 0, 0))
    $graphics.FillPath($shadowBrush, $shadowPath)
    $shadowPath.Dispose()
}

$cardRect = [System.Drawing.RectangleF]::new($cardX, $cardY, $cardWidth, $cardHeight)
$cardPath = New-RoundedPath $cardRect 16
$cardBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#111A38'))
$graphics.FillPath($cardBrush, $cardPath)
$graphics.SetClip($cardPath)
$graphics.DrawImage($shot, $cardRect)
$graphics.ResetClip()
$cardPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#33417A'), 2)
$graphics.DrawPath($cardPen, $cardPath)

$shot.Dispose()
$cardPath.Dispose()
$graphics.Dispose()
$bitmap.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$info = Get-Item $outPath
Write-Host ("已生成 {0}（{1} x {2}, {3:N0} KB）" -f $info.FullName, $width, $height, ($info.Length / 1KB))
