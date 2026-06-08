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
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "STool"
        );
        Directory.CreateDirectory(Path.Combine(appDataPath, "Logs"));

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(
                Path.Combine(appDataPath, "Logs", "app.log"),
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
            Visible = true,
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
        menu.AddHeader("STool 正在运行");
        menu.AddSeparator();
        menu.AddItem(TrayMenuIconKind.Screenshot, "截图", config.Hotkeys.Screenshot, OnScreenshotHotkey);
        menu.AddItem(TrayMenuIconKind.Translate, "翻译", config.Hotkeys.Translation, OnTranslationHotkey);
        menu.AddItem(TrayMenuIconKind.Clipboard, "剪贴板历史", config.Hotkeys.Clipboard, OnClipboardHotkey);
        menu.AddSeparator();
        menu.AddItem(TrayMenuIconKind.Settings, "设置", string.Empty, () => OnSettings(null, EventArgs.Empty));
        menu.AddSeparator();
        menu.AddItem(TrayMenuIconKind.Exit, "退出 STool", string.Empty, () => OnExit(null, EventArgs.Empty), danger: true);
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
        new STool.Modules.Screenshot.CaptureOverlay().Show();
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

    private void OnSettings(object? sender, EventArgs e)
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
