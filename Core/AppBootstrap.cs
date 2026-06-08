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
        var config = _configManager.Get();

        var notifyIcon = new NotifyIcon
        {
            Icon = AppIcons.LoadTrayIcon(),
            Visible = true,
            Text = "STool - 快捷工具"
        };

        var contextMenu = new ContextMenuStrip
        {
            BackColor = System.Drawing.Color.White,
            ForeColor = System.Drawing.Color.FromArgb(17, 24, 39),
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F),
            ImageScalingSize = new System.Drawing.Size(20, 20),
            Padding = new System.Windows.Forms.Padding(8, 8, 8, 8),
            ShowCheckMargin = false,
            ShowImageMargin = true,
            Renderer = new TrayMenuRenderer()
        };

        var titleItem = new ToolStripLabel("STool 正在运行")
        {
            ForeColor = System.Drawing.Color.FromArgb(100, 116, 139),
            Padding = new System.Windows.Forms.Padding(8, 5, 8, 6),
            Margin = new System.Windows.Forms.Padding(0, 0, 0, 2)
        };

        contextMenu.Items.Add(titleItem);
        contextMenu.Items.Add(CreateSeparator());
        contextMenu.Items.Add(CreateMenuItem("截图", config.Hotkeys.Screenshot, TrayMenuIconKind.Screenshot, (_, _) => OnScreenshotHotkey()));
        contextMenu.Items.Add(CreateMenuItem("翻译", config.Hotkeys.Translation, TrayMenuIconKind.Translate, (_, _) => OnTranslationHotkey()));
        contextMenu.Items.Add(CreateMenuItem("剪贴板历史", config.Hotkeys.Clipboard, TrayMenuIconKind.Clipboard, (_, _) => OnClipboardHotkey()));
        contextMenu.Items.Add(CreateSeparator());
        contextMenu.Items.Add(CreateMenuItem("设置", string.Empty, TrayMenuIconKind.Settings, OnSettings));
        contextMenu.Items.Add(CreateSeparator());
        contextMenu.Items.Add(CreateMenuItem("退出 STool", string.Empty, TrayMenuIconKind.Exit, OnExit));

        contextMenu.Opening += (_, _) =>
        {
            _configManager.Reload();
            var currentConfig = _configManager.Get();
            if (contextMenu.Items[2] is ToolStripMenuItem screenshotItem)
                screenshotItem.ShortcutKeyDisplayString = currentConfig.Hotkeys.Screenshot;
            if (contextMenu.Items[3] is ToolStripMenuItem translationItem)
                translationItem.ShortcutKeyDisplayString = currentConfig.Hotkeys.Translation;
            if (contextMenu.Items[4] is ToolStripMenuItem clipboardItem)
                clipboardItem.ShortcutKeyDisplayString = currentConfig.Hotkeys.Clipboard;
        };

        notifyIcon.ContextMenuStrip = contextMenu;
        notifyIcon.DoubleClick += (_, _) => OnClipboardHotkey();

        return notifyIcon;
    }

    private static ToolStripMenuItem CreateMenuItem(
        string text,
        string shortcut,
        TrayMenuIconKind iconKind,
        EventHandler onClick)
    {
        return new ToolStripMenuItem(text, AppIcons.CreateMenuIcon(iconKind), onClick)
        {
            AutoSize = false,
            Height = 34,
            Width = 220,
            Padding = new System.Windows.Forms.Padding(6, 0, 12, 0),
            Margin = new System.Windows.Forms.Padding(2),
            ShortcutKeyDisplayString = shortcut
        };
    }

    private static ToolStripSeparator CreateSeparator()
    {
        return new ToolStripSeparator
        {
            AutoSize = false,
            Height = 9,
            Margin = new System.Windows.Forms.Padding(0, 2, 0, 2)
        };
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

    private void OnScreenshotHotkey()
    {
        Log.Information("Screenshot hotkey triggered");

        // 创建截图覆盖窗口
        var overlay = new STool.Modules.Screenshot.CaptureOverlay();

        overlay.SelectionCompleted += (s, result) =>
        {
            overlay.Close();

            // 显示工具条
            var toolbar = new STool.Modules.Screenshot.ToolbarWindow(result.Bitmap, result.Bounds);
            toolbar.Show();
        };

        overlay.SelectionCancelled += (s, e) =>
        {
            Log.Information("Screenshot cancelled");
        };

        overlay.Show();
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
        var configManager = _serviceProvider.GetService(typeof(ConfigManager)) as ConfigManager;
        if (configManager == null)
        {
            Log.Warning("ConfigManager not found");
            return;
        }

        var settingsWindow = new STool.Views.SettingsWindow(configManager);
        settingsWindow.Show();
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
