using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
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
    private TextBlock _savedIndicator = null!;

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
        var launchSection = new StackPanel();
        launchSection.Children.Add(new TextBlock
        {
            Text = "启动与托盘",
            Style = (Style)FindResource("SettingsGroupTitle")
        });

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
        var hotkeysSection = new StackPanel();
        hotkeysSection.Children.Add(new TextBlock
        {
            Text = "快捷键设置",
            Style = (Style)FindResource("SettingsGroupTitle")
        });
        var hotkeyGrid = new Grid();
        hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SettingsLayout.HotkeyLabelWidth) });
        hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _txtScreenshotHotkey = AddHotkeyRow(hotkeyGrid, 0, "截图");
        _txtTranslationHotkey = AddHotkeyRow(hotkeyGrid, 1, "翻译");
        _txtClipboardHotkey = AddHotkeyRow(hotkeyGrid, 2, "剪贴板");
        _txtSettingsHotkey = AddHotkeyRow(hotkeyGrid, 3, "设置", isLast: true);

        // 即时保存:快捷键录制完(失焦)自动校验并保存
        foreach (var box in new[] { _txtScreenshotHotkey, _txtTranslationHotkey, _txtClipboardHotkey, _txtSettingsHotkey })
        {
            box.LostKeyboardFocus += (_, _) => CommitHotkeysIfChanged();
        }

        hotkeysSection.Children.Add(hotkeyGrid);

        var hotkeyHint = SettingsLayout.CreateHint("点击后直接按快捷键组合");
        hotkeyHint.Margin = new Thickness(SettingsLayout.HotkeyLabelWidth, SettingsLayout.InputHintSpacing, 0, 0);
        hotkeysSection.Children.Add(hotkeyHint);

        Children.Add(WrapSection(hotkeysSection));

        // ── 即时保存反馈:成功后右下角轻闪,随后淡出 ──
        _savedIndicator = new TextBlock
        {
            Text = "✓ 已保存",
            FontSize = SettingsLayout.BodyFontSize,
            Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, SettingsLayout.SpacingSM, 2, 0),
            Opacity = 0
        };
        Children.Add(_savedIndicator);

        Children.Add(new Border { Height = 12 });
    }

    private Border WrapSection(StackPanel section)
    {
        return SettingsLayout.CreateSection(section);
    }

    private System.Windows.Controls.TextBox AddHotkeyRow(Grid grid, int row, string label, bool isLast = false)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bottomSpacing = isLast ? 0 : SettingsLayout.SpacingMD;

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        var labelHost = new Border
        {
            Height = SettingsLayout.InputHeight,
            Margin = new Thickness(0, 0, 0, bottomSpacing),
            Child = lbl
        };
        Grid.SetRow(labelHost, row);
        Grid.SetColumn(labelHost, 0);
        grid.Children.Add(labelHost);

        var box = new HotkeyBox
        {
            Style = (Style)FindResource("HotkeyTextBox"),
            Height = SettingsLayout.InputHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, bottomSpacing)
        };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        return box;
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

    /// <summary>即时保存成功后的轻量反馈:显示"已保存"并淡出。</summary>
    private void FlashSaved()
    {
        _savedIndicator.BeginAnimation(OpacityProperty, null);
        _savedIndicator.Opacity = 1;
        _savedIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 1,
            To = 0,
            BeginTime = TimeSpan.FromSeconds(1.1),
            Duration = TimeSpan.FromSeconds(0.7),
            FillBehavior = FillBehavior.HoldEnd
        });
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
