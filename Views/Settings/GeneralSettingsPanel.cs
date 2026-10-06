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
    private System.Windows.Controls.CheckBox _chkAutoStart = null!;
    private System.Windows.Controls.CheckBox _chkHideTrayIcon = null!;
    private System.Windows.Controls.CheckBox _chkDiagnostics = null!;
    private System.Windows.Controls.TextBox _txtScreenshotHotkey = null!;
    private System.Windows.Controls.TextBox _txtTranslationHotkey = null!;
    private System.Windows.Controls.TextBox _txtClipboardHotkey = null!;
    private System.Windows.Controls.TextBox _txtSettingsHotkey = null!;
    private System.Windows.Controls.TextBox _txtLanTransferHotkey = null!;

    public GeneralSettingsPanel(ConfigManager configManager, IAppShell shell)
    {
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
            "随系统启动",
            _chkAutoStart,
            enabled =>
            {
                if (SetAutoStart(enabled))
                    ToastNotification.ShowSettingsSaved();
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
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("截图", _txtScreenshotHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtTranslationHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("翻译", _txtTranslationHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtClipboardHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("剪贴板", _txtClipboardHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtLanTransferHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("传输", _txtLanTransferHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtSettingsHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("设置", _txtSettingsHotkey, SettingsLayout.HotkeyLabelWidth, isLast: true));

        // 即时保存:快捷键录制完(失焦)自动校验并保存
        foreach (var box in new[] { _txtScreenshotHotkey, _txtTranslationHotkey, _txtClipboardHotkey, _txtSettingsHotkey, _txtLanTransferHotkey })
        {
            box.LostKeyboardFocus += (_, _) => CommitHotkeysIfChanged();
        }
        var hotkeyHint = SettingsLayout.CreateHint("点击后直接按快捷键组合");
        hotkeyHint.Margin = new Thickness(
            24 + SettingsLayout.HotkeyLabelWidth + SettingsLayout.FormTextInset,
            -SettingsLayout.SpacingXS,
            24,
            16);
        hotkeysSection.Children.Add(hotkeyHint);

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

        _chkAutoStart.IsChecked = IsAutoStartEnabled();
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

        // 任一非法:全部回退到已保存值并提示
        if (!ValidateHotkey(screenshot, "截图快捷键") ||
            !ValidateHotkey(translation, "翻译快捷键") ||
            !ValidateHotkey(clipboard, "剪贴板快捷键") ||
            !ValidateHotkey(settings, "设置快捷键") ||
            !ValidateHotkey(lanTransfer, "传输快捷键"))
        {
            RestoreHotkeyText(config.Hotkeys);
            return;
        }

        var proposed = new Dictionary<string, string>
        {
            ["截图"] = HotkeyManager.NormalizeHotkey(screenshot)!,
            ["翻译"] = HotkeyManager.NormalizeHotkey(translation)!,
            ["剪贴板"] = HotkeyManager.NormalizeHotkey(clipboard)!,
            ["设置"] = HotkeyManager.NormalizeHotkey(settings)!,
            ["传输"] = HotkeyManager.NormalizeHotkey(lanTransfer)!
        };
        var duplicate = proposed
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            ToastNotification.Show(
                "快捷键重复",
                $"{string.Join("、", duplicate.Select(pair => pair.Key))} 都使用了 {duplicate.Key}",
                ToastNotification.ToastType.Warning);
            RestoreHotkeyText(config.Hotkeys);
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

                var details = string.Join("、", failures.Select(result => $"{result.FeatureName} {result.Hotkey}"));
                ToastNotification.Show(
                    "快捷键未生效",
                    $"{details} 已被其他程序占用，已恢复原设置。",
                    ToastNotification.ToastType.Warning);
                return;
            }

            RestoreHotkeyText(updated.Hotkeys);
            ToastNotification.ShowSettingsSaved();
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
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void RestoreHotkeyText(HotkeyConfig hotkeys)
    {
        _txtScreenshotHotkey.Text = hotkeys.Screenshot;
        _txtTranslationHotkey.Text = hotkeys.Translation;
        _txtClipboardHotkey.Text = hotkeys.Clipboard;
        _txtSettingsHotkey.Text = hotkeys.Settings;
        _txtLanTransferHotkey.Text = hotkeys.LanTransfer;
    }

    private static void ApplyHotkeys(HotkeyConfig target, IReadOnlyDictionary<string, string> values)
    {
        target.Screenshot = values["截图"];
        target.Translation = values["翻译"];
        target.Clipboard = values["剪贴板"];
        target.Settings = values["设置"];
        target.LanTransfer = values["传输"];
    }

    private static void ApplyHotkeys(HotkeyConfig target, HotkeyConfig source)
    {
        target.Screenshot = source.Screenshot;
        target.Translation = source.Translation;
        target.Clipboard = source.Clipboard;
        target.Settings = source.Settings;
        target.LanTransfer = source.LanTransfer;
    }

    private static bool ValidateHotkey(string hotkey, string label)
    {
        if (HotkeyManager.IsValidHotkey(hotkey))
            return true;

        ToastNotification.Show("快捷键格式无效", $"{label} 请使用类似 Ctrl+Alt+A 或 Ctrl+Shift+F1 的格式", ToastNotification.ToastType.Warning);
        return false;
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
            ToastNotification.ShowSettingsSaved();
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
            ToastNotification.ShowSettingsSaved();
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
