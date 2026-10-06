STool v{{VERSION}} - 便携版
========================

## 快速开始

1. 解压到任意有写入权限的目录（不要放在 Program Files 下，也不要直接在压缩包内运行）
2. 双击运行 STool.exe
3. 右键点击托盘图标进行设置

## 主要功能

- 截图工具 (Alt+1)
  * 标注：矩形、椭圆、箭头、画笔、文字、马赛克，可选颜色与粗细
  * 放大镜取色、方向键微调选区、Ctrl+S 保存
  * 支持原位翻译（快速模式 / 智能模式）
- 翻译工具 (Alt+2)
- 剪贴板历史 (Alt+3)
  * ↑↓ 选择、Enter 粘贴、Esc 关闭
  * 跳过密码管理器标记为隐私的内容，可排除指定应用或暂停记录
- 局域网文件传输 (Alt+4)
  * Android 手机通过浏览器扫码连接
  * 支持文件与文件夹、分块上传和断点续传
- 设置面板 (Alt+5)
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

项目地址: https://github.com/kabuda2077/STool
