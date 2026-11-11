# STool 开发指南

> 修改代码前先读相关实现，保持现有风格和边界。本文件纳入版本库；本地的 `AGENTS.md` 只是指向这里的入口。

## 项目身份

**STool** 是一个 Windows 托盘效率工具：截图与标注、OCR、翻译、剪贴板历史、局域网文件传输。

- 技术栈：.NET 10 + WPF + Windows Forms interop
- 开发环境：.NET 10 SDK；运行发布包需要 .NET 10 Desktop Runtime（x64）
- 仓库：https://github.com/kabuda2077/STool，默认分支 `main`
- 版本号唯一来源：`Directory.Build.props` 中的 `<Version>`
- 解决方案：`STool.sln` 同时包含主程序与测试项目；`Directory.Build.targets` 移除 WinForms 自动注入的重名 using
- 依赖版本：`Directory.Packages.props`（集中管理，全部固定到确切版本）

## 常用命令

```powershell
dotnet build
dotnet test .\Tests\STool.Tests.csproj
Start-Process .\artifacts\bin\Debug\net10.0-windows10.0.26100.0\STool.exe
.\build-portable.ps1                 # 先跑测试，再按 Directory.Build.props 的版本打包
.\build-portable.ps1 -Version 1.6.0  # 指定版本（写入 exe 和压缩包名）
Get-Content .\artifacts\bin\Debug\net10.0-windows10.0.26100.0\Data\Logs\app*.log
```

构建提示 `STool.exe` 被占用时先结束进程：`Get-Process STool -ErrorAction SilentlyContinue | Stop-Process -Force`。

产物统一在仓库根目录 `artifacts/`：主项目沿用 `artifacts\bin\<Configuration>\...`，测试项目在 `artifacts\bin\STool.Tests\...`。

CI（`.github/workflows/ci.yml`）在 windows-latest 上构建并运行测试；推送 `v*` 标签时自动打包并创建草稿 Release。

集成测试会在本机随机端口建立 HttpListener。监听失败必须报告为失败，而不是直接返回导致假通过；若账户没有 HTTP 监听权限，请使用具有相应权限的测试环境，不要跳过断言。

局域网服务仍使用 HTTP。Host/Origin 校验、一次性配对码、会话 IP 绑定及过期检查不能替代 TLS；只在可信局域网使用。

## 项目结构

```text
App.xaml / App.xaml.cs        应用入口、单实例、全局异常兜底、启动失败提示
Core/
  AppBootstrap.cs             应用外壳：创建服务、托盘、热键，实现 IAppShell
  ConfigManager.cs            配置读写（快照 + 原子写入 + 备份恢复）
  SecureStorage.cs            AES-GCM 便携加密（TryDecrypt 区分"为空"和"解不开"）
  OpenAiChatClient.cs         OpenAI 兼容接口：翻译、AI OCR、智能截图翻译共用
  TencentCloudSigner.cs       腾讯云 TC3 签名，OCR 与机器翻译共用
  ClipboardWriter.cs          所有剪贴板写入（被占用时自动重试）
  ForegroundPaste.cs          切回原窗口并模拟 Ctrl+V
  TextScript / LanguageCodes  文字系统判断与语言代码映射
  AppLogging / MemoryDiagnostics  日志与诊断（诊断开关在通用设置）
Models/AppConfig.cs           配置模型
Modules/
  Clipboard/                  监听（UI 线程只做轻量检查，专属 STA 线程读取）→ 管理器（后台查重、编码、入库）→ SQLite 存储 → 面板
  Ocr/                        OCR 管理器（「OCR 识别」与截图翻译两条入口）与各服务
  Screenshot/                 截图窗口（partial 按职责拆分）、ScreenshotTextLayout（版面分析）、
                              ScreenshotTranslationPipeline（截图翻译流程）、ScreenshotTranslationRenderer（擦除与样式），均可单测；标注
  Translation/                翻译管理器、各服务（单条 + 批量）、截图翻译智能模式选择器、翻译面板
  LanTransfer/                HTTP 服务（partial：核心/接口/发送任务/HTTP 辅助）、认证、上传、打包、手机网页；
                              LanTransferController（服务生命周期、地址、修复判断，不依赖 WPF），窗口只做界面
Views/Settings/               通用、剪贴板、服务账号、OCR、翻译设置页；AiServiceSettingsSection 为 AI 账号字段组；SecurePasswordField 为密钥输入框
Styles/                       颜色、字体、图标、按钮、输入、窗口、导航等资源
Tests/                        xUnit 单元测试
docs/                         用户文档、手工回归清单、便携包 README 模板
```

## 核心约定

- **依赖通过构造函数传入。** 窗口和设置页从 `AppBootstrap` 拿到需要的服务（`CaptureOverlayServices`、`IAppShell` 等），不要再通过 `Application.Current` 反查服务。
- **剪贴板写入统一走 `ClipboardWriter`**，自动粘贴统一走 `ForegroundPaste`。
- **快捷键**：`RegisterHotKey` 带 `MOD_NOREPEAT`；回调在 WM_HOTKEY 返回后以 Input 优先级执行，可以直接创建窗口。
- **AI 接口统一走 `OpenAiChatClient`**：它负责地址候选、推理模型参数兼容（`max_completion_tokens`、temperature）、按平台关闭思考（`DisableReasoning`）、JSON 输出（`JsonResponse`）、不支持参数时自动去掉重试，以及截断/拦截/拒答识别。指令放 system 消息，用户文本放 user 消息。
- **账号只在 `AppConfig.Accounts` 保存一份**（腾讯云、AI 文本模型、AI 视觉模型），各功能引用。配置结构变化时提升 `AppConfig.CurrentVersion`，在 `ConfigManager` 里按版本写一次性迁移，不要每次启动都跑。
- **截图翻译**：① 识别（`OcrManager.RecognizeForLayoutAsync`，只用能返回位置的腾讯云/本地）→ ② 挑选（智能：`ScreenContentSelector`；快速：本地规则）→ ③ 翻译（`ITranslationService.TranslateBatchAsync`）。降级：智能 → 快速 → 整段翻译；不跨翻译服务兜底。
- **不要拿界面文案当状态值。** 状态用枚举或常量（如 `ArchiveProgressStage`、`ArchiveStatusNames`），Toast 去重用 `dedupeKey`。
- **UI 线程只做界面相关的事。** 文件 IO、编码、数据库查询放到后台，结果回到 UI 线程再赋值。
- 纯逻辑尽量放进不依赖窗口的类里并补单元测试（参考 `ScreenshotTextLayout`、`ClipboardPrivacy`）。
- 不要把临时构建目录、发布目录、`.claude/`、`.codex/` 等本地文件提交到仓库。

## 设计系统

颜色、Brush、阴影、字号、字重、按钮、输入框、导航、卡片等样式统一定义在 `Styles/`，页面和 C# 代码里不要硬编码。需要新增颜色、阴影、字体档位或控件样式时，先说明用途以及现有资源为何不能满足。

- 颜色：`Styles/Colors.xaml`。常用 `PrimaryBrush`、`PrimarySoftBrush`、`OnPrimaryBrush`、`SurfaceBrush`、`SurfaceAltBrush`、`BorderBrush`、`TextPrimaryBrush`、`TextSecondaryBrush`、`SuccessBrush`、`ErrorBrush`、`TransparentBrush`。截图标注调色板（`Annotation*Color/Brush`）只用于标注。
- 阴影只有三档：`PaneShadow`（最轻）、`StandardShadow`（普通浮起）、`MenuShadow`（菜单、浮层、钉图）。不要新建局部 `DropShadowEffect`。
- 字体：`Styles/Typography.xaml` 中的 `FontSizeTitle/Subtitle/Body/Caption/Content/Hint` 与 `FontWeightNormal/Strong`。
- 组件：按钮 `ModernButton / PrimaryButton / SecondaryButton / IconButton / GhostButton / DangerIconButton`；输入 `ModernTextBox / SunkenTextBox / SunkenPasswordBox / SunkenComboBox`；导航 `NavigationButton`；窗口 `ModernWindow`（内容区预留顶部约 44px）；卡片 `SurfaceCard`；折叠 `SettingsExpander`。
- 悬浮态默认 `PrimarySoftBrush`，主操作 `PrimaryBrush`，危险/关闭 `ErrorBrush`。分类选中使用浅蓝色，避免与主操作争夺注意。设置页共用 `SettingsLayout` 的工厂方法。
- 控件正文 13 DIP、阅读编辑内容 14 DIP、辅助文字 12 DIP；辅助文字使用 `TextSecondaryBrush`，不再叠加透明度。容器圆角使用 `CornerRadiusSmall/Medium/Large`。
- 设置保存反馈通过 `SettingsFeedback` 冒泡到窗口内的状态区；账号测试结果留在账号分组，字段变化后失效。错误可以同时用 Toast 提醒。
- 手机网页的 CSS 变量对应桌面颜色资源。新增交互应维护键盘焦点与可访问名称；轮询只更新变化内容，避免重建聚焦中的控件。
- 浏览器交互检查：使用已安装的 Playwright 和 Chrome，设置 `STOOL_PLAYWRIGHT_MODULE` 为 Playwright 包路径后运行 `node --test scripts/Test-LanTransferWeb.cjs`。可设置 `STOOL_UI_ARTIFACTS` 输出截图。该脚本拦截所有请求并使用示例数据，不启动实际传输服务。
- 本轮设计变更与验证边界见 [design-review.md](design-review.md)。

## 数据位置

全部位于 exe 同级的 `Data\`（`Core/AppPaths.cs`），不写 `%APPDATA%`。启动时会实际写入一次探测文件，目录不可写时提示用户并退出。

```text
Data\config.json                配置（含 Diagnostics、Clipboard.ExcludedApps 等）
Data\clipboard.db               剪贴板数据库（WAL 模式；损坏时尝试备份并停用剪贴板模块，保留原库及图片）
Data\ClipboardImages\           剪贴板图片
Data\ClipboardThumbnails\       缩略图缓存
Data\Logs\app<yyyyMMdd>.log     日志，按天滚动保留 7 份，级别见 Diagnostics.LogLevel
Data\secure.key                 AES-GCM 主密钥（隐藏文件，随程序一起迁移）
Data\lan-devices.json           局域网"记住的设备"（只存令牌哈希）
Data\lan-transfer-history.json  传输历史
```

## 提交

提交前至少运行 `dotnet build` 和 `dotnet test .\Tests\STool.Tests.csproj`，UI 相关改动再按 `docs/manual-regression-checklist.md` 手工回归对应部分。

提交信息：`类型: 简短描述`，类型可用 `feat`、`fix`、`ui`、`perf`、`refactor`、`docs`、`build`、`chore`。
