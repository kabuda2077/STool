using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Serilog;
using STool.Models;
using STool.Modules.Clipboard;
using STool.Modules.LanTransfer;
using STool.Modules.Ocr;
using STool.Modules.Screenshot;
using STool.Modules.Translation;
using STool.Views;

namespace STool.Core;

/// <summary>应用外壳：创建后台服务、托盘图标和全局热键，并按热键打开各功能窗口。</summary>
public sealed class AppBootstrap : IAppShell, IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly HotkeyManager _hotkeyManager;
    private readonly OcrManager _ocrManager;
    private readonly TranslationManager _translationManager;
    private readonly ClipboardManager? _clipboardManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly WindowCoordinator _windows = new();
    private string _appliedLogLevel = "Information";

    public AppBootstrap()
    {
        // 数据目录不可写时直接抛出，由 App 提示用户换目录后退出，避免留下无托盘无热键的空进程。
        AppPaths.EnsureWritableDataDirectory();
        AppLogging.Configure(_appliedLogLevel);
        Log.Information("STool starting...");

        _configManager = new ConfigManager();
        var config = _configManager.Get();
        ApplyDiagnostics(config.Diagnostics);

        _hotkeyManager = new HotkeyManager();
        _ocrManager = new OcrManager(_configManager);
        _translationManager = new TranslationManager(_configManager);
        _clipboardManager = TryCreateClipboardManager();

        _notifyIcon = CreateNotifyIcon(config);
        _hotkeyManager.Initialize();
        RegisterConfiguredHotkeys(notifyFailures: true);

        StartupWarmup.Schedule();
    }

    private ClipboardManager? TryCreateClipboardManager()
    {
        ClipboardManager? manager = null;
        try
        {
            manager = new ClipboardManager(_configManager);
            manager.Start();
            return manager;
        }
        catch (Exception ex)
        {
            manager?.Dispose();
            // 剪贴板数据库异常只影响剪贴板历史，其余功能照常启动。
            Log.Error(ex, "Clipboard history is unavailable");
            ToastNotification.Show(
                "剪贴板历史不可用",
                ex.Message,
                ToastNotification.ToastType.Warning,
                duration: 5000);
            return null;
        }
    }

    private void ApplyDiagnostics(DiagnosticsConfig diagnostics)
    {
        MemoryDiagnostics.Enabled = diagnostics.Enabled;
        var level = AppLogging.ParseLevel(diagnostics.LogLevel).ToString();
        if (string.Equals(level, _appliedLogLevel, StringComparison.OrdinalIgnoreCase))
            return;

        AppLogging.Configure(level);
        _appliedLogLevel = level;
        Log.Information("Log level changed to {LogLevel}", level);
    }

    private NotifyIcon CreateNotifyIcon(AppConfig config)
    {
        var notifyIcon = new NotifyIcon
        {
            Icon = AppIcons.LoadTrayIcon(),
            Visible = !config.HideTrayIcon,
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
        var config = _configManager.Get();

        var menu = new TrayMenuWindow();
        menu.AddItem("截图", config.Hotkeys.Screenshot, OnScreenshotHotkey);
        menu.AddItem("翻译", config.Hotkeys.Translation, OnTranslationHotkey);
        menu.AddItem("剪贴板历史", config.Hotkeys.Clipboard, OnClipboardHotkey);
        menu.AddItem("局域网传输", config.Hotkeys.LanTransfer, OnLanTransferHotkey);
        if (_clipboardManager != null)
        {
            menu.AddSeparator();
            menu.AddItem(
                config.Clipboard.Enabled ? "暂停记录剪贴板" : "恢复记录剪贴板",
                string.Empty,
                ToggleClipboardRecording);
        }
        menu.AddSeparator();
        menu.AddItem("设置", config.Hotkeys.Settings, ShowSettings);
        menu.AddSeparator();
        menu.AddItem("退出 STool", string.Empty, ExitApplication, danger: true);
        menu.ShowNearCursor();
    }

    private void ToggleClipboardRecording()
    {
        var enabled = _configManager
            .Update(config => config.Clipboard.Enabled = !config.Clipboard.Enabled)
            .Clipboard.Enabled;
        ApplyClipboardSettings();
        ToastNotification.Show(
            enabled ? "已恢复记录剪贴板" : "已暂停记录剪贴板",
            type: ToastNotification.ToastType.Info);
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
        return RegisterConfiguredHotkeys(notifyFailures);
    }

    public void SuspendHotkeys()
    {
        _hotkeyManager.UnregisterAll();
    }

    public void ReloadTrayIconVisibility()
    {
        _notifyIcon.Visible = !_configManager.Get().HideTrayIcon;
    }

    public void ApplyClipboardSettings()
    {
        _clipboardManager?.ApplySettings();
    }

    public void ApplyDiagnosticsSettings()
    {
        ApplyDiagnostics(_configManager.Get().Diagnostics);
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
        var diagnosticsEnabled = _configManager.Get().Diagnostics.Enabled;
        var startupTimer = diagnosticsEnabled ? Stopwatch.StartNew() : null;
        var existing = _windows.Get<CaptureOverlay>("screenshot");
        var overlay = _windows.ShowSingle("screenshot", () =>
        {
            var created = new CaptureOverlay(
                new CaptureOverlayServices(_ocrManager, _translationManager, diagnosticsEnabled),
                startupTimer);
            if (startupTimer != null)
                Log.Information("[CaptureStartup] Overlay constructed in {ElapsedMs}ms", startupTimer.ElapsedMilliseconds);
            return created;
        });
        if (existing == null && startupTimer != null)
        {
            Log.Information("[CaptureStartup] Show returned in {ElapsedMs}ms", startupTimer.ElapsedMilliseconds);
            overlay.SchedulePostShowDiagnostics();
        }
    }

    private void OnTranslationHotkey()
    {
        Log.Information("Translation hotkey triggered");
        _windows.ShowSingle("translation", () => new TranslationPanel(_translationManager));
    }

    private void OnClipboardHotkey()
    {
        Log.Information("Clipboard hotkey triggered");
        if (_clipboardManager == null)
        {
            ToastNotification.Show(
                "剪贴板历史不可用",
                "剪贴板数据库初始化失败，详情见 Data\\Logs 中的日志。",
                ToastNotification.ToastType.Warning);
            return;
        }

        _windows.ShowSingle("clipboard", () => new ClipboardPanel(_clipboardManager));
    }

    private void OnLanTransferHotkey()
    {
        Log.Information("LAN transfer hotkey triggered");
        _windows.ShowSingle("lan-transfer", () => new LanTransferWindow(_configManager));
    }

    public void ShowSettings()
    {
        var existing = _windows.Get<SettingsWindow>("settings");
        var settings = _windows.ShowSingle("settings", () => new SettingsWindow(_configManager, this));
        if (existing == null)
            settings.Closed += (_, _) => OnSettingsClosed();
    }

    private void OnSettingsClosed()
    {
        // 设置窗口关闭时重新读取配置文件，手动编辑 config.json 的改动也能在此时生效。
        _configManager.Reload();
        var config = _configManager.Get();
        ApplyDiagnostics(config.Diagnostics);
        ReloadHotkeys();
        ReloadTrayIconVisibility();
        ApplyClipboardSettings();
    }

    private void ExitApplication()
    {
        Log.Information("STool exiting...");
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _windows.Dispose();
        _notifyIcon.Dispose();
        _hotkeyManager.Dispose();
        _clipboardManager?.Dispose();
        _translationManager.Dispose();
        _ocrManager.Dispose();
        Log.CloseAndFlush();
    }
}
