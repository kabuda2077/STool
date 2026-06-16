using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace STool.Core;

public class AppBootstrap : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly IServiceProvider _serviceProvider;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ConfigManager _configManager;
    private STool.Views.SettingsWindow? _settingsWindow;

    public AppBootstrap()
    {
        // 初始化日志
        AppPaths.EnsureStandardDirectories();

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "app.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7
            )
            .CreateLogger();

        Log.Information("STool starting...");

        // 初始化依赖注入
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 获取核心服务
        _configManager = _serviceProvider.GetRequiredService<ConfigManager>();
        _hotkeyManager = _serviceProvider.GetRequiredService<HotkeyManager>();

        // 初始化托盘图标
        _notifyIcon = CreateNotifyIcon();

        // 初始化快捷键
        _hotkeyManager.Initialize();
        RegisterConfiguredHotkeys();

        // 启动剪贴板监听
        var clipboardManager = _serviceProvider.GetService(typeof(STool.Modules.Clipboard.ClipboardManager))
            as STool.Modules.Clipboard.ClipboardManager;
        clipboardManager?.Start();

        // 预热功能窗口:消除"第一次按快捷键慢"(JIT + BAML 解析 + 模板初始化)
        WarmUpWindows();
    }

    /// <summary>
    /// 启动后在 UI 线程空闲时,各功能窗口构造一次再丢弃,把 WPF 一次性初始化成本
    /// (JIT、BAML 解析、控件模板实例化)提前付掉。仅 new 不 Show,用户无感。
    /// </summary>
    private void WarmUpWindows()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        // 逐个排队,低优先级,互不阻塞;任一失败不影响其他与正常使用
        dispatcher.BeginInvoke(DispatcherPriorityBackground, new Action(() => WarmUp("Clipboard", () =>
        {
            var mgr = GetService<STool.Modules.Clipboard.ClipboardManager>();
            return mgr != null ? new STool.Modules.Clipboard.ClipboardPanel(mgr) : null;
        })));

        dispatcher.BeginInvoke(DispatcherPriorityBackground, new Action(() => WarmUp("Translation", () =>
        {
            var mgr = GetService<STool.Modules.Translation.TranslationManager>();
            return mgr != null ? new STool.Modules.Translation.TranslationPanel(mgr) : null;
        })));

        // 截图窗用专用预热构造:跳过抓屏(CaptureAllScreens),只付 BAML/模板的一次性成本
        dispatcher.BeginInvoke(DispatcherPriorityBackground, new Action(() => WarmUp("Screenshot",
            () => STool.Modules.Screenshot.CaptureOverlay.CreateForWarmUp())));
    }

    private const System.Windows.Threading.DispatcherPriority DispatcherPriorityBackground
        = System.Windows.Threading.DispatcherPriority.Background;

    private static void WarmUp(string name, Func<System.Windows.Window?> factory)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 仅构造以触发初始化;不 Show。从未显示的窗口不能 Close,直接丢弃由 GC 回收。
            var window = factory();
            if (window != null)
            {
                window.Visibility = System.Windows.Visibility.Hidden;
                Log.Information("[WarmUp] {Name} prewarmed in {Ms}ms", name, sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WarmUp] {Name} prewarm failed (non-fatal)", name);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<HotkeyManager>();
        services.AddSingleton<STool.Modules.Ocr.OcrManager>();
        services.AddSingleton<STool.Modules.Translation.TranslationManager>();
        services.AddSingleton<STool.Modules.Clipboard.ClipboardManager>();
        // 后续添加其他服务
    }

    private NotifyIcon CreateNotifyIcon()
    {
        var notifyIcon = new NotifyIcon
        {
            Icon = AppIcons.LoadTrayIcon(),
            Visible = !_configManager.Get().HideTrayIcon,
            Text = "STool - 快捷工具"
        };

        notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ShowTrayMenu();
        };
        notifyIcon.DoubleClick += (_, _) => OnClipboardHotkey();

        return notifyIcon;
    }

    private void ShowTrayMenu()
    {
        _configManager.Reload();
        var config = _configManager.Get();

        var menu = new TrayMenuWindow();
        menu.AddItem("截图", config.Hotkeys.Screenshot, OnScreenshotHotkey, TrayMenuIconKind.Screenshot);
        menu.AddItem("翻译", config.Hotkeys.Translation, OnTranslationHotkey, TrayMenuIconKind.Translate);
        menu.AddItem("剪贴板历史", config.Hotkeys.Clipboard, OnClipboardHotkey, TrayMenuIconKind.Clipboard);
        menu.AddSeparator();
        menu.AddItem("设置", string.Empty, ShowSettings, TrayMenuIconKind.Settings);
        menu.AddSeparator();
        menu.AddItem("退出 STool", string.Empty, () => OnExit(null, EventArgs.Empty), TrayMenuIconKind.Exit, danger: true);
        menu.ShowNearCursor();
    }

    private void RegisterConfiguredHotkeys()
    {
        var config = _configManager.Get();

        // 截图快捷键
        if (!_hotkeyManager.RegisterHotkey(config.Hotkeys.Screenshot, OnScreenshotHotkey))
        {
            Log.Warning($"Failed to register screenshot hotkey: {config.Hotkeys.Screenshot}");
        }

        // 翻译快捷键
        if (!_hotkeyManager.RegisterHotkey(config.Hotkeys.Translation, OnTranslationHotkey))
        {
            Log.Warning($"Failed to register translation hotkey: {config.Hotkeys.Translation}");
        }

        // 剪贴板快捷键
        if (!_hotkeyManager.RegisterHotkey(config.Hotkeys.Clipboard, OnClipboardHotkey))
        {
            Log.Warning($"Failed to register clipboard hotkey: {config.Hotkeys.Clipboard}");
        }

        Log.Information("Hotkeys initialized");
    }

    public void ReloadHotkeys()
    {
        _hotkeyManager.UnregisterAll();
        _configManager.Reload();
        RegisterConfiguredHotkeys();
    }

    public void ReloadTrayIconVisibility()
    {
        _configManager.Reload();
        _notifyIcon.Visible = !_configManager.Get().HideTrayIcon;
    }

    /// <summary>
    /// 临时挂起所有全局快捷键。用于快捷键录入框获得焦点时,
    /// 避免系统级热键拦截按键(否则按 Ctrl+Alt+A 等会触发功能而非被录入)。
    /// 失焦时调用 ReloadHotkeys() 恢复。
    /// </summary>
    public void SuspendGlobalHotkeys()
    {
        _hotkeyManager.UnregisterAll();
    }

    private void OnScreenshotHotkey()
    {
        Log.Information("Screenshot hotkey triggered");

        // 一体化取景窗自行处理选区/标注/复制/保存等,无需外部事件
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var overlay = new STool.Modules.Screenshot.CaptureOverlay();
        Log.Information("[Capture] overlay constructed at {Elapsed}ms, calling Show()", sw.ElapsedMilliseconds);
        overlay.Show();
        Log.Information("[Capture] overlay Show() returned at {Elapsed}ms", sw.ElapsedMilliseconds);
    }

    private void OnTranslationHotkey()
    {
        Log.Information("Translation hotkey triggered");

        var translationManager = _serviceProvider.GetService(typeof(STool.Modules.Translation.TranslationManager))
            as STool.Modules.Translation.TranslationManager;

        if (translationManager == null)
        {
            Log.Warning("TranslationManager not found");
            return;
        }

        var panel = new STool.Modules.Translation.TranslationPanel(translationManager);
        panel.Show();
    }

    private void OnClipboardHotkey()
    {
        Log.Information("Clipboard hotkey triggered");

        var clipboardManager = _serviceProvider.GetService(typeof(STool.Modules.Clipboard.ClipboardManager))
            as STool.Modules.Clipboard.ClipboardManager;

        if (clipboardManager == null)
        {
            Log.Warning("ClipboardManager not found");
            return;
        }

        var panel = new STool.Modules.Clipboard.ClipboardPanel(clipboardManager);
        panel.Show();
    }

    public void ShowSettings()
    {
        // 单例:已打开则激活,避免多个设置窗口
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new STool.Views.SettingsWindow(_configManager);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            // 安全网:关闭后确保全局快捷键按最新配置恢复
            ReloadHotkeys();
        };
        _settingsWindow.Show();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        Log.Information("STool exiting...");
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _notifyIcon.Dispose();
        _hotkeyManager.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    public T? GetService<T>() where T : class
    {
        return _serviceProvider.GetService(typeof(T)) as T;
    }
}
