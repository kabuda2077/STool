# STool 项目文档

## 项目概述

STool 是一个 Windows 效率工具，提供截图、翻译、剪贴板历史和 OCR 功能。

- **技术栈**: .NET 9.0 + WPF + Windows Forms
- **开发环境**: Windows 10/11, Visual Studio 2022 / Rider
- **仓库**: https://github.com/kabuda2077/STool

---

## 快速开始

### 环境要求
- Windows 10/11 (64位)
- .NET 9.0 SDK
- Visual Studio 2022 或 JetBrains Rider

### 构建运行
```bash
# 克隆仓库
git clone https://github.com/kabuda2077/STool.git
cd STool

# 还原依赖并构建
dotnet restore
dotnet build

# 运行
dotnet run

# 或直接运行 exe
.\bin\Debug\net9.0-windows10.0.26100.0\STool.exe
```

### 发布便携版
```powershell
.\build-portable.ps1 -Version "1.0.0"
# 生成: releases/STool_v1.0.0_Portable.zip (约 80 MB)
```

---

## 项目结构

```
STool/
├── App.xaml/App.xaml.cs          # 应用入口，DI 配置
├── Core/                          # 核心基础设施
│   ├── HotkeyManager.cs          # 全局快捷键管理
│   └── TrayManager.cs            # 系统托盘管理
├── Modules/                       # 功能模块（独立面板）
│   ├── Clipboard/                 # 剪贴板历史
│   │   ├── ClipboardPanel.xaml   # 主界面
│   │   ├── ClipboardMonitor.cs   # 剪贴板监听
│   │   └── ClipboardStorage.cs   # SQLite 存储
│   ├── Screenshot/                # 截图工具
│   │   └── CaptureOverlay.xaml   # 截图蒙层
│   └── Translation/               # 翻译工具
│       ├── TranslationPanel.xaml # 翻译面板
│       ├── GoogleTranslationService.cs
│       └── TencentTranslationService.cs
├── Views/                         # 通用视图
│   ├── SettingsWindow.xaml       # 设置窗口
│   └── Settings/                  # 设置面板
│       ├── GeneralSettingsPanel.cs
│       ├── TranslationSettingsPanel.cs
│       └── OcrSettingsPanel.cs
├── Styles/                        # 全局样式
│   ├── Colors.xaml               # 颜色系统
│   ├── Typography.xaml           # 字体系统
│   ├── Buttons.xaml              # 按钮样式
│   ├── Inputs.xaml               # 输入框样式
│   └── Windows.xaml              # 窗口样式（ModernWindow）
├── Services/                      # 业务服务
│   └── AppConfig.cs              # 配置管理
├── Models/                        # 数据模型
│   ├── ClipboardItem.cs
│   ├── TranslationProvider.cs
│   └── HotkeyConfig.cs
└── Resources/                     # 资源文件
    └── STool.ico                 # 应用图标
```

---

## 核心功能说明

### 1. 系统托盘 (TrayManager)
- 驻留系统托盘，提供快捷菜单
- 管理各功能面板的显示/隐藏
- **位置**: `Core/TrayManager.cs`

### 2. 全局快捷键 (HotkeyManager)
- 默认快捷键：
  - Alt+1: 截图
  - Alt+2: 翻译
  - Alt+3: 剪贴板历史
- 使用 Win32 API 注册全局热键
- **位置**: `Core/HotkeyManager.cs`

### 3. 剪贴板历史 (ClipboardPanel)
- 监听系统剪贴板变化（WPF ClipboardMonitor）
- 支持文本、图片、文件路径
- SQLite 持久化存储
- 来源应用检测（通过前台窗口）
- **数据库**: `%APPDATA%\STool\clipboard.db`
- **位置**: `Modules/Clipboard/`

### 4. 翻译工具 (TranslationPanel)
- 支持谷歌翻译、腾讯云翻译、AI 翻译
- 语言自动检测
- 结果可复制、划词翻译（计划中）
- **位置**: `Modules/Translation/`

### 5. 截图工具 (CaptureOverlay)
- 全屏蒙层，框选区域截图
- 支持保存文件或复制到剪贴板
- **位置**: `Modules/Screenshot/`

### 6. OCR（计划中）
- 文字识别功能
- **位置**: 待实现

---

## 设计系统

### 颜色系统 (Styles/Colors.xaml)

**语义颜色**（优先使用）：
- `PrimaryBrush` (#0F6FFF) - 主色（按钮、链接、选中态）
- `PrimarySubtleBrush` (#E6F0FF) - 主色淡背景
- `TextPrimaryBrush` (#1C1E21) - 主文字
- `TextSecondaryBrush` (#60656C) - 次要文字
- `SurfaceBrush` (#FFFFFF) - 卡片/面板背景
- `SurfaceCanvasBrush` (#F2F4F7) - 页面底色
- `BorderBrush` (#D9E0EA) - 边框

**特殊用途**：
- `SurfaceAlt` - 悬停/交互态背景
- `SurfaceSunken` - 内凹/输入框背景
- `DangerBrush` (#E8384F) - 危险操作

### 字体系统 (Styles/Typography.xaml)

**5 级字号阶梯**：
```
Title    18px  FontWeightStrong  页面标题
Subtitle 15px  FontWeightStrong  区块标题
Body     13px  FontWeightNormal  正文（默认）
Caption  12px  FontWeightNormal  辅助说明
Micro    11px  FontWeightNormal  时间戳、徽章
```

**字重**：
- `FontWeightNormal` (400) - 正文
- `FontWeightStrong` (600 SemiBold) - 标题、强调

### 阴影系统 (Styles/Colors.xaml)

**4 档 Elevation**（冷色调、向下投影）：
```
Elevation0  无阴影
Elevation1  悬浮卡片 (Blur 8, Offset Y+2, #222 @ 4%)
Elevation2  浮动按钮 (Blur 12, Y+4, 6%)
Elevation3  对话框/菜单 (Blur 24, Y+8, 12%)
```

**使用**：
```xml
<Border Effect="{StaticResource Elevation1}">
```

### 按钮样式 (Styles/Buttons.xaml)

- `PrimaryButton` - 主操作按钮（蓝底白字）
- `SecondaryButton` - 次要按钮（灰底深字）
- `GhostButton` - 幽灵按钮（透明底，悬停显示）
- `IconButton` - 图标按钮（圆形，无边框）
- `ModernButton` - 别名（指向 PrimaryButton，语义化命名）

### 输入框样式 (Styles/Inputs.xaml)

- `ModernTextBox` - 单边框输入框
- `SunkenTextBox` - 无边框灰底输入框
- `SunkenComboBox` - 无边框灰底下拉框

### 窗口样式 (Styles/Windows.xaml)

- `ModernWindow` - 主窗口样式
  - 隐藏默认标题栏，自定义窗口控制按钮
  - 支持拖拽、最小化/最大化/关闭
  - 内容区铺满窗口，标题栏为透明浮层（Height=44）
  - 各页面需自行预留顶部 44px (Margin="0,44,0,0")

---

## 配置管理

### 配置文件位置
- **路径**: `%APPDATA%\STool\appsettings.json`
- **日志**: `%APPDATA%\STool\Logs\`
- **数据库**: `%APPDATA%\STool\clipboard.db`

### 配置结构
```json
{
  "General": {
    "AutoStart": false,
    "Language": "zh-CN"
  },
  "Hotkeys": {
    "Screenshot": "Alt+1",
    "Translation": "Alt+2",
    "Clipboard": "Alt+3"
  },
  "Translation": {
    "Provider": "Google",
    "TencentSecretId": "",
    "TencentSecretKey": "",
    "SourceLanguage": "auto",
    "TargetLanguage": "zh"
  },
  "OCR": {
    "Provider": "Local",
    "ApiKey": ""
  }
}
```

### 配置类
- **位置**: `Services/AppConfig.cs`
- **加载**: 应用启动时自动加载
- **保存**: 设置页面点击"保存设置"

---

## 依赖库

```xml
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.*" />
<PackageReference Include="Serilog.Sinks.File" Version="6.0.*" />
<PackageReference Include="System.Drawing.Common" Version="9.0.*" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.*" />
```

- **DI 容器**: Microsoft.Extensions.DependencyInjection
- **日志**: Serilog (写入文件)
- **数据库**: SQLite (剪贴板历史)
- **图像处理**: System.Drawing.Common

---

## 开发规范

### 代码风格
- C# 命名：PascalCase (类、方法、属性)
- 私有字段：_camelCase
- XAML 控件命名：camelCase (如 `btnSave`, `txtInput`)
- 使用 `var` 进行类型推断（除非类型不明显）

### Git 工作流
- **主分支**: `main` (始终可发布)
- **功能分支**: `feature/功能名`, `fix/bug名`, `experiment/实验名`
- **提交规范**: `类型: 简短描述`
  - 类型: feat, fix, refactor, ui, docs, build, chore

详见：`GIT_WORKFLOW.md`

### 提交前检查
```bash
# 构建验证
dotnet build

# 运行测试（如有）
dotnet test
```

---

## 常见开发任务

### 添加新功能模块

1. 在 `Modules/` 下创建新文件夹
2. 创建主窗口 XAML (继承 ModernWindow 样式)
3. 在 `TrayManager.cs` 添加托盘菜单项
4. 在 `App.xaml.cs` 注册 DI 服务
5. 在 `HotkeyManager.cs` 注册快捷键（可选）

### 修改 UI 样式

- **颜色**: 修改 `Styles/Colors.xaml` 中的语义颜色
- **字体**: 修改 `Styles/Typography.xaml` 中的 FontSize*/FontWeight*
- **按钮**: 修改 `Styles/Buttons.xaml`
- **全局应用**: 所有窗口通过 ResourceDictionary 自动继承

### 添加配置项

1. 在 `Models/` 添加配置模型类
2. 在 `Services/AppConfig.cs` 添加加载/保存逻辑
3. 在 `Views/Settings/` 对应面板添加 UI 控件
4. 保存时调用 `AppConfig.Save()`

### 调试技巧

```bash
# 查看日志
cat $env:APPDATA\STool\Logs\log-20260610.txt

# 删除配置重新开始
rm $env:APPDATA\STool\appsettings.json

# 清空剪贴板数据库
rm $env:APPDATA\STool\clipboard.db
```

---

## 已知问题与 TODO

### 待实现功能
- [ ] OCR 文字识别
- [ ] 剪贴板搜索/过滤
- [ ] 翻译历史记录
- [ ] 划词翻译
- [ ] 截图标注工具
- [ ] 多语言支持（i18n）
- [ ] 自动更新检查

### 已知问题
- 剪贴板监听偶尔漏掉快速连续复制
- 全屏应用下快捷键可能被拦截
- 高 DPI 下截图尺寸计算需验证

### 性能优化建议
- 剪贴板数据库定期清理（保留最近 1000 条）
- 图片压缩后存储（当前原图存储）
- 翻译结果缓存（避免重复请求）

---

## 技术债务

1. **测试覆盖率**: 目前无单元测试，建议添加
2. **错误处理**: 部分异常直接 catch 空处理，应记录日志
3. **配置验证**: 用户输入的配置项缺少有效性验证
4. **国际化**: 所有文本硬编码中文，应抽取资源文件

---

## 发布流程

### 构建便携版
```powershell
# 1. 确保在 main 分支
git checkout main

# 2. 构建
.\build-portable.ps1 -Version "1.0.0"

# 3. 测试生成的 zip
# 解压 releases/STool_v1.0.0_Portable.zip 并运行

# 4. 打标签
git tag -a v1.0.0 -m "Release v1.0.0"
git push origin v1.0.0

# 5. GitHub Release
# 上传 zip 到 https://github.com/kabuda2077/STool/releases
```

### 版本号规范
- 主版本号: 重大功能变更或架构调整
- 次版本号: 新功能添加
- 修订号: bug 修复

---

## 联系与贡献

- **GitHub**: https://github.com/kabuda2077/STool
- **Issues**: https://github.com/kabuda2077/STool/issues

---

## 参考文档

- [Git 工作流](GIT_WORKFLOW.md)
- [设计规范](DESIGN.md)
- [快捷键说明](SHORTCUTS.md)
- [测试指南](TESTING_GUIDE.md)

---

**最后更新**: 2026-06-10
