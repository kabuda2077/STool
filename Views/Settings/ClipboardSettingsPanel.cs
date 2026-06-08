using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;

namespace STool.Views.Settings;

public class ClipboardSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.CheckBox _chkEnabled = null!;
    private System.Windows.Controls.TextBox _txtMaxEntries = null!;
    private System.Windows.Controls.TextBox _txtRetentionDays = null!;
    private System.Windows.Controls.TextBox _txtMaxImageSizeKB = null!;
    private StackPanel _activeSection = null!;

    public ClipboardSettingsPanel(ConfigManager configManager)
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
            Text = "剪贴板设置",
            Style = (Style)FindResource("SettingsPageTitle")
        };
        Children.Add(title);

        _activeSection = CreateSection("监听");

        // 启用开关
        _chkEnabled = new System.Windows.Controls.CheckBox
        {
            Content = "启用剪贴板监听",
            Margin = new Thickness(0, 0, 0, 8)
        };
        _activeSection.Children.Add(_chkEnabled);

        var hint1 = new TextBlock
        {
            Text = "保存后立即生效",
            Style = (Style)FindResource("HintText"),
            Margin = new Thickness(24, 0, 0, 12)
        };
        _activeSection.Children.Add(hint1);
        Children.Add(WrapSection(_activeSection));

        // 存储设置
        _activeSection = CreateSection("存储设置");

        AddLabel("最大条目数");
        _txtMaxEntries = AddTextBox();
        AddHint("超过此数量将自动删除最旧的条目（不包括收藏）");

        AddLabel("保留天数");
        _txtRetentionDays = AddTextBox();
        AddHint("超过此天数的条目将自动删除（不包括收藏）");

        AddLabel("最大图片大小 (KB)");
        _txtMaxImageSizeKB = AddTextBox();
        AddHint("超过此大小的图片将不会被保存");
        Children.Add(WrapSection(_activeSection));

        var actionPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };

        // 保存按钮
        var btnSave = new System.Windows.Controls.Button
        {
            Content = "保存设置",
            Style = (Style)FindResource("ModernButton"),
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        btnSave.Click += BtnSave_Click;
        actionPanel.Children.Add(btnSave);

        // 清空按钮
        var btnClear = new System.Windows.Controls.Button
        {
            Content = "清空所有历史记录",
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("DangerButton")
        };
        btnClear.Click += BtnClear_Click;
        actionPanel.Children.Add(btnClear);
        Children.Add(actionPanel);
    }

    private StackPanel CreateSection(string title)
    {
        var section = new StackPanel();
        section.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SettingsGroupTitle")
        });
        return section;
    }

    private Border WrapSection(StackPanel section)
    {
        return new Border
        {
            Style = (Style)FindResource("SettingsSection"),
            Child = section
        };
    }

    private void AddLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 7, 0, 4)
        };
        _activeSection.Children.Add(label);
    }

    private System.Windows.Controls.TextBox AddTextBox()
    {
        var textBox = new System.Windows.Controls.TextBox
        {
            Width = 280,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        _activeSection.Children.Add(textBox);
        return textBox;
    }

    private void AddHint(string text)
    {
        var hint = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("HintText"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        _activeSection.Children.Add(hint);
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Clipboard;

        _chkEnabled.IsChecked = config.Enabled;
        _txtMaxEntries.Text = config.MaxEntries.ToString();
        _txtRetentionDays.Text = config.RetentionDays.ToString();
        _txtMaxImageSizeKB.Text = config.MaxImageSizeKB.ToString();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _configManager.Get();

            config.Clipboard.Enabled = _chkEnabled.IsChecked == true;
            if (!TryReadPositiveInt(_txtMaxEntries, "最大条目数", out var maxEntries) ||
                !TryReadPositiveInt(_txtRetentionDays, "保留天数", out var retentionDays) ||
                !TryReadPositiveInt(_txtMaxImageSizeKB, "最大图片大小", out var maxImageSizeKB))
            {
                return;
            }

            config.Clipboard.MaxEntries = maxEntries;
            config.Clipboard.RetentionDays = retentionDays;
            config.Clipboard.MaxImageSizeKB = maxImageSizeKB;

            _configManager.Save(config);

            var clipboardManager = ((App)System.Windows.Application.Current)
                .GetService<STool.Modules.Clipboard.ClipboardManager>();
            if (clipboardManager != null)
            {
                if (config.Clipboard.Enabled)
                {
                    clipboardManager.Start();
                }
                else
                {
                    clipboardManager.Stop();
                }
            }

            ToastNotification.Show("设置已保存", "剪贴板设置已更新", ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private static bool TryReadPositiveInt(System.Windows.Controls.TextBox textBox, string label, out int value)
    {
        if (int.TryParse(textBox.Text.Trim(), out value) && value > 0)
        {
            return true;
        }

        ToastNotification.Show("输入无效", $"{label} 必须是大于 0 的整数", ToastNotification.ToastType.Warning);
        textBox.Focus();
        return false;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定要清空所有剪贴板历史记录吗？此操作无法撤销！",
            "STool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var clipboardManager = ((App)System.Windows.Application.Current)
                    .GetService<STool.Modules.Clipboard.ClipboardManager>();

                if (clipboardManager == null)
                {
                    ToastNotification.Show("清空失败", "剪贴板服务未初始化", ToastNotification.ToastType.Error);
                    return;
                }

                clipboardManager.ClearAll();

                ToastNotification.Show("已清空", "所有历史记录已删除", ToastNotification.ToastType.Success);
            }
            catch (Exception ex)
            {
                ToastNotification.Show("清空失败", ex.Message, ToastNotification.ToastType.Error);
            }
        }
    }
}
