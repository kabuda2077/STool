# Claude Context - STool Project

> 此文件为 AI 助手提供项目上下文，帮助快速理解代码库。

## 项目身份

**STool** - Windows 效率工具集（截图、翻译、剪贴板、OCR）

- 技术栈: .NET 9.0 + WPF + Windows Forms
- 单人项目，主分支 `main` 始终保持可发布状态
- 仓库: https://github.com/kabuda2077/STool

## 快速命令

```bash
# 构建运行
dotnet build && dotnet run

# 发布便携版
.\build-portable.ps1 -Version "1.0.0"

# 查看日志
cat $env:APPDATA\STool\Logs\log-*.txt
```

## 项目结构速查

```
Core/           托盘、快捷键管理
Modules/        功能模块（剪贴板、翻译、截图）
Views/          设置界面
Styles/         设计系统（颜色、字体、组件样式）
Services/       配置管理
Models/         数据模型
```

## 设计系统关键约定

### 颜色（Styles/Colors.xaml）
- `PrimaryBrush` (#0F6FFF) - 主色
- `TextPrimaryBrush` / `TextSecondaryBrush` - 文字
- `SurfaceBrush` (#FFF) / `SurfaceCanvasBrush` (#F2F4F7) - 表面
- `SurfaceAlt` - 悬停态 | `SurfaceSunken` - 输入框背景
- `BorderBrush` - 边框

### 字体（Styles/Typography.xaml）
```
FontSizeTitle    18  (页面标题)
FontSizeSubtitle 15  (区块标题)
FontSizeBody     13  (正文 - 默认)
FontSizeCaption  12  (辅助说明)
FontSizeMicro    11  (徽章、时间戳)

FontWeightNormal 400 | FontWeightStrong 600 SemiBold
```

### 阴影（4 档 Elevation）
```xml
<Border Effect="{StaticResource Elevation1}"/>  <!-- 卡片 -->
<Border Effect="{StaticResource Elevation2}"/>  <!-- FAB -->
<Border Effect="{StaticResource Elevation3}"/>  <!-- 对话框 -->
```

### 组件样式
- 按钮: `PrimaryButton` / `SecondaryButton` / `IconButton`
- 输入: `ModernTextBox` / `SunkenTextBox` / `SunkenComboBox`
- 窗口: `ModernWindow` (自定义标题栏，需预留顶部 44px)

## 代码规范

### 命名
- C# 类/方法: `PascalCase`
- 私有字段: `_camelCase`
- XAML 控件: `camelCase` (如 `btnSave`, `txtInput`)

### 提交规范
```
类型: 简短描述

类型: feat, fix, refactor, ui, docs, build, chore
```

### 分支策略
- `main` - 可发布状态，不直接实验
- `feature/*` / `fix/*` / `experiment/*` - 功能分支
- 实验失败直接删分支（最干净的回退）

详见：`GIT_WORKFLOW.md`

## 核心文件说明

### 应用入口
- `App.xaml.cs` - DI 容器配置，启动托盘

### 托盘与快捷键
- `Core/TrayManager.cs` - 系统托盘菜单
- `Core/HotkeyManager.cs` - 全局热键 (Alt+1/2/3)

### 功能模块
- `Modules/Clipboard/ClipboardPanel.xaml` - 剪贴板历史主界面
- `Modules/Clipboard/ClipboardStorage.cs` - SQLite 持久化
- `Modules/Translation/TranslationPanel.xaml` - 翻译面板
- `Modules/Screenshot/CaptureOverlay.xaml` - 截图蒙层

### 设置
- `Views/SettingsWindow.xaml` - 设置窗口（左侧导航栏 + 右侧面板）
- `Views/Settings/*SettingsPanel.cs` - 各设置面板（动态创建 UI）

### 配置
- `Services/AppConfig.cs` - 配置加载/保存
- 配置文件: `%APPDATA%\STool\appsettings.json`
- 数据库: `%APPDATA%\STool\clipboard.db`

## 开发常见任务

### 添加新 UI 组件样式
1. 在 `Styles/` 对应文件添加 `<Style>`
2. 使用语义颜色/字号常量（不要硬编码）
3. 在 `App.xaml` 的 MergedDictionaries 已自动加载

### 修改设计系统
- **改颜色**: 只修改 `Styles/Colors.xaml` 中的语义色定义
- **改字体**: 只修改 `Styles/Typography.xaml` 中的 FontSize*/FontWeight*
- 所有使用了这些资源的地方自动更新

### 添加配置项
1. `Models/` - 添加配置模型
2. `Services/AppConfig.cs` - 添加加载/保存逻辑
3. `Views/Settings/*SettingsPanel.cs` - 添加 UI 控件

### 调试
```bash
# 查看日志
cat $env:APPDATA\STool\Logs\log-20260610.txt

# 重置配置
rm $env:APPDATA\STool\appsettings.json

# 清空剪贴板数据
rm $env:APPDATA\STool\clipboard.db
```

## 重要约定

### UI 一致性
- **所有窗口**继承 `ModernWindow` 样式
- **顶部间距**必须预留 44px (`Margin="0,44,0,0"`)
- **颜色/字号**使用语义常量，不硬编码
- **阴影**使用 Elevation1/2/3，不自定义

### 代码质量
- **提交前必须构建成功** (`dotnet build`)
- **实验性改动在分支完成**，成功才合并到 main
- **提交信息清晰**，描述"做了什么"而非"怎么做"

### 数据安全
- 配置文件、数据库在 `%APPDATA%\STool\`
- 敏感配置（API Key）不提交到 Git
- 日志不记录用户隐私数据

## 待办事项（TODO）

### 功能
- [ ] OCR 文字识别
- [ ] 剪贴板搜索/过滤
- [ ] 翻译历史
- [ ] 划词翻译
- [ ] 截图标注

### 技术债务
- [ ] 添加单元测试
- [ ] 改进错误处理和日志
- [ ] 国际化（抽取资源文件）
- [ ] 配置项验证

## 已知问题

1. 剪贴板监听偶尔漏掉快速连续复制
2. 全屏应用下快捷键可能被拦截
3. 高 DPI 下截图尺寸需验证

## 最近重大变更

### 2026-06-10
- ✅ 统一阴影系统到 Elevation（4 档冷色调）
- ✅ 清理冗余代码（TextBoxWatermark、重复颜色、未使用样式）
- ✅ 标题栏显示窗口标题（剪贴板/翻译）
- ✅ 便携版构建脚本（build-portable.ps1）
- ✅ Git 工作流文档

## 参考文档

- **README.md** - 完整项目文档（给人类开发者）
- **GIT_WORKFLOW.md** - Git 分支策略和常见操作
- **DESIGN.md** - 设计规范（颜色、字体、组件）
- **SHORTCUTS.md** - 快捷键说明
- **TESTING_GUIDE.md** - 测试指南

---

**提示**: 修改代码前，先阅读 `README.md` 了解项目结构，查看相关文件再动手。遵循设计系统约定，保持代码风格一致。
