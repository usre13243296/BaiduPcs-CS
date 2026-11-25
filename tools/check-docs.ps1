#Requires -Version 5.1
<#
.SYNOPSIS
    文档一致性检查：版本号、测试用例数、相对链接、数字去重。

.DESCRIPTION
    本脚本由 CI（.github/workflows/build.yml）与本地发布流程调用。
    兼容 Windows PowerShell 5.1 与 PowerShell 7（Linux / macOS / CI）。
    检查项：
      1. docs/FACTS.md 的「当前版本」== BaiduPcsAPP.csproj 的 <Version>，
         并核对 README.md / llms.txt 中的「当前版本」行。
      2. docs/FACTS.md 的「测试用例总数」== 测试项目里 [Fact] + [InlineData] 的数量。
      3. 全仓 markdown 的相对链接目标存在。
      4. README.md / CONTRIBUTING.md / llms.txt 里不出现写死的测试用例数
         （数字应只写在 docs/FACTS.md）。
    任一不符 → 退出码 1。

.PARAMETER Root
    仓库根目录（含 BaiduPcs.slnx 的那一层）。默认取本脚本的上一级。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-docs.ps1

.EXAMPLE
    pwsh -NoProfile -File tools/check-docs.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

# 中文输出：Windows PowerShell 5.1 的控制台默认不是 UTF-8，尽量改成 UTF-8（失败不影响检查）
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$Root = (Resolve-Path -LiteralPath $Root).Path

$failures = [System.Collections.Generic.List[string]]::new()
$passes = 0

function Add-Fail([string]$message) {
    $script:failures.Add($message)
    Write-Host "  [FAIL] $message" -ForegroundColor Red
}

function Add-Pass([string]$message) {
    $script:passes++
    Write-Host "  [ OK ] $message" -ForegroundColor Green
}

function Read-Text([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}

# 把绝对路径转成相对仓库根的路径（.NET Framework 没有 Path.GetRelativePath）
function Get-RelativeToRoot([string]$rootPath, [string]$fullPath) {
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $prefix = $rootPath.TrimEnd('\', '/') + $sep
    if ($fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($prefix.Length)
    }
    return $fullPath
}

Write-Host "文档一致性检查：$Root" -ForegroundColor Cyan
Write-Host '-----------------------------------------------------------'

# ============================================================
# 1. 版本号
# ============================================================
Write-Host '1) 版本号一致性'

$csprojPath = Join-Path $Root 'BaiduPcsAPP/BaiduPcsAPP.csproj'
$factsPath = Join-Path $Root 'docs/FACTS.md'
$readmePath = Join-Path $Root 'README.md'
$llmsPath = Join-Path $Root 'llms.txt'

foreach ($p in @($csprojPath, $factsPath, $readmePath, $llmsPath)) {
    if (-not (Test-Path -LiteralPath $p)) {
        Add-Fail "缺少文件：$p"
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "检查中止：关键文件缺失（$($failures.Count) 项）" -ForegroundColor Red
    exit 1
}

$csprojText = Read-Text $csprojPath
$versionMatch = [regex]::Match($csprojText, '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $versionMatch.Success) {
    Add-Fail 'BaiduPcsAPP.csproj 中找不到 <Version>'
    $version = $null
}
else {
    $version = $versionMatch.Groups[1].Value
    Add-Pass "csproj 版本号 = $version"

    # FACTS.md：| 当前版本 | v0.6.0 |
    $factsVersion = [regex]::Match((Read-Text $factsPath), '\|\s*当前版本\s*\|\s*v?([0-9]+\.[0-9]+\.[0-9]+)')
    if (-not $factsVersion.Success) {
        Add-Fail 'docs/FACTS.md 缺少「| 当前版本 | vX.Y.Z |」行'
    }
    elseif ($factsVersion.Groups[1].Value -ne $version) {
        Add-Fail "docs/FACTS.md 当前版本 $($factsVersion.Groups[1].Value) ≠ csproj $version"
    }
    else {
        Add-Pass 'docs/FACTS.md 当前版本一致'
    }

    # README.md：| 当前版本 | v0.6.0（…） |
    $readmeVersion = [regex]::Match((Read-Text $readmePath), '\|\s*当前版本\s*\|\s*v?([0-9]+\.[0-9]+\.[0-9]+)')
    if (-not $readmeVersion.Success) {
        Add-Fail 'README.md 缺少「| 当前版本 | vX.Y.Z |」行'
    }
    elseif ($readmeVersion.Groups[1].Value -ne $version) {
        Add-Fail "README.md 当前版本 $($readmeVersion.Groups[1].Value) ≠ csproj $version"
    }
    else {
        Add-Pass 'README.md 当前版本一致'
    }

    # llms.txt：- 当前版本：v0.6.0
    $llmsVersion = [regex]::Match((Read-Text $llmsPath), '当前版本[：:]\s*v?([0-9]+\.[0-9]+\.[0-9]+)')
    if (-not $llmsVersion.Success) {
        Add-Fail 'llms.txt 缺少「当前版本：vX.Y.Z」'
    }
    elseif ($llmsVersion.Groups[1].Value -ne $version) {
        Add-Fail "llms.txt 当前版本 $($llmsVersion.Groups[1].Value) ≠ csproj $version"
    }
    else {
        Add-Pass 'llms.txt 当前版本一致'
    }
}

Write-Host ''

# ============================================================
# 2. 测试用例总数
# ============================================================
Write-Host '2) 测试用例总数（FACTS.md ↔ 测试代码）'

$testsDir = Join-Path $Root 'BaiduPcs.Core.Tests'
if (-not (Test-Path -LiteralPath $testsDir)) {
    Add-Fail "找不到测试项目目录：$testsDir"
}
else {
    $testFiles = Get-ChildItem -LiteralPath $testsDir -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

    $factCount = 0
    $inlineCount = 0
    foreach ($file in $testFiles) {
        $text = Read-Text $file.FullName
        $factCount += ([regex]::Matches($text, '\[Fact\]')).Count
        $inlineCount += ([regex]::Matches($text, '\[InlineData')).Count
    }

    $actualTotal = $factCount + $inlineCount
    $factsTotalMatch = [regex]::Match((Read-Text $factsPath), '\|\s*测试用例总数\s*\|\s*([0-9]+)')
    if (-not $factsTotalMatch.Success) {
        Add-Fail 'docs/FACTS.md 缺少「| 测试用例总数 | N |」行'
    }
    else {
        $declaredTotal = [int]$factsTotalMatch.Groups[1].Value
        if ($declaredTotal -ne $actualTotal) {
            Add-Fail "docs/FACTS.md 声明 $declaredTotal 个用例，代码里实际是 $actualTotal 个（[Fact]=$factCount + [InlineData]=$inlineCount）"
        }
        else {
            Add-Pass "用例总数一致：$actualTotal 个（[Fact]=$factCount + [InlineData]=$inlineCount）"
        }
    }
}

Write-Host ''

# ============================================================
# 3. markdown 相对链接
# ============================================================
Write-Host '3) markdown 相对链接'

$mdFiles = Get-ChildItem -LiteralPath $Root -Recurse -Filter *.md -File |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj|\.git|node_modules)\\' -and
        $_.FullName -notmatch '\\BaiduPCS-Go-main\\'
    }

$linkCount = 0
$linkErrors = 0

foreach ($file in $mdFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $inFence = $false
    $baseDir = Split-Path -Parent $file.FullName

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]

        # 跳过围栏代码块（``` 或 ~~~），避免把示例里的 [x](y) 当成链接
        if ($line -match '^\s*(```|~~~)') {
            $inFence = -not $inFence
            continue
        }
        if ($inFence) { continue }

        foreach ($m in [regex]::Matches($line, '\]\(([^)]+)\)')) {
            $target = $m.Groups[1].Value.Trim()

            # 去掉标题与尖括号
            if ($target -match '^<(.+)>$') { $target = $Matches[1] }
            $target = ($target -split '\s+')[0]
            if ([string]::IsNullOrWhiteSpace($target)) { continue }

            # 跳过外链、锚点、协议链接
            if ($target -match '^(https?:|mailto:|tel:|ftp:|#|data:)') { continue }
            # 跳过变量占位
            if ($target -match '\{\{|\$\{') { continue }

            $target = $target.Split('#')[0].Split('?')[0]
            if ([string]::IsNullOrWhiteSpace($target)) { continue }

            $linkCount++
            $decoded = [System.Uri]::UnescapeDataString($target)
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $baseDir $decoded))
            if (-not (Test-Path -LiteralPath $resolved)) {
                $linkErrors++
                $relative = Get-RelativeToRoot $Root $file.FullName
                Add-Fail "链接失效：${relative}:$($i + 1) → $target"
            }
        }
    }
}

if ($linkErrors -eq 0) {
    Add-Pass "相对链接全部有效（共检查 $linkCount 条，覆盖 $($mdFiles.Count) 份文档）"
}

Write-Host ''

# ============================================================
# 4. 写死的测试数量（应只存在于 FACTS.md）
# ============================================================
Write-Host '4) 数字去重（写死的用例数）'

$dedupeTargets = @(
    (Join-Path $Root 'README.md'),
    (Join-Path $Root 'CONTRIBUTING.md'),
    (Join-Path $Root 'llms.txt')
)

# 形如「164 个用例」「165 个单元/集成测试」「tests-164」
$hardcodedPatterns = @(
    '[0-9]{2,}\s*个[^。，,\n]{0,6}用例',
    'tests-[0-9]{2,}',
    '[0-9]{2,}\s*unit and integration tests'
)

$dedupeErrors = 0
foreach ($path in $dedupeTargets) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $text = Read-Text $path
    $relative = Get-RelativeToRoot $Root $path

    foreach ($pattern in $hardcodedPatterns) {
        foreach ($hit in [regex]::Matches($text, $pattern)) {
            $dedupeErrors++
            Add-Fail "写死了测试用例数（应改为指向 docs/FACTS.md）：$relative → '$($hit.Value)'"
        }
    }
}

if ($dedupeErrors -eq 0) {
    Add-Pass 'README / CONTRIBUTING / llms.txt 未写死用例数'
}

Write-Host ''
Write-Host '-----------------------------------------------------------'
if ($failures.Count -eq 0) {
    Write-Host "全部通过：$passes 项检查" -ForegroundColor Green
    exit 0
}

Write-Host "检查失败：$($failures.Count) 项问题，$passes 项通过" -ForegroundColor Red
Write-Host '修复提示：数字只写在 docs/FACTS.md；文档地图与同步规则见 docs/MAINTENANCE.md' -ForegroundColor Yellow
exit 1
