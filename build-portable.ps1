# STool 便携版构建脚本
# 生成框架依赖的单文件 exe（需要用户安装 .NET 9 Desktop Runtime）

param(
    [string]$Version = "1.2.2"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== STool 便携版构建 ===" -ForegroundColor Cyan
Write-Host "版本: $Version" -ForegroundColor Green

$artifactRoot = ".\artifacts"
$publishDir = Join-Path $artifactRoot "publish"
$releaseDir = Join-Path $artifactRoot "releases"

# 清理旧构建产物
Write-Host "`n清理旧文件..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
if (Test-Path (Join-Path $releaseDir "STool_v${Version}_Portable.zip")) {
    Remove-Item (Join-Path $releaseDir "STool_v${Version}_Portable.zip") -Force
}

# 创建发布目录
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# 构建框架依赖的单文件版本
Write-Host "`n构建 Release 版本 (框架依赖单文件)..." -ForegroundColor Yellow
dotnet publish -c Release `
    --runtime win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "构建失败!" -ForegroundColor Red
    exit 1
}

Write-Host "构建成功!" -ForegroundColor Green

# 清理不必要的文件
Write-Host "`n清理不必要的文件..." -ForegroundColor Yellow
Remove-Item (Join-Path $publishDir "*.pdb") -ErrorAction SilentlyContinue

# 创建 README 文件（使用 UTF-8 with BOM 以便 Windows 记事本正确显示）
$readmeContent = @"
STool v$Version - 便携版
========================

## 快速开始

1. 解压到任意目录
2. 双击运行 STool.exe
3. 右键点击托盘图标进行设置

## 主要功能

- 截图工具 (Alt+1)
  * 支持原位翻译（快速模式 / 智能模式）
- 翻译工具 (Alt+2)
- 剪贴板历史 (Alt+3)
- 设置面板 (Alt+4)
- OCR 文字识别
  * Windows OCR (本地)
  * 腾讯云 OCR
  * AI Vision OCR

## 系统要求

- Windows 10/11 (64位)
- .NET 9 Desktop Runtime (x64)

如果启动时提示需要安装运行时，请访问：
https://dotnet.microsoft.com/download/dotnet/9.0

选择 ".NET Desktop Runtime 9.x.x - Windows x64 Installer" 下载安装。

## 开机自启

在"通用设置"中勾选"开机自动启动"

## 数据存储

所有数据存储在 Data 文件夹：

- 配置文件: Data\config.json
- 剪贴板数据库: Data\clipboard.db
- 剪贴板图片: Data\ClipboardImages\
- 日志文件: Data\Logs\
- 加密密钥: Data\secure.key

注意事项：
- config.json 中的 API Key 等敏感信息使用 secure.key 加密存储
- 备份或迁移时请保留整个 Data 文件夹
- 不要将 Data 文件夹分享给他人（包含加密的敏感信息）

## 翻译服务配置

支持以下翻译服务：
- Google 翻译（免费，无需配置）
- 腾讯云翻译（需配置 SecretId/SecretKey）
- OpenAI 兼容 API（支持任何兼容接口）

截图原位翻译模式：
- 快速模式: OCR + 规则过滤 + 翻译引擎
- 智能模式: OCR + AI 内容识别 + AI 翻译（需配置 AI 翻译）

---

项目地址: https://github.com/yourusername/STool
"@

# 使用 UTF-8 with BOM 写入，确保 Windows 记事本能正确显示中文
$utf8WithBom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText((Join-Path $publishDir "README.txt"), $readmeContent, $utf8WithBom)

# 打包成 zip
Write-Host "`n创建 ZIP 压缩包..." -ForegroundColor Yellow
$zipPath = Join-Path $releaseDir "STool_v${Version}_Portable.zip"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

# 显示构建结果
$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host "`n=== 构建完成 ===" -ForegroundColor Cyan
Write-Host "压缩包: $zipPath" -ForegroundColor Green
Write-Host "大小: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green

# 显示发布目录内容
Write-Host "`n发布目录内容:" -ForegroundColor Yellow
Get-ChildItem $publishDir | ForEach-Object {
    $size = if ($_.PSIsContainer) { "[目录]" } else { "($([math]::Round($_.Length / 1MB, 2)) MB)" }
    Write-Host "  $($_.Name) $size"
}

Write-Host "`n提示: 可以解压并运行 STool.exe 进行测试" -ForegroundColor Cyan
