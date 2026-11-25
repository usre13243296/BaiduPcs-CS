#Requires -Version 5.1
<#
.SYNOPSIS
    为当前小版本打一份独立备份（源码快照 + git bundle + 清单）。

.DESCRIPTION
    备份默认写到「工作区根目录」的 version-backups\ 下（仓库的上一级），不进入仓库、不被 git 跟踪。
    每份备份包含：
      1. 源码快照（排除 bin / obj / publish / .git / BaiduPCS-Go-main 等）；
      2. repo.bundle —— git 全量历史（可用 git clone repo.bundle 还原）；
      3. BACKUP.md —— 版本号、提交号、工作区状态、文件数、恢复步骤。
    兼容 Windows PowerShell 5.1 与 PowerShell 7。

.PARAMETER Label
    版本标签，用于目录名，例如 v0.6.0 / v0.6.1。不能包含路径分隔符。

.PARAMETER Note
    写进 BACKUP.md 的备注，例如「阶段 0：界面可用性 + 安全默认值」。

.PARAMETER OutDir
    备份根目录。默认 <仓库上一级>\version-backups。

.PARAMETER RepoPath
    仓库目录。默认本脚本的上一级。

.PARAMETER SkipBundle
    跳过 git bundle（例如仓库还没初始化时）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\backup-version.ps1 -Label v0.6.1 -Note "阶段 0 修复"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Label,

    [string]$Note = '',
    [string]$OutDir = '',
    [string]$RepoPath = '',
    [switch]$SkipBundle
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# ---------- 参数与目录 ----------
if ($Label -match '[\\/:*?"<>|]') {
    throw "Label 不能包含路径分隔符或非法字符：$Label"
}

if ([string]::IsNullOrWhiteSpace($RepoPath)) {
    $RepoPath = Split-Path -Parent $PSScriptRoot
}
$RepoPath = (Resolve-Path -LiteralPath $RepoPath).Path

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path -Parent $RepoPath) 'version-backups'
}

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$target = Join-Path $OutDir ("{0}_{1}" -f $Label, $stamp)

if (Test-Path -LiteralPath $target) {
    throw "备份目录已存在（同一分钟重复执行？）：$target"
}

New-Item -ItemType Directory -Path $target -Force | Out-Null

# ---------- 排除规则 ----------
$excludeSegments = @('bin', 'obj', 'publish', 'TestResults', '.git', '.vs', '.idea', '.vscode', 'node_modules', 'BaiduPCS-Go-main')
$excludeFiles = @('*.part', '*.part.json', '*.user', '*.suo', '*.trx')

function Test-Excluded([string]$relativePath) {
    foreach ($segment in ($relativePath -split '[\\/]')) {
        if ($excludeSegments -contains $segment) { return $true }
    }
    foreach ($pattern in $excludeFiles) {
        if ($relativePath -like $pattern) { return $true }
    }
    return $false
}

# ---------- 复制快照 ----------
Write-Host "备份小版本 $Label" -ForegroundColor Cyan
Write-Host "  源目录：$RepoPath"
Write-Host "  目标：  $target"

$repoFull = $RepoPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$files = Get-ChildItem -LiteralPath $RepoPath -Recurse -File -Force
$copied = 0
$bytes = 0

foreach ($file in $files) {
    $relative = $file.FullName.Substring($repoFull.Length)
    if (Test-Excluded $relative) { continue }

    $destination = Join-Path $target $relative
    $destinationDir = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    $copied++
    $bytes += $file.Length
}

Write-Host ("  已复制 {0} 个文件，{1:N1} MB" -f $copied, ($bytes / 1MB))

# ---------- git 信息 ----------
$gitBranch = '(非 git 仓库)'
$gitCommit = '-'
$gitDescribe = '-'
$gitLastLog = '-'
$gitDirty = '未知'

$hasGit = Test-Path -LiteralPath (Join-Path $RepoPath '.git')
if ($hasGit) {
    try {
        $gitBranch = (git -C $RepoPath rev-parse --abbrev-ref HEAD 2>$null)
        $gitCommit = (git -C $RepoPath rev-parse --short HEAD 2>$null)
        $gitDescribe = (git -C $RepoPath describe --tags --always --dirty 2>$null)
        $gitLastLog = (git -C $RepoPath log -1 --pretty='%h %ad %s' --date=short 2>$null)
        $porcelain = @(git -C $RepoPath status --porcelain 2>$null)
        $gitDirty = if ($porcelain.Count -eq 0) { '干净' } else { "有 $($porcelain.Count) 处未提交改动（已包含在源码快照里）" }
    }
    catch {
        $gitDirty = "读取失败：$($_.Exception.Message)"
    }
}

# ---------- git bundle（全量历史） ----------
$bundleState = '未生成'
if ($hasGit -and -not $SkipBundle) {
    try {
        $bundlePath = Join-Path $target 'repo.bundle'
        git -C $RepoPath bundle create $bundlePath --all 2>&1 | Out-Null
        if (Test-Path -LiteralPath $bundlePath) {
            $bundleState = ('已生成（{0:N0} KB）' -f ((Get-Item $bundlePath).Length / 1KB))
        }
        else {
            $bundleState = '生成失败（bundle 文件不存在）'
        }
    }
    catch {
        $bundleState = "生成失败：$($_.Exception.Message)"
    }
}
elseif ($SkipBundle) {
    $bundleState = '按参数跳过'
}

# ---------- 版本号 ----------
$version = '(未找到)'
$csproj = Join-Path $RepoPath 'BaiduPcsAPP/BaiduPcsAPP.csproj'
if (-not (Test-Path -LiteralPath $csproj)) {
    $csproj = Join-Path $RepoPath 'BaiduPcsAPP\BaiduPcsAPP.csproj'
}
if (Test-Path -LiteralPath $csproj) {
    $m = [regex]::Match([System.IO.File]::ReadAllText($csproj), '<Version>\s*([^<\s]+)\s*</Version>')
    if ($m.Success) { $version = $m.Groups[1].Value }
}

# ---------- 清单（单引号模板 + 占位替换，避免反引号被当作转义符） ----------
if ([string]::IsNullOrWhiteSpace($Note)) { $Note = '（未填写）' }
$now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$sizeText = ('{0:N1} MB' -f ($bytes / 1MB))

$template = @'
# 备份清单 · @LABEL@

| 项目 | 值 |
|------|-----|
| 备份时间 | @TIME@ |
| 版本标签 | @LABEL@ |
| csproj 版本 | @VERSION@ |
| git 分支 | @BRANCH@ |
| git 提交 | @COMMIT@（@DESCRIBE@） |
| 工作区状态 | @DIRTY@ |
| 最近一次提交 | @LASTLOG@ |
| git 历史包 | @BUNDLE@ |
| 文件数 / 体积 | @COUNT@ 个 / @SIZE@ |
| 源目录 | @REPO@ |

## 备注

@NOTE@

## 快照内容

源码与文档的完整快照，含 `.github`、`docs`、`tools`、`*.csproj`、`*.slnx`、`LICENSE`、`llms.txt` 等。

排除目录：`bin/`、`obj/`、`publish/`、`TestResults/`、`.git/`、`.vs/`、`.idea/`、`.vscode/`、`node_modules/`、`BaiduPCS-Go-main/`；
排除文件：`*.part`、`*.part.json`、`*.user`、`*.suo`、`*.trx`。

## 恢复方式

1. 只看旧版本源码：直接在本目录里翻阅。
2. 把某个版本恢复成工作目录：先另存当前 `BaiduPcs` 目录，再把本目录内容复制回去；若需保留历史，配合 `repo.bundle` 或原仓库的 `.git`。
3. 用 bundle 还原带历史的仓库：

   ~~~bash
   git clone repo.bundle restored-repo
   ~~~

4. 验证该版本可用：

   ~~~powershell
   dotnet build BaiduPcs.slnx -c Release
   dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-docs.ps1
   ~~~
'@

$manifest = $template.
    Replace('@LABEL@', $Label).
    Replace('@TIME@', $now).
    Replace('@VERSION@', $version).
    Replace('@BRANCH@', $gitBranch).
    Replace('@COMMIT@', $gitCommit).
    Replace('@DESCRIBE@', $gitDescribe).
    Replace('@DIRTY@', $gitDirty).
    Replace('@LASTLOG@', $gitLastLog).
    Replace('@BUNDLE@', $bundleState).
    Replace('@COUNT@', "$copied").
    Replace('@SIZE@', $sizeText).
    Replace('@REPO@', $RepoPath).
    Replace('@NOTE@', $Note)

$manifestPath = Join-Path $target 'BACKUP.md'
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# ---------- 汇总 ----------
$totalSize = (Get-ChildItem -LiteralPath $target -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host ("备份完成：{0}" -f $target) -ForegroundColor Green
Write-Host ("  版本 {0} ｜ git {1} ｜ {2} 个文件 ｜ 合计 {3:N1} MB" -f $version, $gitCommit, $copied, ($totalSize / 1MB))
Write-Host ("  历史包：{0}" -f $bundleState)
Write-Host ("  清单：  {0}" -f $manifestPath)
