using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Modules.Translation;
using STool.Models;

namespace STool.Views.Settings;

public class TranslationSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbTranslationMode = null!;
    private System.Windows.Controls.ComboBox _cmbScreenshotMode = null!;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private System.Windows.Controls.PasswordBox _pwdTencentSecretKey = null!;

    // AI
    private System.Windows.Controls.ComboBox _cmbAiPlatform = null!;
    private System.Windows.Controls.TextBox _txtAiApiUrl = null!;
    private System.Windows.Controls.PasswordBox _pwdAiApiKey = null!;
    private System.Windows.Controls.ComboBox _cmbAiModel = null!;
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

        // 默认策略
        _activeSection = CreateSection("默认策略");

        AddLabel("翻译策略");
        _cmbTranslationMode = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("SunkenComboBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 7)
        };
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "中 ⇄ 英", Tag = "zh-en" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 中文", Tag = "auto-zh" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 英文", Tag = "auto-en" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 日文", Tag = "auto-ja" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 韩文", Tag = "auto-ko" });
        _activeSection.Children.Add(_cmbTranslationMode);

        AddLabel("截图翻译模式");
        _cmbScreenshotMode = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("SunkenComboBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 3, 0, 7)
        };
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "快速：规则筛选 + 当前翻译引擎", Tag = ScreenshotTranslationMode.Fast });
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "智能：AI 选择并翻译正文", Tag = ScreenshotTranslationMode.Smart });
        _activeSection.Children.Add(_cmbScreenshotMode);
        AddHint("智能模式会额外使用 AI 翻译配置，失败时自动回退快速模式。");
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

        AddLabel("平台");
        _cmbAiPlatform = AddComboBox();
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "OpenAI", Tag = TranslationAiPlatform.OpenAI });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "Google AI Studio", Tag = TranslationAiPlatform.GoogleAiStudio });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "DeepSeek", Tag = TranslationAiPlatform.DeepSeek });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "自定义", Tag = TranslationAiPlatform.Custom });
        _cmbAiPlatform.SelectionChanged += CmbAiPlatform_SelectionChanged;

        AddLabel("API URL");
        _txtAiApiUrl = AddTextBox();
        AddHint("OpenAI 兼容 Chat Completions 地址，自定义接口需手动填写。");

        AddLabel("API Key");
        _pwdAiApiKey = AddPasswordBox();

        AddLabel("模型");
        _cmbAiModel = AddEditableComboBox();
        AddHint("可点击获取模型列表，也可以直接手动输入模型名。");

        var aiActions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var btnFetchModels = new System.Windows.Controls.Button
        {
            Content = "获取模型",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(14, 7, 14, 7)
        };
        btnFetchModels.Click += BtnFetchModels_Click;
        var btnTestAi = new System.Windows.Controls.Button
        {
            Content = "测试",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(8, 0, 0, 0)
        };
        btnTestAi.Click += BtnTestAi_Click;
        aiActions.Children.Add(btnFetchModels);
        aiActions.Children.Add(btnTestAi);
        _activeSection.Children.Add(aiActions);
        Children.Add(aiSection);

        // 保存按钮
        var btnSave = new System.Windows.Controls.Button
        {
            Content = "保存设置",
            Style = (Style)FindResource("ModernButton"),
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
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

    private System.Windows.Controls.ComboBox AddComboBox()
    {
        var comboBox = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("SunkenComboBox"),
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        _activeSection.Children.Add(comboBox);
        return comboBox;
    }

    private System.Windows.Controls.ComboBox AddEditableComboBox()
    {
        var comboBox = AddComboBox();
        comboBox.IsEditable = true;
        comboBox.IsTextSearchEnabled = false;
        return comboBox;
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

    private void CmbAiPlatform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var platform = GetSelectedAiPlatform();
        if (platform == TranslationAiPlatform.Custom)
        {
            return;
        }

        _txtAiApiUrl.Text = AiTranslationService.GetDefaultApiUrl(platform);
    }

    private async void BtnFetchModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var models = await AiTranslationService.FetchModelsAsync(_txtAiApiUrl.Text, _pwdAiApiKey.Password);
            var currentModel = GetAiModel();

            _cmbAiModel.Items.Clear();
            foreach (var model in models)
            {
                _cmbAiModel.Items.Add(model);
            }

            if (!string.IsNullOrWhiteSpace(currentModel))
            {
                _cmbAiModel.Text = currentModel;
            }
            else if (models.Count > 0)
            {
                _cmbAiModel.Text = models[0];
            }

            ToastNotification.Show("模型已获取", $"共 {models.Count} 个模型", ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("获取模型失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private async void BtnTestAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await AiTranslationService.TestAsync(_txtAiApiUrl.Text, _pwdAiApiKey.Password, GetAiModel());
            if (result.Success)
            {
                ToastNotification.Show("测试成功", result.TranslatedText, ToastNotification.ToastType.Success);
            }
            else
            {
                ToastNotification.Show("测试失败", result.ErrorMessage ?? "未知错误", ToastNotification.ToastType.Error);
            }
        }
        catch (Exception ex)
        {
            ToastNotification.Show("测试失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private TranslationAiPlatform GetSelectedAiPlatform()
    {
        return (_cmbAiPlatform.SelectedItem as ComboBoxItem)?.Tag is TranslationAiPlatform platform
            ? platform
            : TranslationAiPlatform.Custom;
    }

    private string GetAiModel()
    {
        return _cmbAiModel.Text.Trim();
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

        // 翻译策略
        foreach (ComboBoxItem item in _cmbTranslationMode.Items)
        {
            if ((string)item.Tag == config.TranslationMode)
            {
                _cmbTranslationMode.SelectedItem = item;
                break;
            }
        }
        _cmbTranslationMode.SelectedIndex = _cmbTranslationMode.SelectedIndex < 0 ? 0 : _cmbTranslationMode.SelectedIndex;

        foreach (ComboBoxItem item in _cmbScreenshotMode.Items)
        {
            if ((ScreenshotTranslationMode)item.Tag == config.ScreenshotMode)
            {
                _cmbScreenshotMode.SelectedItem = item;
                break;
            }
        }
        _cmbScreenshotMode.SelectedIndex = _cmbScreenshotMode.SelectedIndex < 0 ? 0 : _cmbScreenshotMode.SelectedIndex;

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
        foreach (ComboBoxItem item in _cmbAiPlatform.Items)
        {
            if ((TranslationAiPlatform)item.Tag == config.AiPlatform)
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
        _cmbAiModel.Text = config.AiModel ?? "";
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

            config.Translation.Provider = (TranslationProvider)(_cmbProvider.SelectedItem as ComboBoxItem)!.Tag;
            config.Translation.TranslationMode = (string)(_cmbTranslationMode.SelectedItem as ComboBoxItem)!.Tag;
            config.Translation.ScreenshotMode = (ScreenshotTranslationMode)(_cmbScreenshotMode.SelectedItem as ComboBoxItem)!.Tag;
            config.Translation.SourceLanguage = "auto";
            config.Translation.TargetLanguage = TranslationManager.ResolveTargetLanguage(string.Empty, config.Translation.TranslationMode);

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
            config.Translation.AiPlatform = GetSelectedAiPlatform();
            if (!string.IsNullOrWhiteSpace(_txtAiApiUrl.Text))
            {
                config.Translation.AiApiUrlEncrypted = SecureStorage.Encrypt(_txtAiApiUrl.Text);
            }
            if (!string.IsNullOrWhiteSpace(_pwdAiApiKey.Password))
            {
                config.Translation.AiApiKeyEncrypted = SecureStorage.Encrypt(_pwdAiApiKey.Password);
            }
            config.Translation.AiModel = GetAiModel();

            _configManager.Save(config);

            ToastNotification.Show("设置已保存", "翻译设置已更新", ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }
}
