using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;

namespace STool.Views.Settings;

public class TranslationSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbSourceLanguage = null!;
    private System.Windows.Controls.ComboBox _cmbTargetLanguage = null!;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private System.Windows.Controls.PasswordBox _pwdTencentSecretKey = null!;

    // AI
    private System.Windows.Controls.TextBox _txtAiApiUrl = null!;
    private System.Windows.Controls.PasswordBox _pwdAiApiKey = null!;
    private System.Windows.Controls.TextBox _txtAiModel = null!;
    private StackPanel _activeSection = null!;

    public TranslationSettingsPanel(ConfigManager configManager)
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
            Text = "翻译设置",
            Style = (Style)FindResource("SettingsPageTitle")
        };
        Children.Add(title);

        _activeSection = CreateSection("服务商");

        // 提供商选择
        AddLabel("翻译提供商");
        _cmbProvider = new System.Windows.Controls.ComboBox
        {
            Width = 360,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8)
        };
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译 (OpenAI/Claude)", Tag = TranslationProvider.OpenAI });
        _activeSection.Children.Add(_cmbProvider);
        Children.Add(WrapSection(_activeSection));

        // 默认语言
        _activeSection = CreateSection("默认语言");

        AddLabel("源语言");
        _cmbSourceLanguage = new System.Windows.Controls.ComboBox
        {
            Width = 220,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8)
        };
        _cmbSourceLanguage.Items.Add(new ComboBoxItem { Content = "自动检测", Tag = "auto" });
        _cmbSourceLanguage.Items.Add(new ComboBoxItem { Content = "中文", Tag = "zh" });
        _cmbSourceLanguage.Items.Add(new ComboBoxItem { Content = "英文", Tag = "en" });
        _cmbSourceLanguage.Items.Add(new ComboBoxItem { Content = "日文", Tag = "ja" });
        _cmbSourceLanguage.Items.Add(new ComboBoxItem { Content = "韩文", Tag = "ko" });
        _activeSection.Children.Add(_cmbSourceLanguage);

        AddLabel("目标语言");
        _cmbTargetLanguage = new System.Windows.Controls.ComboBox
        {
            Width = 220,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8)
        };
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "中文", Tag = "zh" });
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "英文", Tag = "en" });
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "日文", Tag = "ja" });
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "韩文", Tag = "ko" });
        _activeSection.Children.Add(_cmbTargetLanguage);
        Children.Add(WrapSection(_activeSection));

        // 腾讯云设置
        _activeSection = CreateSection("腾讯云设置");

        AddLabel("Secret ID");
        _txtTencentSecretId = AddTextBox();

        AddLabel("Secret Key");
        _pwdTencentSecretKey = AddPasswordBox();
        Children.Add(WrapSection(_activeSection));

        // AI 设置
        _activeSection = CreateSection("AI 翻译设置");

        AddLabel("API URL");
        _txtAiApiUrl = AddTextBox();
        AddHint("例如：https://api.openai.com/v1/chat/completions");

        AddLabel("API Key");
        _pwdAiApiKey = AddPasswordBox();

        AddLabel("模型");
        _txtAiModel = AddTextBox();
        AddHint("例如：gpt-4o-mini, claude-3-5-haiku-20241022");
        Children.Add(WrapSection(_activeSection));

        // 保存按钮
        var btnSave = new System.Windows.Controls.Button
        {
            Content = "保存设置",
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
            Width = 420,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        _activeSection.Children.Add(textBox);
        return textBox;
    }

    private System.Windows.Controls.PasswordBox AddPasswordBox()
    {
        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            Width = 420,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
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

    private void LoadSettings()
    {
        var config = _configManager.Get().Translation;

        // 选择提供商
        foreach (ComboBoxItem item in _cmbProvider.Items)
        {
            if ((TranslationProvider)item.Tag == config.Provider)
            {
                _cmbProvider.SelectedItem = item;
                break;
            }
        }

        // 源语言
        foreach (ComboBoxItem item in _cmbSourceLanguage.Items)
        {
            if ((string)item.Tag == config.SourceLanguage)
            {
                _cmbSourceLanguage.SelectedItem = item;
                break;
            }
        }

        // 目标语言
        foreach (ComboBoxItem item in _cmbTargetLanguage.Items)
        {
            if ((string)item.Tag == config.TargetLanguage)
            {
                _cmbTargetLanguage.SelectedItem = item;
                break;
            }
        }

        // 腾讯云（解密显示）
        if (!string.IsNullOrEmpty(config.TencentSecretIdEncrypted))
        {
            _txtTencentSecretId.Text = SecureStorage.Decrypt(config.TencentSecretIdEncrypted);
        }
        if (!string.IsNullOrEmpty(config.TencentSecretKeyEncrypted))
        {
            _pwdTencentSecretKey.Password = SecureStorage.Decrypt(config.TencentSecretKeyEncrypted);
        }

        // AI（解密显示）
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

            config.Translation.Provider = (TranslationProvider)(_cmbProvider.SelectedItem as ComboBoxItem)!.Tag;
            config.Translation.SourceLanguage = (string)(_cmbSourceLanguage.SelectedItem as ComboBoxItem)!.Tag;
            config.Translation.TargetLanguage = (string)(_cmbTargetLanguage.SelectedItem as ComboBoxItem)!.Tag;

            // 腾讯云（加密保存）
            if (!string.IsNullOrWhiteSpace(_txtTencentSecretId.Text))
            {
                config.Translation.TencentSecretIdEncrypted = SecureStorage.Encrypt(_txtTencentSecretId.Text);
            }
            if (!string.IsNullOrWhiteSpace(_pwdTencentSecretKey.Password))
            {
                config.Translation.TencentSecretKeyEncrypted = SecureStorage.Encrypt(_pwdTencentSecretKey.Password);
            }

            // AI（加密保存）
            if (!string.IsNullOrWhiteSpace(_txtAiApiUrl.Text))
            {
                config.Translation.AiApiUrlEncrypted = SecureStorage.Encrypt(_txtAiApiUrl.Text);
            }
            if (!string.IsNullOrWhiteSpace(_pwdAiApiKey.Password))
            {
                config.Translation.AiApiKeyEncrypted = SecureStorage.Encrypt(_pwdAiApiKey.Password);
            }
            config.Translation.AiModel = _txtAiModel.Text;

            _configManager.Save(config);

            System.Windows.MessageBox.Show("设置已保存", "STool",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "STool",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
