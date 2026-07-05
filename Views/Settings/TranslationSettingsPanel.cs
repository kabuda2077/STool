using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private Border _tencentSection = null!;
    private Border _aiSection = null!;
    private Expander _tencentExpander = null!;
    private Expander _aiExpander = null!;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private SecurePasswordField _pwdTencentSecretKey = null!;

    // AI
    private System.Windows.Controls.ComboBox _cmbAiPlatform = null!;
    private System.Windows.Controls.TextBox _txtAiApiUrl = null!;
    private SecurePasswordField _pwdAiApiKey = null!;
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
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "谷歌翻译", Tag = TranslationProvider.Google });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译", Tag = TranslationProvider.OpenAI });
        _cmbProvider.SelectionChanged += CmbProvider_SelectionChanged;
        providerSection.Children.Add(SettingsLayout.CreateInlineField("当前引擎", _cmbProvider));
        Children.Add(WrapSection(providerSection));

        // ── 默认策略 ──
        var strategySection = new StackPanel();
        strategySection.Children.Add(new TextBlock
        {
            Text = "默认策略",
            Style = (Style)FindResource("SettingsGroupTitle")
        });

        _cmbTranslationMode = SettingsLayout.CreateComboBox();
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("中文", "IconArrowLeftRight", "英文", "zh-en"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "IconArrowRight", "中文", "auto-zh"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "IconArrowRight", "英文", "auto-en"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "IconArrowRight", "日文", "auto-ja"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "IconArrowRight", "韩文", "auto-ko"));
        strategySection.Children.Add(SettingsLayout.CreateInlineField("翻译策略", _cmbTranslationMode));

        _cmbScreenshotMode = SettingsLayout.CreateComboBox();
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "快速：本地规则识别", Tag = ScreenshotTranslationMode.Fast });
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "智能：AI 识别并翻译", Tag = ScreenshotTranslationMode.Smart });
        strategySection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("截图翻译", _cmbScreenshotMode, "智能模式使用 AI，失败回退快速模式。"));
        Children.Add(WrapSection(strategySection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard) = CreateCollapsibleSection("腾讯云设置");
        _tencentSection = tencentCard;

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey));

        Children.Add(tencentCard);

        // ── AI 翻译设置(可折叠,行内布局) ──
        var (aiContent, aiCard) = CreateCollapsibleSection("AI 翻译设置");
        _aiSection = aiCard;

        _cmbAiPlatform = SettingsLayout.CreateComboBox();
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "OpenAI", Tag = TranslationAiPlatform.OpenAI });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "Google AI Studio", Tag = TranslationAiPlatform.GoogleAiStudio });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "DeepSeek", Tag = TranslationAiPlatform.DeepSeek });
        _cmbAiPlatform.Items.Add(new ComboBoxItem { Content = "自定义", Tag = TranslationAiPlatform.Custom });
        _cmbAiPlatform.SelectionChanged += CmbAiPlatform_SelectionChanged;
        aiContent.Children.Add(SettingsLayout.CreateInlineField("平台", _cmbAiPlatform));

        _txtAiApiUrl = SettingsLayout.CreateTextBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("API URL", _txtAiApiUrl, "OpenAI 兼容地址，自定义需手动填写。"));

        _pwdAiApiKey = SettingsLayout.CreatePasswordField();
        aiContent.Children.Add(SettingsLayout.CreateInlineField("API Key", _pwdAiApiKey));

        _cmbAiModel = SettingsLayout.CreateEditableComboBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模型", _cmbAiModel, "可获取列表，也可手动输入。"));

        var aiActions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = SettingsLayout.ActionRowMargin
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
        var btnSave = SettingsLayout.CreateSaveButton();
        btnSave.Click += BtnSave_Click;
        Children.Add(btnSave);
    }

    private ComboBoxItem CreateLanguageModeItem(string source, string iconKey, string target, string tag)
    {
        var textBrush = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        var transparentBrush = (System.Windows.Media.Brush)FindResource("TransparentBrush");
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        System.Windows.Documents.TextElement.SetForeground(panel, textBrush);
        panel.Children.Add(new TextBlock { Text = source });
        panel.Children.Add(new Viewbox
        {
            Width = 13,
            Height = 13,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Controls.Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = (Geometry)FindResource(iconKey),
                        Stroke = textBrush,
                        StrokeThickness = 2,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Fill = transparentBrush
                    }
                }
            }
        });
        panel.Children.Add(new TextBlock { Text = target });

        return new ComboBoxItem
        {
            Content = panel,
            Tag = tag
        };
    }

    private Border WrapSection(StackPanel section)
    {
        return SettingsLayout.CreateSection(section);
    }

    private (StackPanel content, Border card) CreateCollapsibleSection(string title)
    {
        var (content, card, expander) = SettingsLayout.CreateCollapsibleSection(title);
        if (title.Contains("腾讯"))
        {
            _tencentExpander = expander;
        }
        else
        {
            _aiExpander = expander;
        }
        return (content, card);
    }

    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderSections();
    }

    private void UpdateProviderSections()
    {
        if (_tencentSection == null || _aiSection == null || _tencentExpander == null || _aiExpander == null)
        {
            return;
        }

        var provider = (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider selected
            ? selected
            : TranslationProvider.Google;

        _tencentExpander.IsExpanded = provider == TranslationProvider.Tencent;
        _aiExpander.IsExpanded = provider == TranslationProvider.OpenAI;
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
        _cmbProvider.SelectedIndex = _cmbProvider.SelectedIndex < 0 ? 0 : _cmbProvider.SelectedIndex;
        UpdateProviderSections();

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
