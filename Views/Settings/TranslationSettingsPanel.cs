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

    public TranslationSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 翻译提供商 ──
        var providerSection = new StackPanel();
        providerSection.Children.Add(new TextBlock
        {
            Text = "翻译提供商",
            Style = (Style)FindResource("SettingsGroupTitle")
        });
        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Margin = SettingsLayout.FieldSpacing;
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "谷歌翻译", Tag = TranslationProvider.Google });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译 (OpenAI/Claude)", Tag = TranslationProvider.OpenAI });
        providerSection.Children.Add(_cmbProvider);
        Children.Add(WrapSection(providerSection));

        // ── 默认策略 ──
        var strategySection = new StackPanel();
        strategySection.Children.Add(new TextBlock
        {
            Text = "默认策略",
            Style = (Style)FindResource("SettingsGroupTitle")
        });

        strategySection.Children.Add(new TextBlock
        {
            Text = "翻译策略",
            Style = (Style)FindResource("FieldLabel")
        });
        _cmbTranslationMode = SettingsLayout.CreateComboBox();
        _cmbTranslationMode.Margin = SettingsLayout.FieldSpacing;
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "中 ⇄ 英", Tag = "zh-en" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 中文", Tag = "auto-zh" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 英文", Tag = "auto-en" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 日文", Tag = "auto-ja" });
        _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = "自动 → 韩文", Tag = "auto-ko" });
        strategySection.Children.Add(_cmbTranslationMode);

        strategySection.Children.Add(new TextBlock
        {
            Text = "截图翻译模式",
            Style = (Style)FindResource("FieldLabel")
        });
        _cmbScreenshotMode = SettingsLayout.CreateComboBox();
        _cmbScreenshotMode.Margin = SettingsLayout.FieldSpacing;
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "快速：规则筛选 + 当前翻译引擎", Tag = ScreenshotTranslationMode.Fast });
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "智能：AI 选择并翻译正文", Tag = ScreenshotTranslationMode.Smart });
        strategySection.Children.Add(_cmbScreenshotMode);
        strategySection.Children.Add(SettingsLayout.CreateHint("智能模式会额外使用 AI 翻译配置，失败时自动回退快速模式。"));
        Children.Add(WrapSection(strategySection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard) = CreateCollapsibleSection("腾讯云设置");

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        var (tencentPwdHost, tencentPwd) = SettingsLayout.CreatePasswordField();
        _pwdTencentSecretKey = tencentPwd;
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", tencentPwdHost));

        Children.Add(tencentCard);

        // ── AI 翻译设置(可折叠,行内布局) ──
        var (aiContent, aiCard) = CreateCollapsibleSection("AI 翻译设置");

        _cmbAiPlatform = SettingsLayout.CreateComboBox();
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "OpenAI", Tag = TranslationAiPlatform.OpenAI });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "Google AI Studio", Tag = TranslationAiPlatform.GoogleAiStudio });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "DeepSeek", Tag = TranslationAiPlatform.DeepSeek });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "自定义", Tag = TranslationAiPlatform.Custom });
        _cmbAiPlatform.SelectionChanged += CmbAiPlatform_SelectionChanged;
        aiContent.Children.Add(SettingsLayout.CreateInlineField("平台", _cmbAiPlatform));

        _txtAiApiUrl = SettingsLayout.CreateTextBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("API URL", _txtAiApiUrl, "OpenAI 兼容 Chat Completions 地址，自定义接口需手动填写。"));

        var (aiPwdHost, aiPwd) = SettingsLayout.CreatePasswordField();
        _pwdAiApiKey = aiPwd;
        aiContent.Children.Add(SettingsLayout.CreateInlineField("API Key", aiPwdHost));

        _cmbAiModel = SettingsLayout.CreateEditableComboBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模型", _cmbAiModel, "可点击获取模型列表，也可以直接手动输入模型名。"));

        var aiActions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(SettingsLayout.InlineLabelWidth, SettingsLayout.SpacingSM, 0, 0)
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
            Margin = new Thickness(SettingsLayout.SpacingSM, 0, 0, 0)
        };
        btnTestAi.Click += BtnTestAi_Click;
        aiActions.Children.Add(btnFetchModels);
        aiActions.Children.Add(btnTestAi);
        aiContent.Children.Add(aiActions);

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
