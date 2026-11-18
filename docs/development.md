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
- 账号默认按文字识别和翻译用途独立保存；显式复用只关联凭据，模型保持独立。运行服务与缓存签名使用 `Accounts.Resolve*` 得到的有效账号，不直接读取来源草稿。
- 截图翻译按识别、选段、翻译分阶段处理；选段可从 AI 模式降级到标准、再到整段，不跨翻译服务兜底。识别跟随文字识别服务，AI 改用 Windows 本地，腾讯云失败始终回退本地。
- 配置 v3 拆分腾讯云账号并保留旧 AI 关联。旧配置原本本地截图识别、独立 OCR 为腾讯云时，通过 `ScreenshotOcrMigrationPending` 保持本地，直到用户点击「跟随文字识别」或实际更改 OCR 服务。
- 剪贴板图片按重新编码后的 PNG 大小限制为内部常量 10MB；保留像素体积、队列和有界编码输出保护，不恢复旧图片大小用户选项。
- 剪贴板仅按条数上限清理最旧的非收藏记录，默认 1000 条，收藏不计入上限。配置 v4 移除按时间清理的字段与机制；旧 `retentionDays` 忽略且保存后不再写出，既有条数上限保持。记录开关与历史记录上限放在同一列表。
- 局域网传输使用 HTTP，仅用于可信局域网；保留 Host/Origin、配对码、会话 IP 和过期校验。

## 界面约定

设置页采用已确认的 OpenDesign Cloud `stool-magpie.html` 适配设计，使用 C# / WPF 实现。2026-11-18 用户要求完整复刻：设置页的颜色、字体、阴影、焦点与运动规则发生冲突时，以最新 demo 为准。实际实现、配置迁移和验证状态见 [设置界面设计决定与落地清单](settings-design-decisions.md)。后续原型变动仍需明确落地指令。

UI/UX 设计必须遵循：

> Avoid unnecessary secondary labels and descriptions. Let the layout do the explaining.

- 颜色、阴影、字号、字重及控件样式统一维护在 `Styles/`，页面和 C# 代码引用资源。新增资源前先检查能否复用，并说明新增用途。
- 颜色见 `Colors.xaml`，字体见 `Typography.xaml`，按钮、输入和窗口样式分别见 `Buttons.xaml`、`Inputs.xaml`、`Windows.xaml`。截图标注的 `Annotation*` 颜色仅用于标注。
- `Settings.xaml` 是设置页专用资源：660×560 DIP、最小 600×480、54 DIP 顶栏、52 DIP 基础行高、白色分组和浅灰字段按钮。跳转入口默认透明，悬浮时显示浅灰背景并加深文字，复用 `SettingsActionButton`。设置正文 13 DIP、字段 12.5 DIP、账号摘要 11.5 DIP、提示 11 DIP；统一 12 DIP 方向箭头。正文字体先用 Segoe UI，再用 Microsoft YaHei UI，避免 WPF 中文回退到日文或繁体字体；下拉与模型跳转使用 Microsoft YaHei UI。不修改其他工具的默认字号。
- Magpie 的视觉设计参考在 README 简短致谢，不附带独立声明文件、不内嵌声明到 EXE，也不为声明增加打包校验。
- 设置页的滑块、开关、菜单和提示使用 `Settings.xaml` 内对应 demo 的共用阴影；页面代码不新建局部 `DropShadowEffect`。其他工具继续复用 `PaneShadow`、`StandardShadow`、`MenuShadow`，普通内容面板不加阴影。
- 保持紧凑的视觉密度。控件文字默认 12 DIP，阅读编辑内容按模块使用已有字号资源；翻译正文与占位提示为 12 DIP。全局字号、间距、颜色变更需核对实际界面。
- 操作成组并对齐。快捷键说明放 ToolTip；错误、过期和等待反馈按状态显示。设置页顶部用带阴影的白色滑块，位置和真实宽度过渡为 220ms；其他工具的分段选择保持蓝底白字和 `PrimarySoftBrush` 悬浮态。
- 设置页复用 `SettingsLayout`；保存反馈通过 `SettingsFeedback` 汇入窗口状态区。账号测试结果留在账号分组，字段变化后失效。
- 设置提示仅保留名称无法说明的规则和图标操作含义，悬停和键盘焦点只在对应控件触发，设置名称与整行空白不触发。模型显示名不同于原始 ID 或文字截断时补充全文；应用名和账号摘要只在截断时提示。接口地址仅在请求地址被补全或转换时提示，错误直接显示。缺失账号、迁移操作和错误保留可见入口；隐藏滚动条但保留滚动。账号详情同时只展开一个，未保存失败时保留草稿、重试和放弃操作。
- 设置普通分隔线贯通白色列表内部，账号详情内部分隔线按表单左右内缩 14 DIP。弹出菜单的方向键只浏览，Enter／点击才提交，Esc 关闭并恢复入口焦点；账号折叠或离页立即取消未完成请求并恢复按钮。
- 设置视觉验证读取最新 demo 的浏览器实测结果，以同窗口尺寸、配置及展开／滚动状态对照原生截图。`SettingsVisualChecks` 检查五页两种窗口尺寸、动态行、账号复用、分隔线、独立浮层和动画中间帧；设置 `STOOL_UI_ARTIFACTS` 保存截图。功能测试通过不能代替截图核对。
- 手机网页 CSS 变量对应桌面颜色资源。保留键盘焦点和可访问名称；轮询只更新变化内容，避免重建聚焦控件。

## 数据与提交

数据位于可执行文件同级的 `Data/`，路径统一由 `Core/AppPaths.cs` 提供。目录必须可写；配置、数据库、图片、密钥和历史记录均随此目录保存，替换程序时保留它。

不要提交构建产物、运行数据和本地代理工具目录。提交信息使用 `类型: 简短描述`，如 `fix: 修复语言选择文字位移`。CI 配置见 [ci.yml](../.github/workflows/ci.yml)。
