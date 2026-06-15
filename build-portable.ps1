# STool Portable Build Script
# 构建并打包便携版 zip

param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

Write-Host "=== STool Portable Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Green

$artifactRoot = ".\artifacts"
$publishDir = Join-Path $artifactRoot "publish"
$releaseDir = Join-Path $artifactRoot "releases"

# 清理旧的发布文件
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path (Join-Path $releaseDir "STool_v${Version}_Portable.zip")) {
    Remove-Item (Join-Path $releaseDir "STool_v${Version}_Portable.zip") -ErrorAction SilentlyContinue
}

# 创建发布目录
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# 构建 Release 版本（依赖本机 .NET Desktop Runtime，单文件）
Write-Host "`nBuilding Release (framework-dependent, single-file)..." -ForegroundColor Yellow
dotnet publish -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build succeeded!" -ForegroundColor Green

# 清理不必要的文件
Write-Host "`nCleaning unnecessary files..." -ForegroundColor Yellow
Remove-Item (Join-Path $publishDir "*.pdb") -ErrorAction SilentlyContinue

# 创建 README 文件
$readmeContent = @"
STool v$Version - 便携版
========================

## 快速开始

1. 解压到任意目录
2. 双击运行 STool.exe
3. 右键点击托盘图标进行设置

## 功能

- 截图工具 (Ctrl+Alt+A)
- 翻译工具 (Ctrl+Alt+T)
- 剪贴板历史 (Ctrl+Alt+V)
- OCR 文字识别

## 系统要求

- Windows 10/11 (64位)
- .NET 9 Desktop Runtime x64

下载地址: https://dotnet.microsoft.com/download/dotnet/9.0

## 开机自启

在"通用设置"中勾选"开机自动启动"

## 数据存储

- 配置文件: .\Data\config.json
- 剪贴板数据: .\Data\clipboard.db
- 剪贴板图片: .\Data\ClipboardImages\
- 日志文件: .\Data\Logs\
- 本地密钥: .\Data\secure.key

API Key 等敏感配置会写入 config.json,但内容使用 .\Data\secure.key 加密。
备份或迁移时请保留整个 Data 文件夹；不要把 Data 文件夹分享给别人。

---

GitHub: https://github.com/kabuda2077/STool
"@

$readmeContent | Out-File -FilePath (Join-Path $publishDir "README.txt") -Encoding UTF8

# 打包成 zip
Write-Host "`nCreating zip package..." -ForegroundColor Yellow
$zipPath = Join-Path $releaseDir "STool_v${Version}_Portable.zip"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

# 获取文件大小
$fileSize = (Get-Item $zipPath).Length / 1MB
Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
Write-Host "Package: $zipPath" -ForegroundColor Green
Write-Host "Size: $([math]::Round($fileSize, 2)) MB" -ForegroundColor Green

# 显示发布目录内容
Write-Host "`nPublish directory contents:" -ForegroundColor Yellow
Get-ChildItem $publishDir | ForEach-Object {
    $size = if ($_.PSIsContainer) { "" } else { " ($([math]::Round($_.Length / 1MB, 2)) MB)" }
    Write-Host "  $($_.Name)$size"
}

Write-Host "`nDone! You can test by extracting and running STool.exe from the zip." -ForegroundColor Cyan
