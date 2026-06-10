# STool 开发指南

> AI 助手快速上下文，5 分钟了解项目

## 项目身份

**STool** - Windows 效率工具（截图、翻译、剪贴板、OCR）  
技术栈: .NET 9.0 + WPF + Windows Forms  
仓库: https://github.com/kabuda2077/STool

## 快速命令

```bash
dotnet build && dotnet run                    # 构建运行
.\build-portable.ps1 -Version "1.0.0"         # 发布便携版
cat $env:APPDATA\STool\Logs\log-*.txt         # 查看日志
```

## 项目结构

```
App.xaml.cs                  应用入口，DI 配置
Core/
  ├─ TrayManager.cs          系统托盘
  └─ HotkeyManager.cs        全局快捷键 (Alt+1/2/3)
Modules/                     功能模块
  ├─ Clipboard/              剪贴板历史 (SQLite)
  ├─ Translation/            翻译 (谷歌/腾讯)
  └─ Screenshot/             截图
Views/                       设置窗口
Styles/                      全局样式
  ├─ Colors.xaml             颜色系统
  ├─ Typography.xaml         字体系统
  ├─ Buttons.xaml            按钮样式
  └─ Windows.xaml            ModernWindow 样式
Services/AppConfig.cs        配置管理
Models/                      数据模型
```

## 设计系统

### 颜色 (Styles/Colors.xaml)
```
PrimaryBrush            #2563EB  主色
TextPrimaryBrush        #111827  主文字
TextSecondaryBrush      #64748B  次要文字
SurfaceBrush            #FFFFFF  卡片背景
SurfaceCanvasBrush      #F2F4F7  页面底色
SurfaceAltBrush         #F1F5F9  悬停态
SurfaceSunkenBrush      #EDF0F4  输入框背景
BorderBrush             #D9E0EA  边框
ErrorBrush              #DC2626  错误/危险
```

**规则**: 只修改 Colors.xaml 中的颜色定义，不要硬编码颜色值

### 字体 (Styles/Typography.xaml)
```
FontSizeTitle      18px  页面标题
FontSizeSubtitle   15px  区块标题
FontSizeBody       13px  正文（默认）
FontSizeCaption    12px  辅助说明
FontSizeMicro      11px  时间戳

FontWeightNormal   400   正文
FontWeightStrong   600   标题
```

**规则**: 使用语义常量，不要硬编码字号

### 阴影
```xml
<Border Effect="{StaticResource StandardShadow}"/>
```

所有浮动元素统一使用 `StandardShadow`

### 组件样式
```
按钮: PrimaryButton / SecondaryButton / IconButton / GhostButton
输入: ModernTextBox / SunkenTextBox / SunkenComboBox
窗口: ModernWindow (自定义标题栏 44px 高)
```

**ModernWindow 使用规则**:
```xml
<Window Style="{StaticResource ModernWindow}" Title="窗口标题">
    <Grid Margin="0,44,0,0">  <!-- 必须预留顶部 44px -->
        <!-- 内容 -->
    </Grid>
</Window>
```

## 开发规范

### 命名
- C# 类/方法: `PascalCase`
- 私有字段: `_camelCase`
- XAML 控件: `camelCase` (如 `btnSave`)

### Git 工作流
```bash
# main 分支保持可发布状态，实验用分支

# 开始实验
git checkout -b experiment/修字体

# 失败 → 删分支（最干净的回退）
git checkout main
git branch -D experiment/修字体

# 成功 → 合并
git checkout main
git merge experiment/修字体
```

### 提交格式
```
类型: 简短描述

类型: feat, fix, refactor, ui, docs, build, chore
```

### 提交前检查
```bash
dotnet build  # 必须构建成功
```

## 常见任务

### 添加配置项
1. `Models/` 添加配置类
2. `Services/AppConfig.cs` 添加加载/保存逻辑
3. `Views/Settings/*SettingsPanel.cs` 添加 UI

### 修改颜色
只改 `Styles/Colors.xaml` 中的颜色定义，所有地方自动更新

### 修改字体
只改 `Styles/Typography.xaml` 中的 FontSize*/FontWeight*

### 调试
```bash
cat $env:APPDATA\STool\Logs\log-*.txt          # 查看日志
rm $env:APPDATA\STool\appsettings.json         # 重置配置
rm $env:APPDATA\STool\clipboard.db             # 清空数据库
```

## 核心约定

### UI 一致性
- 所有窗口继承 `ModernWindow`
- 顶部预留 44px (`Margin="0,44,0,0"`)
- 使用语义颜色/字号常量，不硬编码
- 统一使用 `StandardShadow`

### 数据存储
- 配置: `%APPDATA%\STool\appsettings.json`
- 数据库: `%APPDATA%\STool\clipboard.db`
- 日志: `%APPDATA%\STool\Logs\`

### 代码质量
- 提交前必须 `dotnet build` 成功
- 实验性改动在分支完成
- 提交信息描述"做了什么"

## 待办事项

**功能**:
- [ ] OCR 文字识别
- [ ] 剪贴板搜索
- [ ] 翻译历史
- [ ] 划词翻译
- [ ] 截图标注

**技术债**:
- [ ] 单元测试
- [ ] 错误处理改进
- [ ] 国际化

## 已知问题

1. 剪贴板监听偶尔漏掉快速连续复制
2. 全屏应用下快捷键可能被拦截

## 最近重大变更 (2026-06-10)

- ✅ 统一阴影系统为 StandardShadow（单一阴影）
- ✅ 清理冗余样式代码
- ✅ 便携版构建脚本
- ✅ Git 工作流优化

---

**修改代码前**: 先看相关文件，理解现有模式，保持风格一致
