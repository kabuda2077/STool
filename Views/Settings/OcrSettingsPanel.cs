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
    private System.Windows.Controls.ComboBox _cmbAiPlatform = null!;
    private System.Windows.Controls.TextBox _txtAiApiUrl = null!;
    private System.Windows.Controls.PasswordBox _pwdAiApiKey = null!;
    private System.Windows.Controls.TextBox _txtAiModel = null!;

    public OcrSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 基础选项 ──
        var baseSection = new StackPanel();
        baseSection.Children.Add(new TextBlock
        {
            Text = "基础选项",
            Style = (Style)FindResource("SettingsGroupTitle")
        });

        baseSection.Children.Add(new TextBlock
        {
            Text = "OCR 提供商",
            Style = (Style)FindResource("FieldLabel")
        });
        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Margin = SettingsLayout.FieldSpacing;
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "Windows 本地 OCR", Tag = OcrProvider.WindowsLocal });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云 OCR", Tag = OcrProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI Vision OCR", Tag = OcrProvider.AI });
        _cmbProvider.SelectionChanged += CmbProvider_SelectionChanged;
        baseSection.Children.Add(_cmbProvider);

        _chkFallbackToLocal = new System.Windows.Controls.CheckBox
        {
            Content = "失败时自动降级到本地 OCR",
            Style = (Style)FindResource("ModernCheckBox")
        };
        baseSection.Children.Add(_chkFallbackToLocal);
        Children.Add(WrapSection(baseSection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard) = CreateCollapsibleSection("腾讯云设置");

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        var (tencentPwdHost, tencentPwd) = SettingsLayout.CreatePasswordField();
        _pwdTencentSecretKey = tencentPwd;
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", tencentPwdHost));

        Children.Add(tencentCard);

        // ── AI Vision 设置(可折叠,行内布局) ──
        var (aiContent, aiCard) = CreateCollapsibleSection("AI Vision 设置");

        _cmbAiPlatform = SettingsLayout.CreateComboBox();
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "OpenAI", Tag = OcrAiPlatform.OpenAI });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "Google AI Studio", Tag = OcrAiPlatform.GoogleAiStudio });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "自定义", Tag = OcrAiPlatform.Custom });
        _cmbAiPlatform.SelectionChanged += CmbAiPlatform_SelectionChanged;
        aiContent.Children.Add(SettingsLayout.CreateInlineField("平台", _cmbAiPlatform));

        _txtAiApiUrl = SettingsLayout.CreateTextBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineField("API URL", _txtAiApiUrl));
        aiContent.Children.Add(SettingsLayout.CreateHint("OpenAI 兼容 Chat Completions 地址，自定义接口需手动填写。", inline: true));

        var (aiPwdHost, aiPwd) = SettingsLayout.CreatePasswordField();
        _pwdAiApiKey = aiPwd;
        aiContent.Children.Add(SettingsLayout.CreateInlineField("API Key", aiPwdHost));

        _txtAiModel = SettingsLayout.CreateTextBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineField("模型", _txtAiModel));
        aiContent.Children.Add(SettingsLayout.CreateHint("例如：gpt-4o-mini, gemini-1.5-flash", inline: true));

        Children.Add(aiCard);

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
    }

    private Border WrapSection(StackPanel section)
    {
        return new Border
        {
            Style = (Style)FindResource("SurfaceCard"),
            Child = section
        };
    }

    private (StackPanel content, Border card) CreateCollapsibleSection(string title)
    {
        var content = new StackPanel();
        var exp = new Expander
        {
            Style = (Style)FindResource("SettingsExpander"),
            Header = title,
            Content = content,
            IsExpanded = false
        };
        var card = new Border
        {
            Style = (Style)FindResource("SurfaceCard"),
            Child = exp
        };
        return (content, card);
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
        foreach (ComboBoxItem item in _cmbAiPlatform.Items)
        {
            if ((OcrAiPlatform)item.Tag == config.AiPlatform)
            {
                _cmbAiPlatform.SelectedItem = item;
                break;
            }
        }
        _cmbAiPlatform.SelectedIndex = _cmbAiPlatform.SelectedIndex < 0 ? 0 : _cmbAiPlatform.SelectedIndex;

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
        SaveSettings();
    }

    public void SaveSettings()
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
            config.Ocr.AiPlatform = GetSelectedAiPlatform();
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

    private void CmbAiPlatform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var platform = GetSelectedAiPlatform();
        if (platform == OcrAiPlatform.Custom)
        {
            return;
        }

        _txtAiApiUrl.Text = platform switch
        {
            OcrAiPlatform.OpenAI => "https://api.openai.com/v1/chat/completions",
            OcrAiPlatform.GoogleAiStudio => "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            _ => string.Empty
        };

        _txtAiModel.Text = platform switch
        {
            OcrAiPlatform.OpenAI => "gpt-4o-mini",
            OcrAiPlatform.GoogleAiStudio => "gemini-1.5-flash",
            _ => string.Empty
        };
    }

    private OcrAiPlatform GetSelectedAiPlatform()
    {
        return (_cmbAiPlatform.SelectedItem as ComboBoxItem)?.Tag is OcrAiPlatform platform
            ? platform
            : OcrAiPlatform.Custom;
    }
}
