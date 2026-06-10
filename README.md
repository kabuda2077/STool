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

- **截图** (Alt+1) - 框选截图
- **翻译** (Alt+2) - 谷歌/腾讯翻译
- **剪贴板历史** (Alt+3) - 自动保存复制记录
- **OCR** (计划中)

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

## 开发规范

### Git 工作流
```bash
# 实验性改动用分支
git checkout -b experiment/新功能

# 失败直接删分支（最干净的回退）
git branch -D experiment/新功能

# 成功合并到 main
git merge experiment/新功能
```

### 提交规范
```
类型: 简短描述

类型: feat, fix, refactor, ui, docs, build, chore
```

### 设计系统
- **颜色**: 用 `PrimaryBrush` / `TextPrimaryBrush` 等语义常量
- **字体**: 用 `FontSizeTitle` (18) / `Body` (13) / `Caption` (12)
- **阴影**: 用 `StandardShadow`（统一阴影）
- **窗口**: 继承 `ModernWindow`，预留顶部 44px

详见：`CLAUDE.md`

## 配置与数据

- 配置: `%APPDATA%\STool\appsettings.json`
- 数据库: `%APPDATA%\STool\clipboard.db`
- 日志: `%APPDATA%\STool\Logs\`

## 仓库

https://github.com/kabuda2077/STool

---

**开发者**: 详细说明见 `CLAUDE.md`
