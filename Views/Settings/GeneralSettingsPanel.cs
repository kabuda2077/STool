using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using STool.Core;
using STool.Models;

namespace STool.Views.Settings;

public class GeneralSettingsPanel : StackPanel
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "STool";

    private readonly ConfigManager _configManager;
    private readonly IAppShell _shell;
    private const string HotkeyRecordingHint = "点击后直接按快捷键组合，按 Tab 或点击其他位置保存，Esc 结束录制";
    private readonly Dictionary<System.Windows.Controls.TextBox, TextBlock> _hotkeyErrors = new();
    private bool _restoringHotkeys;
    private System.Windows.Controls.CheckBox _chkAutoStart = null!;
    private System.Windows.Controls.CheckBox _chkHideTrayIcon = null!;
    private System.Windows.Controls.CheckBox _chkDiagnostics = null!;
    private System.Windows.Controls.TextBox _txtScreenshotHotkey = null!;
    private System.Windows.Controls.TextBox _txtTranslationHotkey = null!;
    private System.Windows.Controls.TextBox _txtClipboardHotkey = null!;
    private System.Windows.Controls.TextBox _txtSettingsHotkey = null!;
    private System.Windows.Controls.TextBox _txtLanTransferHotkey = null!;

    private readonly Func<bool> _readAutoStart;
    private readonly Func<bool, bool> _writeAutoStart;

    public GeneralSettingsPanel(ConfigManager configManager, IAppShell shell)
        : this(configManager, shell, IsAutoStartEnabled, SetAutoStart)
    {
    }

    internal GeneralSettingsPanel(ConfigManager configManager, IAppShell shell,
        Func<bool> readAutoStart, Func<bool, bool> writeAutoStart)
    {
        _readAutoStart = readAutoStart;
        _writeAutoStart = writeAutoStart;
        _configManager = configManager;
        _shell = shell;
        InitializeUI();
        LoadSettings();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 启动与托盘 ──
        var launchSection = SettingsLayout.CreateSectionContent("启动与托盘");

        _chkAutoStart = SettingsLayout.CreateSwitch();
        launchSection.Children.Add(SettingsLayout.CreateSwitchRow(
            "开机自动启动",
            string.Empty,
            _chkAutoStart,
            enabled =>
            {
                if (_writeAutoStart(enabled))
                    SettingsFeedback.Report(this);
                else
                    _chkAutoStart.IsChecked = !enabled;
            }));

        _chkHideTrayIcon = SettingsLayout.CreateSwitch();
        launchSection.Children.Add(SettingsLayout.CreateSwitchRow(
            "隐藏托盘图标",
            "仍可用快捷键唤出",
            _chkHideTrayIcon,
            SaveHideTrayIcon,
            isLast: true));

        Children.Add(SettingsLayout.CreateSection(launchSection));

        // ── 快捷键设置(紧凑双列) ──
        var hotkeysSection = SettingsLayout.CreateSectionContent("快捷键设置");
        _txtScreenshotHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("截图", _txtScreenshotHotkey));

        _txtTranslationHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("翻译", _txtTranslationHotkey));

        _txtClipboardHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("剪贴板", _txtClipboardHotkey));

        _txtLanTransferHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("传输", _txtLanTransferHotkey));

        _txtSettingsHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("设置", _txtSettingsHotkey, isLast: true));

        // 即时保存:快捷键录制完(失焦)自动校验并保存
        foreach (var box in new[] { _txtScreenshotHotkey, _txtTranslationHotkey, _txtClipboardHotkey, _txtSettingsHotkey, _txtLanTransferHotkey })
        {
            box.LostKeyboardFocus += (_, _) => CommitHotkeysIfChanged();
        }

        Children.Add(SettingsLayout.CreateSection(hotkeysSection));

        // ── 诊断 ──
        var diagnosticsSection = SettingsLayout.CreateSectionContent("诊断");
        _chkDiagnostics = SettingsLayout.CreateSwitch();
        diagnosticsSection.Children.Add(SettingsLayout.CreateSwitchRow(
            "记录诊断日志",
            "排查卡顿或异常时开启，额外记录内存占用与截图启动耗时",
            _chkDiagnostics,
            SaveDiagnostics,
            isLast: true));
        Children.Add(SettingsLayout.CreateSection(diagnosticsSection));

        Children.Add(new Border { Height = 12 });
    }

    private Grid CreateHotkeyRow(string label, System.Windows.Controls.TextBox box, bool isLast = false)
    {
        var field = new StackPanel();
        field.Children.Add(box);
        var error = SettingsLayout.CreateHint(string.Empty);
        error.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        error.Margin = new Thickness(SettingsLayout.FormTextInset, 4, 0, 8);
        error.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        field.Children.Add(error);
        _hotkeyErrors.Add(box, error);
        System.Windows.Automation.AutomationProperties.SetName(box, $"{label}快捷键");
        SetHotkeyError(box, string.Empty);
        box.TextChanged += (_, _) =>
        {
            if (_restoringHotkeys || string.IsNullOrEmpty(error.Text))
                return;

            // Duplicate/conflicting fields share one message; editing either dismisses that failure.
            var previousError = error.Text;
            foreach (var affected in _hotkeyErrors.Where(pair => pair.Value.Text == previousError).Select(pair => pair.Key).ToArray())
                SetHotkeyError(affected, string.Empty);
        };
        return SettingsLayout.CreateInlineField(label, field, SettingsLayout.HotkeyLabelWidth, isLast, minHeight: 44);
    }

    private void SetHotkeyError(System.Windows.Controls.TextBox box, string message)
    {
        var error = _hotkeyErrors[box];
        error.Text = message;
        error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        var help = string.IsNullOrEmpty(message) ? HotkeyRecordingHint : $"{message}。{HotkeyRecordingHint}";
        box.ToolTip = help;
        System.Windows.Automation.AutomationProperties.SetHelpText(box, help);
        if (!string.IsNullOrEmpty(message))
            System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private System.Windows.Controls.TextBox CreateHotkeyBox()
    {
        return new HotkeyBox
        {
            Style = (Style)FindResource("HotkeyTextBox"),
            Height = SettingsLayout.InputHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Shell = _shell
        };
    }

    private void LoadSettings()
    {
        var config = _configManager.Get();

        _chkAutoStart.IsChecked = _readAutoStart();
        _chkHideTrayIcon.IsChecked = config.HideTrayIcon;
        _chkDiagnostics.IsChecked = config.Diagnostics.Enabled;

        _txtScreenshotHotkey.Text = config.Hotkeys.Screenshot;
        _txtTranslationHotkey.Text = config.Hotkeys.Translation;
        _txtClipboardHotkey.Text = config.Hotkeys.Clipboard;
        _txtSettingsHotkey.Text = config.Hotkeys.Settings;
        _txtLanTransferHotkey.Text = config.Hotkeys.LanTransfer;
    }

    /// <summary>快捷键录制完(失焦)时:有变化才校验并保存,非法则回退到已保存值。</summary>
    private void CommitHotkeysIfChanged()
    {
        var config = _configManager.Get();
        var screenshot = _txtScreenshotHotkey.Text.Trim();
        var translation = _txtTranslationHotkey.Text.Trim();
        var clipboard = _txtClipboardHotkey.Text.Trim();
        var settings = _txtSettingsHotkey.Text.Trim();
        var lanTransfer = _txtLanTransferHotkey.Text.Trim();

        // 无变化则不动,避免无意义的保存和"已保存"闪烁
        if (screenshot == config.Hotkeys.Screenshot &&
            translation == config.Hotkeys.Translation &&
            clipboard == config.Hotkeys.Clipboard &&
            settings == config.Hotkeys.Settings &&
            lanTransfer == config.Hotkeys.LanTransfer)
        {
            return;
        }

        var fields = new[]
        {
            (Box: _txtScreenshotHotkey, Label: "截图", Value: screenshot, Previous: config.Hotkeys.Screenshot),
            (Box: _txtTranslationHotkey, Label: "翻译", Value: translation, Previous: config.Hotkeys.Translation),
            (Box: _txtClipboardHotkey, Label: "剪贴板", Value: clipboard, Previous: config.Hotkeys.Clipboard),
            (Box: _txtSettingsHotkey, Label: "设置", Value: settings, Previous: config.Hotkeys.Settings),
            (Box: _txtLanTransferHotkey, Label: "传输", Value: lanTransfer, Previous: config.Hotkeys.LanTransfer)
        };
        var changedFields = fields.Where(field => field.Value != field.Previous).ToArray();
        var invalidFields = fields.Where(field => !HotkeyManager.IsValidHotkey(field.Value)).ToArray();
        if (invalidFields.Length > 0)
        {
            RestoreHotkeyText(config.Hotkeys);
            foreach (var field in invalidFields)
                SetHotkeyError(field.Box, $"{field.Label}快捷键格式无效，请使用 Ctrl+Alt+A 或 Ctrl+Shift+F1 等组合，已恢复原设置");
            return;
        }

        var proposed = new HotkeyConfig
        {
            Screenshot = HotkeyManager.NormalizeHotkey(screenshot)!,
            Translation = HotkeyManager.NormalizeHotkey(translation)!,
            Clipboard = HotkeyManager.NormalizeHotkey(clipboard)!,
            Settings = HotkeyManager.NormalizeHotkey(settings)!,
            LanTransfer = HotkeyManager.NormalizeHotkey(lanTransfer)!
        };
        var duplicates = fields
            .GroupBy(field => HotkeyManager.NormalizeHotkey(field.Value)!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        if (duplicates.Length > 0)
        {
            RestoreHotkeyText(config.Hotkeys);
            foreach (var duplicate in duplicates)
            {
                var message = $"{string.Join("、", duplicate.Select(field => field.Label))} 重复使用了 {duplicate.Key}，已恢复原设置";
                foreach (var field in duplicate)
                    SetHotkeyError(field.Box, message);
            }
            return;
        }

        var previous = new HotkeyConfig
        {
            Screenshot = config.Hotkeys.Screenshot,
            Translation = config.Hotkeys.Translation,
            Clipboard = config.Hotkeys.Clipboard,
            Settings = config.Hotkeys.Settings,
            LanTransfer = config.Hotkeys.LanTransfer
        };

        try
        {
            var updated = _configManager.Update(current => ApplyHotkeys(current.Hotkeys, proposed));
            var failures = _shell.ReloadHotkeys()
                .Where(result => !result.Success)
                .ToArray();
            if (failures.Length > 0)
            {
                _configManager.Update(current => ApplyHotkeys(current.Hotkeys, previous));
                _shell.ReloadHotkeys();
                RestoreHotkeyText(previous);

                foreach (var failure in failures)
                {
                    foreach (var field in fields.Where(field => string.Equals(
                        HotkeyManager.NormalizeHotkey(field.Value), failure.Hotkey, StringComparison.OrdinalIgnoreCase)))
                    {
                        SetHotkeyError(field.Box, $"{failure.Hotkey} 未能注册，可能已被其他程序占用，已恢复原设置");
                    }
                }
                return;
            }

            RestoreHotkeyText(updated.Hotkeys);
            foreach (var field in fields)
                SetHotkeyError(field.Box, string.Empty);
            SettingsFeedback.Report(this);
        }
        catch (Exception ex)
        {
            try
            {
                _configManager.Update(current => ApplyHotkeys(current.Hotkeys, previous));
                _shell.ReloadHotkeys();
            }
            catch (Exception rollbackEx)
            {
                Log.Error(rollbackEx, "Failed to roll back hotkey settings");
            }
            RestoreHotkeyText(previous);
            foreach (var field in changedFields)
                SetHotkeyError(field.Box, $"保存失败：{ex.Message}");
        }
    }

    private void RestoreHotkeyText(HotkeyConfig hotkeys)
    {
        _restoringHotkeys = true;
        try
        {
            _txtScreenshotHotkey.Text = hotkeys.Screenshot;
            _txtTranslationHotkey.Text = hotkeys.Translation;
            _txtClipboardHotkey.Text = hotkeys.Clipboard;
            _txtSettingsHotkey.Text = hotkeys.Settings;
            _txtLanTransferHotkey.Text = hotkeys.LanTransfer;
        }
        finally
        {
            _restoringHotkeys = false;
        }
    }

    private static void ApplyHotkeys(HotkeyConfig target, HotkeyConfig source)
    {
        target.Screenshot = source.Screenshot;
        target.Translation = source.Translation;
        target.Clipboard = source.Clipboard;
        target.Settings = source.Settings;
        target.LanTransfer = source.LanTransfer;
    }

    /// <summary>
    /// 读取开机自启状态。便携版可能被整体移动到新目录，此时注册表里还是旧路径，
    /// 发现后直接改成当前路径，避免开关显示开启但实际启动失败。
    /// </summary>
    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not string registered)
                return false;

            var currentPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentPath) && !IsSamePath(registered, currentPath))
            {
                key.SetValue(RunValueName, Quote(currentPath));
                Log.Information("Updated auto-start path from {OldPath} to {NewPath}", registered, currentPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read auto-start registry value");
            return false;
        }
    }

    internal static bool IsSamePath(string registeredCommand, string executablePath)
    {
        var registered = registeredCommand.Trim().Trim('"');
        try
        {
            return string.Equals(Path.GetFullPath(registered), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Quote(string path) => $"\"{path}\"";

    private void SaveHideTrayIcon(bool enabled)
    {
        try
        {
            _configManager.Update(config => config.HideTrayIcon = enabled);
            _shell.ReloadTrayIconVisibility();
            SettingsFeedback.Report(this);
        }
        catch (Exception ex)
        {
            _chkHideTrayIcon.IsChecked = !enabled;
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void SaveDiagnostics(bool enabled)
    {
        try
        {
            _configManager.Update(config => config.Diagnostics.Enabled = enabled);
            _shell.ApplyDiagnosticsSettings();
            SettingsFeedback.Report(this);
        }
        catch (Exception ex)
        {
            _chkDiagnostics.IsChecked = !enabled;
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private static bool SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
                return false;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                    return false;
                key.SetValue(RunValueName, Quote(exePath));
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            ToastNotification.Show("设置开机自启失败", ex.Message, ToastNotification.ToastType.Error);
            return false;
        }
    }
}
