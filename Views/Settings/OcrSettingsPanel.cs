using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;

namespace STool.Views.Settings;

public class OcrSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.CheckBox _chkFallbackToLocal = null!;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private System.Windows.Controls.PasswordBox _pwdTencentSecretKey = null!;

    // AI Vision
    private System.Windows.Controls.TextBox _txtAiApiUrl = null!;
    private System.Windows.Controls.PasswordBox _pwdAiApiKey = null!;
    private System.Windows.Controls.TextBox _txtAiModel = null!;
    private StackPanel _activeSection = null!;

    public OcrSettingsPanel(ConfigManager configManager)
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
            Text = "OCR 设置",
            Style = (Style)FindResource("SettingsPageTitle")
        };
        Children.Add(title);

        _activeSection = CreateSection("基础选项");

        // 提供商选择
        AddLabel("OCR 提供商");
        _cmbProvider = new System.Windows.Controls.ComboBox
        {
            MinHeight = 36,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 8)
        };
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "Windows 本地 OCR", Tag = OcrProvider.WindowsLocal });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云 OCR", Tag = OcrProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI Vision OCR", Tag = OcrProvider.AI });
        _cmbProvider.SelectionChanged += CmbProvider_SelectionChanged;
        _activeSection.Children.Add(_cmbProvider);

        // 降级到本地
        _chkFallbackToLocal = new System.Windows.Controls.CheckBox
        {
            Content = "失败时自动降级到本地 OCR",
            Margin = new Thickness(0, 0, 0, 14)
        };
        _activeSection.Children.Add(_chkFallbackToLocal);
        Children.Add(WrapSection(_activeSection));

        // 腾讯云设置
        _activeSection = CreateSection("腾讯云设置");

        AddLabel("Secret ID");
        _txtTencentSecretId = AddTextBox();

        AddLabel("Secret Key");
        _pwdTencentSecretKey = AddPasswordBox();
        Children.Add(WrapSection(_activeSection));

        // AI Vision 设置
        _activeSection = CreateSection("AI Vision 设置");

        AddLabel("API URL");
        _txtAiApiUrl = AddTextBox();
        AddHint("例如：https://api.openai.com/v1/chat/completions");

        AddLabel("API Key");
        _pwdAiApiKey = AddPasswordBox();

        AddLabel("模型");
        _txtAiModel = AddTextBox();
        AddHint("例如：gpt-4o, claude-3-5-sonnet-20241022");
        Children.Add(WrapSection(_activeSection));

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
        Children.Add(btnSave);
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
            MinHeight = 36,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        _activeSection.Children.Add(textBox);
        return textBox;
    }

    private System.Windows.Controls.PasswordBox AddPasswordBox()
    {
        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            MinHeight = 36,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        _activeSection.Children.Add(passwordBox);
        return passwordBox;
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

    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 可以根据选择显示/隐藏相关设置
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Ocr;

        // 选择提供商
        foreach (ComboBoxItem item in _cmbProvider.Items)
        {
            if ((OcrProvider)item.Tag == config.Provider)
            {
                _cmbProvider.SelectedItem = item;
                break;
            }
        }

        _chkFallbackToLocal.IsChecked = config.FallbackToLocal;

        // 腾讯云（解密显示）
        if (!string.IsNullOrEmpty(config.TencentSecretIdEncrypted))
        {
            _txtTencentSecretId.Text = SecureStorage.Decrypt(config.TencentSecretIdEncrypted);
        }
        if (!string.IsNullOrEmpty(config.TencentSecretKeyEncrypted))
        {
            _pwdTencentSecretKey.Password = SecureStorage.Decrypt(config.TencentSecretKeyEncrypted);
        }

        // AI Vision（解密显示）
        if (!string.IsNullOrEmpty(config.AiApiUrlEncrypted))
        {
            _txtAiApiUrl.Text = SecureStorage.Decrypt(config.AiApiUrlEncrypted);
        }
        if (!string.IsNullOrEmpty(config.AiApiKeyEncrypted))
        {
            _pwdAiApiKey.Password = SecureStorage.Decrypt(config.AiApiKeyEncrypted);
        }
        _txtAiModel.Text = config.AiModel ?? "";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _configManager.Get();

            config.Ocr.Provider = (OcrProvider)(_cmbProvider.SelectedItem as ComboBoxItem)!.Tag;
            config.Ocr.FallbackToLocal = _chkFallbackToLocal.IsChecked == true;

            // 腾讯云（加密保存）
            if (!string.IsNullOrWhiteSpace(_txtTencentSecretId.Text))
            {
                config.Ocr.TencentSecretIdEncrypted = SecureStorage.Encrypt(_txtTencentSecretId.Text);
            }
            if (!string.IsNullOrWhiteSpace(_pwdTencentSecretKey.Password))
            {
                config.Ocr.TencentSecretKeyEncrypted = SecureStorage.Encrypt(_pwdTencentSecretKey.Password);
            }

            // AI Vision（加密保存）
            if (!string.IsNullOrWhiteSpace(_txtAiApiUrl.Text))
            {
                config.Ocr.AiApiUrlEncrypted = SecureStorage.Encrypt(_txtAiApiUrl.Text);
            }
            if (!string.IsNullOrWhiteSpace(_pwdAiApiKey.Password))
            {
                config.Ocr.AiApiKeyEncrypted = SecureStorage.Encrypt(_pwdAiApiKey.Password);
            }
            config.Ocr.AiModel = _txtAiModel.Text;

            _configManager.Save(config);

            ToastNotification.Show("设置已保存", "OCR 设置已更新", ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }
}
