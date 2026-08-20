using System;
using System.Diagnostics;
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
    private readonly WindowCoordinator _windows = new();

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
        RegisterConfiguredHotkeys(notifyFailures: true);

        // 启动剪贴板监听
        var clipboardManager = _serviceProvider.GetService(typeof(STool.Modules.Clipboard.ClipboardManager))
            as STool.Modules.Clipboard.ClipboardManager;
        clipboardManager?.Start();

        StartupWarmup.Schedule(() =>
        {
            var manager = GetService<STool.Modules.Clipboard.ClipboardManager>();
            return manager == null ? null : new STool.Modules.Clipboard.ClipboardPanel(manager);
        });
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
        menu.AddItem("截图", config.Hotkeys.Screenshot, OnScreenshotHotkey);
        menu.AddItem("翻译", config.Hotkeys.Translation, OnTranslationHotkey);
        menu.AddItem("剪贴板历史", config.Hotkeys.Clipboard, OnClipboardHotkey);
        menu.AddSeparator();
        menu.AddItem("设置", config.Hotkeys.Settings, ShowSettings);
        menu.AddSeparator();
        menu.AddItem("退出 STool", string.Empty, () => OnExit(null, EventArgs.Empty), danger: true);
        menu.ShowNearCursor();
    }

    private IReadOnlyList<HotkeyRegistrationResult> RegisterConfiguredHotkeys(bool notifyFailures = false)
    {
        var config = _configManager.Get();
        var results = new[]
        {
            _hotkeyManager.RegisterHotkey("截图", config.Hotkeys.Screenshot, OnScreenshotHotkey),
            _hotkeyManager.RegisterHotkey("翻译", config.Hotkeys.Translation, OnTranslationHotkey),
            _hotkeyManager.RegisterHotkey("剪贴板", config.Hotkeys.Clipboard, OnClipboardHotkey),
            _hotkeyManager.RegisterHotkey("设置", config.Hotkeys.Settings, ShowSettings),
            _hotkeyManager.RegisterHotkey("局域网传输", config.Hotkeys.LanTransfer, OnLanTransferHotkey)
        };

        var failures = results.Where(result => !result.Success).ToArray();
        foreach (var failure in failures)
        {
            Log.Warning(
                "Failed to register {FeatureName} hotkey {Hotkey}: {Failure}",
                failure.FeatureName,
                failure.Hotkey,
                failure.Failure);
        }

        if (notifyFailures && failures.Length > 0)
        {
            var details = string.Join("、", failures.Select(result => $"{result.FeatureName} {result.Hotkey}"));
            ToastNotification.Show(
                "部分快捷键未生效",
                $"{details} 已被其他程序占用或格式无效。",
                ToastNotification.ToastType.Warning,
                duration: 5000);
        }

        Log.Information(
            "Hotkeys initialized success={SuccessCount} failed={FailureCount}",
            results.Length - failures.Length,
            failures.Length);
        return results;
    }

    public IReadOnlyList<HotkeyRegistrationResult> ReloadHotkeys(bool notifyFailures = false)
    {
        _hotkeyManager.UnregisterAll();
        _configManager.Reload();
        return RegisterConfiguredHotkeys(notifyFailures);
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

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            ShowScreenshotOverlay();
            return;
        }

        // Do not create/show the WPF overlay inside the WM_HOTKEY hook itself.
        // Posting it lets the hotkey message unwind first, so mouse input can flow
        // normally as soon as the overlay is visible.
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(ShowScreenshotOverlay));
    }

    private void ShowScreenshotOverlay()
    {
        var startupTimer = Stopwatch.StartNew();
        var existing = _windows.Get<STool.Modules.Screenshot.CaptureOverlay>("screenshot");
        var overlay = _windows.ShowSingle("screenshot", () =>
        {
            var created = new STool.Modules.Screenshot.CaptureOverlay(startupTimer);
            Log.Information("[CaptureStartup] Overlay constructed in {ElapsedMs}ms", startupTimer.ElapsedMilliseconds);
            return created;
        });
        if (existing == null)
        {
            Log.Information("[CaptureStartup] Show returned in {ElapsedMs}ms", startupTimer.ElapsedMilliseconds);
            overlay.SchedulePostShowDiagnostics();
        }
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

        _windows.ShowSingle(
            "translation",
            () => new STool.Modules.Translation.TranslationPanel(translationManager));
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

        _windows.ShowSingle(
            "clipboard",
            () => new STool.Modules.Clipboard.ClipboardPanel(clipboardManager));
    }

    private void OnLanTransferHotkey()
    {
        Log.Information("LAN transfer hotkey triggered");
        _windows.ShowSingle(
            "lan-transfer",
            () => new STool.Modules.LanTransfer.LanTransferWindow(_configManager));
    }

    public void ShowSettings()
    {
        var existing = _windows.Get<STool.Views.SettingsWindow>("settings");
        var settings = _windows.ShowSingle(
            "settings",
            () => new STool.Views.SettingsWindow(_configManager));
        if (existing == null)
            settings.Closed += (_, _) => ReloadHotkeys();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        Log.Information("STool exiting...");
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _windows.Dispose();
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
