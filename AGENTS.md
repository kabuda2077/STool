# STool 开发指南

> AI 助手快速上下文。修改代码前先读相关实现，保持现有风格和边界。

## 项目身份

**STool** 是一个 Windows 效率工具，核心功能包括截图、OCR、翻译、剪贴板历史和系统托盘快捷入口。

- 技术栈: .NET 9 + WPF + Windows Forms interop
- 仓库: https://github.com/kabuda2077/STool
- 默认分支: `main`
- 构建产物: `artifacts/`

## 快速命令

```powershell
dotnet build
Start-Process .\artifacts\bin\Debug\net9.0-windows10.0.26100.0\STool.exe
.\build-portable.ps1 -Version "1.0.0"
Get-Content $env:APPDATA\STool\Logs\log-*.txt
```

如果构建提示 `STool.exe` 被占用，先关闭运行中的进程:

```powershell
Get-Process STool -ErrorAction SilentlyContinue | Stop-Process -Force
```

## 项目结构

```text
App.xaml / App.xaml.cs        应用入口、资源字典、依赖注入
Core/                         托盘、热键、Toast、确认弹窗、窗口辅助逻辑
Models/                       配置和业务模型
Modules/
  Clipboard/                  剪贴板历史、过滤、存储、面板
  Ocr/                        OCR 管理和结果窗口
  Screenshot/                 截图覆盖层、标注、钉图窗口
  Translation/                翻译面板、原位翻译、翻译管理
Styles/
  Colors.xaml                 颜色、Brush、阴影资源
  Typography.xaml             字号和字重
  Buttons.xaml                按钮样式
  Inputs.xaml                 输入框、下拉框、滚动条、复选框
  Navigation.xaml             设置页侧栏导航
  Windows.xaml                ModernWindow、设置卡片、通用窗口样式
Views/Settings/               通用、OCR、翻译设置页
Resources/                    图标等静态资源
```

## 设计系统

### 颜色和 Brush

颜色统一定义在 `Styles/Colors.xaml`。界面代码不要硬编码颜色、命名颜色或 `Brushes.*`。

常用资源:

```text
PrimaryBrush          深蓝主题色
PrimarySoftBrush      浅蓝悬浮/选中背景
OnPrimaryBrush        深色主题背景上的文字/图标
SurfaceBrush          白色卡片/控件表面
SurfaceAltBrush       页面灰底/浅灰输入背景
BorderBrush           边框
TextPrimaryBrush      主文字
TextSecondaryBrush    次要文字
SuccessBrush          成功状态
ErrorBrush            危险/错误状态
TransparentBrush      透明背景
```

截图、蒙版、钉图关闭按钮、标注默认色等也在 `Colors.xaml` 中集中定义。

### 阴影

阴影只保留三档，全部在 `Styles/Colors.xaml`:

```text
PaneShadow        最轻，设置卡片、翻译文本面板
StandardShadow    普通浮起，截图工具条等
MenuShadow        更明显，右键菜单、下拉菜单、浮动操作条、钉图
```

不要新建局部 `DropShadowEffect`。如确实需要新增阴影档位，先说明用途和现有三档不能覆盖的原因。

### 字体

字号和字重统一定义在 `Styles/Typography.xaml`:

```text
FontSizeTitle
FontSizeSubtitle
FontSizeBody
FontSizeCaption
FontSizeMicro
FontWeightNormal
FontWeightStrong
```

普通文本、按钮、输入框、下拉项都应引用这些语义资源。图标字体的视觉尺寸可以按控件需要单独设置。

### 组件样式

优先复用已有样式:

```text
按钮: ModernButton / PrimaryButton / SecondaryButton / IconButton / GhostButton / DangerIconButton
输入: ModernTextBox / SunkenTextBox / SunkenPasswordBox / SunkenComboBox
导航: NavigationButton
窗口: ModernWindow
卡片: SurfaceCard
折叠分组: SettingsExpander
文本: SettingsPageTitle / SettingsGroupTitle / SectionLabel / HintText
```

`ModernWindow` 使用自定义标题栏，内容区通常需要预留顶部约 `44px`。

## 核心约定

- 后续功能增减、交互调整、样式变更，必须优先引用已有样式、资源和业务逻辑。
- 颜色、Brush、阴影、字号、字重、按钮、输入框、导航、卡片等样式不得在页面或 C# 里随手硬编码。
- 如果需要增加新的颜色、阴影、字体档位、控件样式或业务抽象，必须特别说明新增原因、适用范围，以及为什么现有资源不能满足。
- UI 悬浮态默认使用 `PrimarySoftBrush`，主操作按钮默认使用 `PrimaryBrush`，危险/关闭动作使用 `ErrorBrush`。
- 设置页三栏内容共用 `SurfaceCard` 和 `NavigationButton`，不要在单个设置页里复制卡片/导航样式。
- 剪贴板、截图、翻译、OCR 的业务逻辑优先复用各自模块内已有 Manager/Storage/Panel 结构。
- 不要把临时构建目录、发布目录、`.Codex/` 等本地文件提交到仓库。

## 功能现状

### 截图

- 支持全屏冻结、窗口识别、区域选择、工具条、保存、复制、钉图。
- 支持矩形、椭圆、箭头、画笔标注和撤销/重做。
- 截图翻译采用原位覆盖效果，而不是弹出独立结果窗口。
- 钉图窗口有圆形关闭按钮和浮层阴影。

### 翻译

- 翻译面板为上下文本区布局，结果区略高于输入区。
- 引擎切换为统一胶囊按钮。
- 语言切换为统一胶囊，下拉菜单带 `MenuShadow`。
- 支持复制、复制并隐藏、复制并输入。

### 剪贴板

- 支持文本、图片等历史记录、分类清空、右键菜单和确认弹窗。
- 会过滤微信等应用产生的空白/透明/近白图片内容。
- 清空按钮在分类页只清当前分类，在“全部”页清空全部。

### 设置

- 设置窗口包含通用、OCR、翻译三页。
- API Key/Secret Key 字段支持眼睛按钮显示/隐藏。
- 保存按钮使用主题深蓝色。
- 左侧选中项使用浅蓝背景和深蓝端点。

## 数据位置

```text
配置: %APPDATA%\STool\appsettings.json
剪贴板数据库: %APPDATA%\STool\clipboard.db
日志: %APPDATA%\STool\Logs\
```

## Git 和构建

提交前至少运行:

```powershell
dotnet build
```

提交信息建议:

```text
类型: 简短描述

类型可用: feat, fix, ui, refactor, docs, build, chore
```

## 最近维护记录

- 统一主题色为图标一致的蓝色: `PrimaryBrush` / `PrimarySoftBrush`
- 样式颜色和 Brush 收敛到 `Styles/Colors.xaml`
- 阴影系统收敛为 `PaneShadow` / `StandardShadow` / `MenuShadow`
- 设置页、翻译页、剪贴板菜单、截图钉图等界面更新为更灰、更极简的风格
- 构建输出统一到 `artifacts/`
