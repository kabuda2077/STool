# STool

Windows 效率工具 - 截图、翻译、剪贴板历史、OCR

## 快速开始

```bash
# 构建运行
dotnet build && dotnet run

# 发布便携版
.\build-portable.ps1 -Version "1.0.0"
```

## 功能

- **截图** (Alt+1) - 框选截图，支持原位翻译
- **翻译** (Alt+2) - 选中文本后按快捷键翻译
- **剪贴板历史** (Alt+3) - 自动保存复制记录
- **设置** (Alt+4) - 打开设置面板

## 技术栈

- .NET 9.0 + WPF + Windows Forms
- SQLite (剪贴板存储)
- Serilog (日志)

## 项目结构

```
Core/           托盘、快捷键
Modules/        功能模块
Views/          设置界面
Styles/         设计系统
```

## 配置与数据

便携版数据存储在程序目录的 `Data\` 文件夹：

- 配置: `Data\config.json`
- 数据库: `Data\clipboard.db`
- 日志: `Data\Logs\`
- 加密密钥: `Data\secure.key`
