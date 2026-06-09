using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using STool.Core;

namespace STool.Views.Settings;

public class GeneralSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.CheckBox _chkAutoStart = null!;
    private System.Windows.Controls.TextBox _txtScreenshotHotkey = null!;
    private System.Windows.Controls.TextBox _txtTranslationHotkey = null!;
    private System.Windows.Controls.TextBox _txtClipboardHotkey = null!;

    public GeneralSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // 标题
        var title = new TextBlock
        {
            Text = "通用设置",
            Style = (Style)FindResource("SettingsPageTitle")
        };
        Children.Add(title);

        var startupSection = CreateSection();
        startupSection.Children.Add(new TextBlock
        {
            Text = "启动",
            Style = (Style)FindResource("SettingsGroupTitle")
        });

        // 开机自启
        _chkAutoStart = new System.Windows.Controls.CheckBox
        {
            Content = "开机自动启动",
            Margin = new Thickness(0)
        };
        _chkAutoStart.Checked += ChkAutoStart_Changed;
        _chkAutoStart.Unchecked += ChkAutoStart_Changed;
        startupSection.Children.Add(_chkAutoStart);
        Children.Add(WrapSection(startupSection));

        var hotkeysSection = CreateSection();

        // 快捷键设置
        var hotkeySection = new TextBlock
        {
            Text = "快捷键设置",
            Style = (Style)FindResource("SettingsGroupTitle")
        };
        hotkeysSection.Children.Add(hotkeySection);

        // 截图快捷键
        AddHotkeyField(hotkeysSection, "截图", ref _txtScreenshotHotkey, showHint: true);

        // 翻译快捷键
        AddHotkeyField(hotkeysSection, "翻译", ref _txtTranslationHotkey, showHint: false);

        // 剪贴板快捷键
        AddHotkeyField(hotkeysSection, "剪贴板", ref _txtClipboardHotkey, showHint: false);

        // 保存按钮
        var btnSave = new System.Windows.Controls.Button
        {
            Content = "保存设置",
            Style = (Style)FindResource("ModernButton"),
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        btnSave.Click += BtnSave_Click;
        hotkeysSection.Children.Add(btnSave);
        Children.Add(WrapSection(hotkeysSection));

        // 添加底部占位，防止内容过少
        Children.Add(new Border { Height = 20 });
    }

    private StackPanel CreateSection()
    {
        return new StackPanel();
    }

    private Border WrapSection(StackPanel section)
    {
        // 色块分层:分组包成无边框白底卡片,靠柔和阴影从画布浮起
        return new Border
        {
            Style = (Style)FindResource("SurfaceCard"),
            Child = section
        };
    }

    private void AddHotkeyField(StackPanel parent, string label, ref System.Windows.Controls.TextBox textBox, bool showHint = true)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var labelBlock = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 4)
        };
        panel.Children.Add(labelBlock);

        textBox = new HotkeyBox
        {
            Style = (Style)FindResource("SunkenTextBox"),
            Height = 34,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        panel.Children.Add(textBox);

        if (showHint)
        {
            var hint = new TextBlock
            {
                Text = "点击输入框后，直接按下快捷键组合（如 Ctrl+Alt+A）",
                Style = (Style)FindResource("HintText"),
                Margin = new Thickness(0, 3, 0, 0)
            };
            panel.Children.Add(hint);
        }

        parent.Children.Add(panel);
    }

    private void LoadSettings()
    {
        var config = _configManager.Get();

        // 加载开机自启状态
        _chkAutoStart.IsChecked = IsAutoStartEnabled();

        // 加载快捷键
        _txtScreenshotHotkey.Text = config.Hotkeys.Screenshot;
        _txtTranslationHotkey.Text = config.Hotkeys.Translation;
        _txtClipboardHotkey.Text = config.Hotkeys.Clipboard;
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = _chkAutoStart.IsChecked == true;
        SetAutoStart(enabled);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ValidateHotkey(_txtScreenshotHotkey.Text, "截图快捷键") ||
                !ValidateHotkey(_txtTranslationHotkey.Text, "翻译快捷键") ||
                !ValidateHotkey(_txtClipboardHotkey.Text, "剪贴板快捷键"))
            {
                return;
            }

            var config = _configManager.Get();
            config.Hotkeys.Screenshot = _txtScreenshotHotkey.Text.Trim();
            config.Hotkeys.Translation = _txtTranslationHotkey.Text.Trim();
            config.Hotkeys.Clipboard = _txtClipboardHotkey.Text.Trim();

            _configManager.Save(config);
            ((App)System.Windows.Application.Current).ReloadHotkeys();

            ToastNotification.Show("设置已保存", "快捷键已更新", ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
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
        catch
        {
            return false;
        }
    }

    private void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

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
        }
        catch (Exception ex)
        {
            ToastNotification.Show("设置开机自启失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }
}
