# STool 开发指南

STool 是 Windows 托盘工具，提供截图、OCR、翻译、剪贴板历史和局域网文件传输。技术栈为 .NET 10、WPF 和 Windows Forms 互操作。修改前先读相关实现，保持现有风格和职责边界。

## 构建与验证

在 Windows 上使用 .NET 10 SDK 和 PowerShell 7（`pwsh`）：

```powershell
dotnet build
dotnet test .\Tests\STool.Tests.csproj
.\build-portable.ps1  # 运行 Release 测试、发布并校验便携包
```

- 版本号维护在 `Directory.Build.props`，依赖版本维护在 `Directory.Packages.props`。打包脚本的 `-Version` 只覆盖本次构建，不修改版本文件。
- 产物位于 `artifacts/`：主程序在 `bin/<配置>/<目标框架>/`，测试在 `bin/STool.Tests/`，发布文件和压缩包分别在 `publish/<variant>/<build-id>/`、`releases/<variant>/<build-id>/`。便携包需要 .NET 10 Desktop Runtime（x64）。
- 提交前运行构建和测试；同一代码状态已通过的检查无需重复。打包时若相应测试已通过，可用 `-SkipTests`。UI 改动按 [手工回归清单](manual-regression-checklist.md) 检查相关部分。
- 手机网页改动运行 `node --test scripts/Test-LanTransferWeb.cjs`，需要已安装的 Playwright 和 Chrome；可用 `STOOL_PLAYWRIGHT_MODULE` 指定模块路径、`STOOL_UI_ARTIFACTS` 指定截图目录。测试拦截请求，不启动传输服务。
- 集成测试需要本机 HTTP 监听权限；监听失败应报告失败，不能静默跳过。构建遇到文件占用时，先核对并退出占用该产物的进程。

## 代码入口

| 目录 | 职责 |
|---|---|
| `App.xaml` / `App.xaml.cs` | 应用入口、单实例和全局异常处理 |
| `Core/` | 应用组装、配置、热键、系统交互和公共服务 |
| `Modules/` | `Screenshot`、`Ocr`、`Translation`、`Clipboard`、`LanTransfer` 功能模块 |
| `Views/` | 设置窗口及各设置页 |
| `Models/` | 配置等数据模型 |
| `Styles/` | 共用视觉资源与控件样式 |
| `Tests/` / `scripts/` | 自动测试和构建校验脚本 |

## 实现约定

- 服务通过构造函数传入，由 `AppBootstrap` 组装；不要通过 `Application.Current` 反查服务。
- 剪贴板写入走 `ClipboardWriter`，切回原窗口并粘贴走 `ForegroundPaste`，AI 接口走 `OpenAiChatClient`。复用这些入口的兼容与重试逻辑。
- UI 线程只处理界面；文件 IO、编码和数据库查询放到后台。纯逻辑尽量独立于窗口，新增非平凡逻辑补充有针对性的测试。
- 热键注册带 `MOD_NOREPEAT`，在 `WM_HOTKEY` 返回后通过 Dispatcher 的 Input 优先级执行回调。
- 状态使用枚举或常量，不用界面文案作为状态值；Toast 去重使用 `dedupeKey`。
- 账号统一保存在 `AppConfig.Accounts`。配置结构变更时提升 `AppConfig.CurrentVersion`，在 `ConfigManager` 做一次性迁移。
- 截图翻译按识别、选段、翻译分阶段处理；选段可从智能降级到快速、再到整段，不跨翻译服务兜底。
- 局域网传输使用 HTTP，仅用于可信局域网；保留 Host/Origin、配对码、会话 IP 和过期校验。

## 界面约定

UI/UX 设计必须遵循：

> Avoid unnecessary secondary labels and descriptions. Let the layout do the explaining.

- 颜色、阴影、字号、字重及控件样式统一维护在 `Styles/`，页面和 C# 代码引用资源。新增资源前先检查能否复用，并说明新增用途。
- 颜色见 `Colors.xaml`，字体见 `Typography.xaml`，按钮、输入和窗口样式分别见 `Buttons.xaml`、`Inputs.xaml`、`Windows.xaml`。截图标注的 `Annotation*` 颜色仅用于标注。
- 阴影复用 `PaneShadow`、`StandardShadow`、`MenuShadow`；普通工具内容面板不加阴影，菜单和浮层按需使用。不要新建局部 `DropShadowEffect`。
- 保持紧凑的视觉密度。控件文字默认 12 DIP，阅读编辑内容按模块使用已有字号资源；翻译正文与占位提示为 12 DIP。全局字号、间距、颜色变更需核对实际界面。
- 操作成组并对齐。快捷键说明放 ToolTip；错误、过期和等待反馈按状态显示。分段选择保持蓝底白字，悬浮态使用 `PrimarySoftBrush`。
- 设置页复用 `SettingsLayout`；保存反馈通过 `SettingsFeedback` 汇入窗口状态区。账号测试结果留在账号分组，字段变化后失效。
- 手机网页 CSS 变量对应桌面颜色资源。保留键盘焦点和可访问名称；轮询只更新变化内容，避免重建聚焦控件。

## 数据与提交

数据位于可执行文件同级的 `Data/`，路径统一由 `Core/AppPaths.cs` 提供。目录必须可写；配置、数据库、图片、密钥和历史记录均随此目录保存，替换程序时保留它。

不要提交构建产物、运行数据和本地代理工具目录。提交信息使用 `类型: 简短描述`，如 `fix: 修复语言选择文字位移`。CI 配置见 [ci.yml](../.github/workflows/ci.yml)。
