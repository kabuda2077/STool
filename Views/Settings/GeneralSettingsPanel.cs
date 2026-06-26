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
        var launchSection = new StackPanel();
        launchSection.Children.Add(new TextBlock
        {
            Text = "启动与托盘",
            Style = (Style)FindResource("SettingsGroupTitle")
        });

        _chkAutoStart = new System.Windows.Controls.CheckBox
        {
            Content = "开机自动启动",
            Style = (Style)FindResource("ModernCheckBox"),
            Margin = new Thickness(0, 0, 0, SettingsLayout.SpacingSM)
        };
        _chkAutoStart.Click += ChkAutoStart_Changed;
        launchSection.Children.Add(_chkAutoStart);

        _chkHideTrayIcon = new System.Windows.Controls.CheckBox
        {
            Content = "隐藏托盘图标",
            Style = (Style)FindResource("ModernCheckBox"),
            Margin = new Thickness(0, 0, 0, SettingsLayout.SpacingSM)
        };
        launchSection.Children.Add(_chkHideTrayIcon);
        launchSection.Children.Add(new TextBlock
        {
            Text = "隐藏后仍可通过快捷键打开功能面板，重新显示可在本窗口取消勾选。",
            Style = (Style)FindResource("HintText"),
            Margin = new Thickness(0)
        });

        Children.Add(WrapSection(launchSection));

        // ── 快捷键设置(紧凑双列) ──
        var hotkeysSection = new StackPanel();
        hotkeysSection.Children.Add(new TextBlock
        {
            Text = "快捷键设置",
            Style = (Style)FindResource("SettingsGroupTitle")
        });
        hotkeysSection.Children.Add(new TextBlock
        {
            Text = "点击输入框后，直接按下快捷键组合（如 Ctrl+Alt+A）",
            Style = (Style)FindResource("HintText"),
            Margin = new Thickness(0, 0, 0, SettingsLayout.SpacingSM)
        });

        var hotkeyGrid = new Grid();
        hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SettingsLayout.HotkeyLabelWidth) });
        hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _txtScreenshotHotkey = AddHotkeyRow(hotkeyGrid, 0, "截图");
        _txtTranslationHotkey = AddHotkeyRow(hotkeyGrid, 1, "翻译");
        _txtClipboardHotkey = AddHotkeyRow(hotkeyGrid, 2, "剪贴板");
        _txtSettingsHotkey = AddHotkeyRow(hotkeyGrid, 3, "设置");

        hotkeysSection.Children.Add(hotkeyGrid);
        Children.Add(WrapSection(hotkeysSection));

        // ── 保存按钮 ──
        var btnSave = new System.Windows.Controls.Button
        {
            Content = "保存设置",
            Style = (Style)FindResource("ModernButton"),
            Padding = new Thickness(18, 8, 18, 8),
            Margin = SettingsLayout.SaveButtonMargin,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        btnSave.Click += BtnSave_Click;
        Children.Add(btnSave);

        Children.Add(new Border { Height = 20 });
    }

    private Border WrapSection(StackPanel section)
    {
        return new Border
        {
            Style = (Style)FindResource("SurfaceCard"),
            Child = section
        };
    }

    private System.Windows.Controls.TextBox AddHotkeyRow(Grid grid, int row, string label)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var box = new HotkeyBox
        {
            Style = (Style)FindResource("HotkeyTextBox"),
            Height = SettingsLayout.InputHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, SettingsLayout.SpacingXS)
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

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = _chkAutoStart.IsChecked == true;
        SetAutoStart(enabled);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    public void SaveSettings()
    {
        try
        {
            if (!ValidateHotkey(_txtScreenshotHotkey.Text, "截图快捷键") ||
                !ValidateHotkey(_txtTranslationHotkey.Text, "翻译快捷键") ||
                !ValidateHotkey(_txtClipboardHotkey.Text, "剪贴板快捷键") ||
                !ValidateHotkey(_txtSettingsHotkey.Text, "设置快捷键"))
            {
                return;
            }

            var config = _configManager.Get();
            config.Hotkeys.Screenshot = _txtScreenshotHotkey.Text.Trim();
            config.Hotkeys.Translation = _txtTranslationHotkey.Text.Trim();
            config.Hotkeys.Clipboard = _txtClipboardHotkey.Text.Trim();
            config.Hotkeys.Settings = _txtSettingsHotkey.Text.Trim();
            config.HideTrayIcon = _chkHideTrayIcon.IsChecked == true;

            _configManager.Save(config);
            ((App)System.Windows.Application.Current).ReloadHotkeys();
            ((App)System.Windows.Application.Current).ReloadTrayIconVisibility();

            ToastNotification.Show("设置已保存", "通用设置已更新", ToastNotification.ToastType.Success);
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read auto-start registry value");
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
