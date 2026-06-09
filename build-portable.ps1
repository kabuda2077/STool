# STool Portable Build Script
# 构建并打包便携版 zip

param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

Write-Host "=== STool Portable Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Green

# 清理旧的发布文件
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
if (Test-Path ".\publish") { Remove-Item ".\publish" -Recurse -Force }
if (Test-Path ".\releases") { Remove-Item ".\releases\STool_v${Version}_Portable.zip" -ErrorAction SilentlyContinue }

# 创建发布目录
New-Item -ItemType Directory -Path ".\releases" -Force | Out-Null

# 构建 Release 版本（自包含，多文件）
Write-Host "`nBuilding Release (self-contained, multi-file)..." -ForegroundColor Yellow
dotnet publish -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o .\publish

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build succeeded!" -ForegroundColor Green

# 清理不必要的文件
Write-Host "`nCleaning unnecessary files..." -ForegroundColor Yellow
Remove-Item ".\publish\*.pdb" -ErrorAction SilentlyContinue

# 创建 README 文件
$readmeContent = @"
STool v$Version - 便携版
========================

## 快速开始

1. 解压到任意目录
2. 双击运行 STool.exe
3. 右键点击托盘图标进行设置

## 功能

- 截图工具 (Alt+1)
- 翻译工具 (Alt+2)
- 剪贴板历史 (Alt+3)
- OCR 文字识别

## 系统要求

- Windows 10/11 (64位)
- .NET 9.0 运行时（已包含，无需额外安装）

## 开机自启

在"通用设置"中勾选"开机自动启动"

## 数据存储

- 配置文件: %APPDATA%\STool\appsettings.json
- 剪贴板数据: %APPDATA%\STool\clipboard.db
- 日志文件: %APPDATA%\STool\Logs\

---

GitHub: https://github.com/kabuda2077/STool
"@

$readmeContent | Out-File -FilePath ".\publish\README.txt" -Encoding UTF8

# 打包成 zip
Write-Host "`nCreating zip package..." -ForegroundColor Yellow
$zipPath = ".\releases\STool_v${Version}_Portable.zip"
Compress-Archive -Path ".\publish\*" -DestinationPath $zipPath -Force

# 获取文件大小
$fileSize = (Get-Item $zipPath).Length / 1MB
Write-Host "`n=== Build Complete ===" -ForegroundColor Cyan
Write-Host "Package: $zipPath" -ForegroundColor Green
Write-Host "Size: $([math]::Round($fileSize, 2)) MB" -ForegroundColor Green

# 显示发布目录内容
Write-Host "`nPublish directory contents:" -ForegroundColor Yellow
Get-ChildItem ".\publish" | ForEach-Object {
    $size = if ($_.PSIsContainer) { "" } else { " ($([math]::Round($_.Length / 1MB, 2)) MB)" }
    Write-Host "  $($_.Name)$size"
}

Write-Host "`nDone! You can test by extracting and running STool.exe from the zip." -ForegroundColor Cyan
