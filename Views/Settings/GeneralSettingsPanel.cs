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
    private const string HotkeyRecordingHint = "点击修改，Esc 取消。";
    private readonly Dictionary<System.Windows.Controls.TextBox, TextBlock> _hotkeyErrors = new();
    private bool _restoringHotkeys;
    private System.Windows.Controls.CheckBox _chkAutoStart = null!;
    private System.Windows.Controls.CheckBox _chkHideTrayIcon = null!;
    private System.Windows.Controls.CheckBox _chkDiagnostics = null!;
    private FrameworkElement _hideTrayRow = null!;
    private HotkeyBox _txtScreenshotHotkey = null!;
    private HotkeyBox _txtTranslationHotkey = null!;
    private HotkeyBox _txtClipboardHotkey = null!;
    private HotkeyBox _txtSettingsHotkey = null!;
    private HotkeyBox _txtLanTransferHotkey = null!;

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
        _hideTrayRow = SettingsLayout.CreateSwitchRow(
            "隐藏托盘图标",
            string.Empty,
            _chkHideTrayIcon,
            SaveHideTrayIcon,
            hasTopDivider: true);
        launchSection.Children.Add(_hideTrayRow);

        Children.Add(SettingsLayout.CreateSection(launchSection));

        // ── 全局快捷键 ──
        var hotkeysSection = SettingsLayout.CreateSectionContent("全局快捷键");
        _txtScreenshotHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("截图", _txtScreenshotHotkey));

        _txtTranslationHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("翻译", _txtTranslationHotkey, hasTopDivider: true));

        _txtClipboardHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("剪贴板", _txtClipboardHotkey, hasTopDivider: true));

        _txtLanTransferHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("局域网传输", _txtLanTransferHotkey, hasTopDivider: true));

        _txtSettingsHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(CreateHotkeyRow("设置", _txtSettingsHotkey, hasTopDivider: true));

        var restoreHotkeys = SettingsLayout.CreateSecondaryActionButton("恢复默认");
        restoreHotkeys.Click += (_, _) =>
        {
            foreach (var box in _hotkeyErrors.Keys.Cast<HotkeyBox>())
                box.StopRecording(cancel: true);
            ClearHotkeyErrors();
            RestoreHotkeyText(new HotkeyConfig());
            CommitHotkeysIfChanged();
        };
        Children.Add(SettingsLayout.CreateSection(hotkeysSection, restoreHotkeys));

        // ── 诊断 ──
        var diagnosticsSection = SettingsLayout.CreateSectionContent("诊断");
        _chkDiagnostics = SettingsLayout.CreateSwitch();
        diagnosticsSection.Children.Add(SettingsLayout.CreateSwitchRow(
            "记录诊断日志",
            string.Empty,
            _chkDiagnostics,
            SaveDiagnostics));
        Children.Add(SettingsLayout.CreateSection(diagnosticsSection));
    }

    private Grid CreateHotkeyRow(string label, HotkeyBox box, bool hasTopDivider = false)
    {
        var error = SettingsLayout.CreateHint(string.Empty);
        error.Foreground = (System.Windows.Media.Brush)FindResource("SettingsErrorBrush");
        error.TextAlignment = TextAlignment.Right;
        error.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        _hotkeyErrors.Add(box, error);
        System.Windows.Automation.AutomationProperties.SetName(box, $"{label}快捷键");
        SetHotkeyError(box, string.Empty);
        box.RecordingStarted += (_, _) => ClearHotkeyErrors();
        box.RecordingEnded += (_, _) =>
        {
            // The saved configuration remains authoritative when canceling a failed attempt.
            RestoreHotkeyText(_configManager.Get().Hotkeys);
            ClearHotkeyErrors();
        };
        box.RecordingRejected += (_, _) => SetHotkeyError(box, "请使用修饰键组合或功能键。");
        box.HotkeyCaptured += (_, _) =>
        {
            ClearHotkeyErrors();
            var registered = CommitHotkeysIfChanged();
            if (_hotkeyErrors.Values.All(message => message.Visibility == Visibility.Collapsed))
                box.StopRecording(reloadHotkeys: !registered);
            else
                _shell.SuspendHotkeys();
        };
        box.TextChanged += (_, _) =>
        {
            if (_restoringHotkeys || string.IsNullOrEmpty(error.Text))
                return;

            // Duplicate/conflicting fields share one message; editing either dismisses that failure.
            var previousError = error.Text;
            foreach (var affected in _hotkeyErrors.Where(pair => pair.Value.Text == previousError).Select(pair => pair.Key).ToArray())
                SetHotkeyError(affected, string.Empty);
        };
        return SettingsLayout.CreateInlineFieldWithHint(label, box, error, hasTopDivider: hasTopDivider);
    }

    private void ClearHotkeyErrors()
    {
        foreach (var box in _hotkeyErrors.Keys)
            SetHotkeyError(box, string.Empty);
    }

    private void SetHotkeyError(System.Windows.Controls.TextBox box, string message)
    {
        var error = _hotkeyErrors[box];
        error.Text = message;
        error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        SettingsLayout.SetFieldHelp(box, HotkeyRecordingHint);
        if (!string.IsNullOrEmpty(message))
            System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private HotkeyBox CreateHotkeyBox()
    {
        return new HotkeyBox
        {
            Style = (Style)FindResource("SettingsHotkeyBox"),
            Shell = _shell
        };
    }

    private void LoadSettings()
    {
        var config = _configManager.Get();

        _chkAutoStart.IsChecked = _readAutoStart();
        _chkHideTrayIcon.IsChecked = config.HideTrayIcon;
        _chkDiagnostics.IsChecked = config.Diagnostics.Enabled;

        RestoreHotkeyText(config.Hotkeys);
    }

    /// <summary>捕获快捷键后立即校验并保存；非法或注册失败时回退到已保存值。</summary>
    private bool CommitHotkeysIfChanged()
    {
        var config = _configManager.Get();
        var screenshot = _txtScreenshotHotkey.Text.Trim();
        var translation = _txtTranslationHotkey.Text.Trim();
        var clipboard = _txtClipboardHotkey.Text.Trim();
        var settings = _txtSettingsHotkey.Text.Trim();
        var lanTransfer = _txtLanTransferHotkey.Text.Trim();

        // 无变化则不动,避免无意义的保存和"已保存"闪烁
        if (SameHotkey(screenshot, config.Hotkeys.Screenshot) &&
            SameHotkey(translation, config.Hotkeys.Translation) &&
            SameHotkey(clipboard, config.Hotkeys.Clipboard) &&
            SameHotkey(settings, config.Hotkeys.Settings) &&
            SameHotkey(lanTransfer, config.Hotkeys.LanTransfer))
        {
            return false;
        }

        var fields = new[]
        {
            (Box: _txtScreenshotHotkey, Label: "截图", Value: screenshot, Previous: config.Hotkeys.Screenshot),
            (Box: _txtTranslationHotkey, Label: "翻译", Value: translation, Previous: config.Hotkeys.Translation),
            (Box: _txtClipboardHotkey, Label: "剪贴板", Value: clipboard, Previous: config.Hotkeys.Clipboard),
            (Box: _txtSettingsHotkey, Label: "设置", Value: settings, Previous: config.Hotkeys.Settings),
            (Box: _txtLanTransferHotkey, Label: "局域网传输", Value: lanTransfer, Previous: config.Hotkeys.LanTransfer)
        };
        var changedFields = fields.Where(field => !SameHotkey(field.Value, field.Previous)).ToArray();
        var invalidFields = fields.Where(field => !HotkeyManager.IsValidHotkey(field.Value)).ToArray();
        if (invalidFields.Length > 0)
        {
            RestoreHotkeyText(config.Hotkeys);
            foreach (var field in invalidFields)
                SetHotkeyError(field.Box, $"{field.Label}快捷键格式无效，请使用修饰键组合或 F1–F24，已恢复原设置");
            return false;
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
                foreach (var field in duplicate.Where(field => changedFields.Any(changed => ReferenceEquals(changed.Box, field.Box))))
                {
                    var existing = duplicate.First(other => !ReferenceEquals(other.Box, field.Box));
                    SetHotkeyError(field.Box, $"此快捷键已分配给{existing.Label}。");
                }
            }
            return false;
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
                        SetHotkeyError(field.Box, $"{FormatHotkey(failure.Hotkey)} 未能注册，可能已被其他程序占用，已恢复原设置");
                    }
                }
                return false;
            }

            RestoreHotkeyText(updated.Hotkeys);
            foreach (var field in fields)
                SetHotkeyError(field.Box, string.Empty);
            SettingsFeedback.Report(this);
            return true;
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
            return false;
        }
    }

    private void RestoreHotkeyText(HotkeyConfig hotkeys)
    {
        _restoringHotkeys = true;
        try
        {
            _txtScreenshotHotkey.Text = FormatHotkey(hotkeys.Screenshot);
            _txtTranslationHotkey.Text = FormatHotkey(hotkeys.Translation);
            _txtClipboardHotkey.Text = FormatHotkey(hotkeys.Clipboard);
            _txtSettingsHotkey.Text = FormatHotkey(hotkeys.Settings);
            _txtLanTransferHotkey.Text = FormatHotkey(hotkeys.LanTransfer);
            var trayHelp = $"隐藏后，按 {FormatHotkey(hotkeys.Settings)} 打开设置。";
            SettingsLayout.SetFieldHelp(_chkHideTrayIcon, trayHelp);
            System.Windows.Automation.AutomationProperties.SetHelpText(_hideTrayRow, trayHelp);
        }
        finally
        {
            _restoringHotkeys = false;
        }
    }

    private static string FormatHotkey(string value) =>
        HotkeyManager.NormalizeHotkey(value)?.Replace("+", " + ") ?? value;

    private static bool SameHotkey(string first, string second) =>
        (HotkeyManager.NormalizeHotkey(first) ?? first) == (HotkeyManager.NormalizeHotkey(second) ?? second);

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
