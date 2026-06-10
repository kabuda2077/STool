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
            Style = (Style)FindResource("SunkenComboBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 7)
        };
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "谷歌翻译", Tag = TranslationProvider.Google });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译 (OpenAI/Claude)", Tag = TranslationProvider.OpenAI });
        _activeSection.Children.Add(_cmbProvider);
        Children.Add(WrapSection(_activeSection));

        // 默认语言
        _activeSection = CreateSection("默认语言");

        AddLabel("源语言");
        _cmbSourceLanguage = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("SunkenComboBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 7)
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
            Style = (Style)FindResource("SunkenComboBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 7)
        };
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "中文", Tag = "zh" });
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "英文", Tag = "en" });
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "日文", Tag = "ja" });
        _cmbTargetLanguage.Items.Add(new ComboBoxItem { Content = "韩文", Tag = "ko" });
        _activeSection.Children.Add(_cmbTargetLanguage);
        Children.Add(WrapSection(_activeSection));

        // 腾讯云设置
        var tencentSection = CreateCollapsibleSection("腾讯云设置");

        AddLabel("Secret ID");
        _txtTencentSecretId = AddTextBox();

        AddLabel("Secret Key");
        _pwdTencentSecretKey = AddPasswordBox();
        Children.Add(tencentSection);

        // AI 设置(可折叠)
        var aiSection = CreateCollapsibleSection("AI 翻译设置");

        AddLabel("API URL");
        _txtAiApiUrl = AddTextBox();
        AddHint("例如：https://api.openai.com/v1/chat/completions");

        AddLabel("API Key");
        _pwdAiApiKey = AddPasswordBox();

        AddLabel("模型");
        _txtAiModel = AddTextBox();
        AddHint("例如：gpt-4o-mini, claude-3-5-haiku-20241022");
        Children.Add(aiSection);

        // 保存按钮
        var btnSave = new System.Windows.Controls.Button
        {
            Content = "保存设置",
            Style = (Style)FindResource("ModernButton"),
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 10, 0, 0),
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
            Style = (Style)FindResource("SurfaceCard"),
            Child = section
        };
    }

    /// <summary>创建可折叠分组(默认收起),后续 Add* 写入其内容区;整组包成阴影卡片。</summary>
    private Border CreateCollapsibleSection(string title)
    {
        var content = new StackPanel();
        _activeSection = content;
        var exp = new Expander
        {
            Style = (Style)FindResource("SettingsExpander"),
            Header = title,
            Content = content,
            IsExpanded = false
        };
        return new Border
        {
            Style = (Style)FindResource("SurfaceCard"),
            Child = exp
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
            Style = (Style)FindResource("SunkenTextBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        _activeSection.Children.Add(textBox);
        return textBox;
    }

    private System.Windows.Controls.PasswordBox AddPasswordBox()
    {
        var inputHost = new Grid
        {
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };

        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            Style = (Style)FindResource("SunkenPasswordBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Padding = new Thickness(5, 3, 38, 3)
        };

        var textBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)FindResource("SunkenTextBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Padding = new Thickness(5, 3, 38, 3),
            Visibility = Visibility.Collapsed
        };

        var revealButton = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "显示密钥",
            Content = new TextBlock
            {
                Text = "\uE890",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
            }
        };

        var isRevealed = false;
        var isSyncing = false;

        passwordBox.PasswordChanged += (_, _) =>
        {
            if (isSyncing || isRevealed) return;
            isSyncing = true;
            textBox.Text = passwordBox.Password;
            isSyncing = false;
        };

        textBox.TextChanged += (_, _) =>
        {
            if (isSyncing || !isRevealed) return;
            isSyncing = true;
            passwordBox.Password = textBox.Text;
            isSyncing = false;
        };

        revealButton.Click += (_, _) =>
        {
            isRevealed = !isRevealed;
            if (isRevealed)
            {
                textBox.Text = passwordBox.Password;
                passwordBox.Visibility = Visibility.Collapsed;
                textBox.Visibility = Visibility.Visible;
                revealButton.ToolTip = "隐藏密钥";
                textBox.Focus();
                textBox.CaretIndex = textBox.Text.Length;
            }
            else
            {
                passwordBox.Password = textBox.Text;
                textBox.Visibility = Visibility.Collapsed;
                passwordBox.Visibility = Visibility.Visible;
                revealButton.ToolTip = "显示密钥";
                passwordBox.Focus();
            }
        };

        inputHost.Children.Add(passwordBox);
        inputHost.Children.Add(textBox);
        inputHost.Children.Add(revealButton);
        _activeSection.Children.Add(inputHost);
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

            ToastNotification.Show("设置已保存", "翻译设置已更新", ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }
}
