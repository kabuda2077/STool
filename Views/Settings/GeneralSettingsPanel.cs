using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using STool.Core;

namespace STool.Views.Settings;

public class GeneralSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.CheckBox _chkAutoStart = null!;
    private System.Windows.Controls.CheckBox _chkHideTrayIcon = null!;
    private System.Windows.Controls.TextBox _txtScreenshotHotkey = null!;
    private System.Windows.Controls.TextBox _txtTranslationHotkey = null!;
    private System.Windows.Controls.TextBox _txtClipboardHotkey = null!;
    private System.Windows.Controls.TextBox _txtSettingsHotkey = null!;

    public GeneralSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
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
                {
                    FlashSaved();
                }
                else
                {
                    _chkAutoStart.IsChecked = !enabled;
                }
            }));

        _chkHideTrayIcon = SettingsLayout.CreateSwitch();
        launchSection.Children.Add(SettingsLayout.CreateSwitchRow(
            "隐藏托盘图标",
            "仍可用快捷键唤出",
            _chkHideTrayIcon,
            SaveHideTrayIcon,
            isLast: true));

        Children.Add(WrapSection(launchSection));

        // ── 快捷键设置(紧凑双列) ──
        var hotkeysSection = SettingsLayout.CreateSectionContent("快捷键设置");
        _txtScreenshotHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("截图", _txtScreenshotHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtTranslationHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("翻译", _txtTranslationHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtClipboardHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("剪贴板", _txtClipboardHotkey, SettingsLayout.HotkeyLabelWidth));

        _txtSettingsHotkey = CreateHotkeyBox();
        hotkeysSection.Children.Add(SettingsLayout.CreateInlineField("设置", _txtSettingsHotkey, SettingsLayout.HotkeyLabelWidth, isLast: true));

        // 即时保存:快捷键录制完(失焦)自动校验并保存
        foreach (var box in new[] { _txtScreenshotHotkey, _txtTranslationHotkey, _txtClipboardHotkey, _txtSettingsHotkey })
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

        Children.Add(WrapSection(hotkeysSection));

        Children.Add(new Border { Height = 12 });
    }

    private Border WrapSection(StackPanel section)
    {
        return SettingsLayout.CreateSection(section);
    }

    private System.Windows.Controls.TextBox CreateHotkeyBox()
    {
        return new HotkeyBox
        {
            Style = (Style)FindResource("HotkeyTextBox"),
            Height = SettingsLayout.InputHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
    }

    private void LoadSettings()
    {
        var config = _configManager.Get();

        // 加载开机自启状态
        _chkAutoStart.IsChecked = IsAutoStartEnabled();
        _chkHideTrayIcon.IsChecked = config.HideTrayIcon;

        // 加载快捷键
        _txtScreenshotHotkey.Text = config.Hotkeys.Screenshot;
        _txtTranslationHotkey.Text = config.Hotkeys.Translation;
        _txtClipboardHotkey.Text = config.Hotkeys.Clipboard;
        _txtSettingsHotkey.Text = config.Hotkeys.Settings;
    }

    /// <summary>快捷键录制完(失焦)时:有变化才校验并保存,非法则回退到已保存值。</summary>
    private void CommitHotkeysIfChanged()
    {
        var config = _configManager.Get();
        var screenshot = _txtScreenshotHotkey.Text.Trim();
        var translation = _txtTranslationHotkey.Text.Trim();
        var clipboard = _txtClipboardHotkey.Text.Trim();
        var settings = _txtSettingsHotkey.Text.Trim();

        // 无变化则不动,避免无意义的保存和"已保存"闪烁
        if (screenshot == config.Hotkeys.Screenshot &&
            translation == config.Hotkeys.Translation &&
            clipboard == config.Hotkeys.Clipboard &&
            settings == config.Hotkeys.Settings)
        {
            return;
        }

        // 任一非法:全部回退到已保存值并提示
        if (!ValidateHotkey(screenshot, "截图快捷键") ||
            !ValidateHotkey(translation, "翻译快捷键") ||
            !ValidateHotkey(clipboard, "剪贴板快捷键") ||
            !ValidateHotkey(settings, "设置快捷键"))
        {
            _txtScreenshotHotkey.Text = config.Hotkeys.Screenshot;
            _txtTranslationHotkey.Text = config.Hotkeys.Translation;
            _txtClipboardHotkey.Text = config.Hotkeys.Clipboard;
            _txtSettingsHotkey.Text = config.Hotkeys.Settings;
            return;
        }

        try
        {
            config.Hotkeys.Screenshot = screenshot;
            config.Hotkeys.Translation = translation;
            config.Hotkeys.Clipboard = clipboard;
            config.Hotkeys.Settings = settings;

            _configManager.Save(config);
            ((App)System.Windows.Application.Current).ReloadHotkeys();

            FlashSaved();
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    /// <summary>即时保存成功后在设置窗口中下部显示轻量提示。</summary>
    private void FlashSaved()
    {
        ToastNotification.Show(
            "设置已保存",
            type: ToastNotification.ToastType.Success,
            duration: 1600);
    }

    private static bool ValidateHotkey(string hotkey, string label)
    {
        if (HotkeyManager.IsValidHotkey(hotkey))
        {
            return true;
        }

        ToastNotification.Show("快捷键格式无效", $"{label} 请使用类似 Ctrl+Alt+A 或 Ctrl+Shift+F1 的格式", ToastNotification.ToastType.Warning);
        return false;
    }

    private bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("STool") != null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read auto-start registry value");
            return false;
        }
    }

    private void SaveHideTrayIcon(bool enabled)
    {
        try
        {
            var config = _configManager.Get();
            config.HideTrayIcon = enabled;
            _configManager.Save(config);
            ((App)System.Windows.Application.Current).ReloadTrayIconVisibility();
            FlashSaved();
        }
        catch (Exception ex)
        {
            _chkHideTrayIcon.IsChecked = !enabled;
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private bool SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return false;

            if (enabled)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    key.SetValue("STool", exePath);
                }
            }
            else
            {
                key.DeleteValue("STool", false);
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
