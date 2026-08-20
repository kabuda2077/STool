using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using STool.Core;
using STool.Modules.Translation;
using STool.Models;

namespace STool.Views.Settings;

public class TranslationSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private SettingsAutoSaveController _autoSave = null!;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbTranslationMode = null!;
    private System.Windows.Controls.ComboBox _cmbScreenshotMode = null!;
    private TextBlock _statusText = null!;
    private readonly Dictionary<TranslationProvider, bool?> _serviceTestResults = new();
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
    private TextBlock _txtAiApiUrlHint = null!;
    private SecurePasswordField _pwdAiApiKey = null!;
    private System.Windows.Controls.ComboBox _cmbAiModel = null!;

    public TranslationSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 翻译提供商 ──
        var providerSection = SettingsLayout.CreateSectionContent("翻译提供商");
        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "谷歌翻译", Tag = TranslationProvider.Google });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译", Tag = TranslationProvider.OpenAI });
        _cmbProvider.SelectionChanged += CmbProvider_SelectionChanged;
        providerSection.Children.Add(SettingsLayout.CreateInlineField("当前引擎", _cmbProvider));
        _statusText = SettingsLayout.CreateHint(string.Empty, inline: false);
        providerSection.Children.Add(SettingsLayout.CreateInlineField("服务状态", _statusText, isLast: true));
        Children.Add(WrapSection(providerSection));

        // ── 默认策略 ──
        var strategySection = SettingsLayout.CreateSectionContent("默认策略");

        _cmbTranslationMode = SettingsLayout.CreateComboBox();
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("中文", "英文", "zh-en", bidirectional: true));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "中文", "auto-zh"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "英文", "auto-en"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "日文", "auto-ja"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "韩文", "auto-ko"));
        strategySection.Children.Add(SettingsLayout.CreateInlineField("翻译策略", _cmbTranslationMode));

        _cmbScreenshotMode = SettingsLayout.CreateComboBox();
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "快速：本地规则识别", Tag = ScreenshotTranslationMode.Fast });
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "智能：AI 识别并翻译", Tag = ScreenshotTranslationMode.Smart });
        strategySection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("截图翻译", _cmbScreenshotMode, "智能模式使用 AI，失败回退快速模式。", isLast: true));
        Children.Add(WrapSection(strategySection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard) = CreateCollapsibleSection("腾讯云设置");
        _tencentSection = tencentCard;

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey, isLast: true));

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
        _txtAiApiUrlHint = SettingsLayout.CreateHint(string.Empty, inline: false);
        _txtAiApiUrl.TextChanged += (_, _) => UpdateApiUrlPreview();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("API URL", _txtAiApiUrl, _txtAiApiUrlHint));

        _pwdAiApiKey = SettingsLayout.CreatePasswordField();
        aiContent.Children.Add(SettingsLayout.CreateInlineField("API Key", _pwdAiApiKey));

        _cmbAiModel = SettingsLayout.CreateEditableComboBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模型", _cmbAiModel, "可获取列表，也可手动输入。", isLast: true));

        var btnFetchModels = SettingsLayout.CreateSecondaryActionButton("获取模型");
        btnFetchModels.Click += BtnFetchModels_Click;
        var btnTestAi = SettingsLayout.CreateSecondaryActionButton("测试");
        btnTestAi.Click += BtnTestAi_Click;
        aiContent.Children.Add(SettingsLayout.CreateActionRow(btnFetchModels, btnTestAi));

        Children.Add(aiCard);

    }

    private ComboBoxItem CreateLanguageModeItem(string source, string target, string tag, bool bidirectional = false)
    {
        var textBrush = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        var iconBrush = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        var transparentBrush = (System.Windows.Media.Brush)FindResource("TransparentBrush");

        var content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = source,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new Viewbox
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(7, 0, 7, 0),
            Opacity = 0.78,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = (Geometry)FindResource(bidirectional ? "IconArrowLeftRight" : "IconArrowRight"),
                        Stroke = iconBrush,
                        StrokeThickness = 1.5,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Fill = transparentBrush
                    }
                }
            }
        });
        content.Children.Add(new TextBlock
        {
            Text = target,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var accessibleName = bidirectional
            ? $"{source}和{target}互译"
            : $"自动识别，译为{target}";
        var item = new ComboBoxItem
        {
            Content = content,
            Tag = tag
        };
        System.Windows.Automation.AutomationProperties.SetName(item, accessibleName);
        TextSearch.SetText(item, accessibleName);
        return item;
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
        UpdateServiceStatus();
    }

    private string FormatCloudStatus(bool configured, string serviceName)
    {
        if (!configured)
            return $"{serviceName} 未配置";
        var provider = serviceName == "腾讯云"
            ? TranslationProvider.Tencent
            : TranslationProvider.OpenAI;
        _serviceTestResults.TryGetValue(provider, out var result);
        return result switch
        {
            true => $"{serviceName} 最近测试成功",
            false => $"{serviceName} 最近测试失败",
            null => $"{serviceName} 配置完整，尚未测试"
        };
    }

    private void ResetServiceTestStatus(TranslationProvider provider)
    {
        _serviceTestResults[provider] = null;
        UpdateServiceStatus();
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

    private void UpdateServiceStatus()
    {
        if (_statusText == null || _cmbProvider == null)
            return;

        var provider = (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider selected
            ? selected
            : TranslationProvider.Google;
        _statusText.Text = provider switch
        {
            TranslationProvider.Google => "免费端点无需密钥，繁忙时可能限流",
            TranslationProvider.Tencent => FormatCloudStatus(
                !string.IsNullOrWhiteSpace(_txtTencentSecretId?.Text) &&
                !string.IsNullOrWhiteSpace(_pwdTencentSecretKey?.Password),
                "腾讯云"),
            TranslationProvider.OpenAI => FormatCloudStatus(
                !string.IsNullOrWhiteSpace(_txtAiApiUrl?.Text) &&
                !string.IsNullOrWhiteSpace(_pwdAiApiKey?.Password) &&
                !string.IsNullOrWhiteSpace(_cmbAiModel?.Text),
                "AI 翻译"),
            _ => string.Empty
        };
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
        if (sender is not System.Windows.Controls.Button button)
            return;

        await UiBusyState.RunWithBusyStateAsync(button, "获取中...", async () =>
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

                _serviceTestResults[TranslationProvider.OpenAI] = true;
                UpdateServiceStatus();
                ToastNotification.Show("模型已获取", $"共 {models.Count} 个模型", ToastNotification.ToastType.Success);
            }
            catch (Exception ex)
            {
                _serviceTestResults[TranslationProvider.OpenAI] = false;
                UpdateServiceStatus();
                ToastNotification.Show("获取模型失败", ex.Message, ToastNotification.ToastType.Error);
            }
        });
    }

    private async void BtnTestAi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        await UiBusyState.RunWithBusyStateAsync(button, "测试中...", async () =>
        {
            try
            {
                var result = await AiTranslationService.TestAsync(_txtAiApiUrl.Text, _pwdAiApiKey.Password, GetAiModel());
                if (result.Success)
                {
                    _serviceTestResults[TranslationProvider.OpenAI] = true;
                    UpdateServiceStatus();
                    ToastNotification.Show("测试成功", result.TranslatedText, ToastNotification.ToastType.Success);
                }
                else
                {
                    _serviceTestResults[TranslationProvider.OpenAI] = false;
                    UpdateServiceStatus();
                    ToastNotification.Show("测试失败", result.ErrorMessage ?? "未知错误", ToastNotification.ToastType.Error);
                }
            }
            catch (Exception ex)
            {
                _serviceTestResults[TranslationProvider.OpenAI] = false;
                UpdateServiceStatus();
                ToastNotification.Show("测试失败", ex.Message, ToastNotification.ToastType.Error);
            }
        });
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
        UpdateApiUrlPreview();
        UpdateServiceStatus();
    }

    private void UpdateApiUrlPreview()
    {
        var apiUrl = _txtAiApiUrl?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(apiUrl))
        {
            _txtAiApiUrlHint.Text = "输入域名或完整的 Chat Completions 地址";
            return;
        }

        try
        {
            _txtAiApiUrlHint.Text = $"实际请求：{AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(apiUrl)}";
        }
        catch (InvalidOperationException ex)
        {
            _txtAiApiUrlHint.Text = ex.Message;
        }
    }

    private void EnableAutoSave()
    {
        _autoSave = new SettingsAutoSaveController(this, SaveSettings);
        _txtTencentSecretId.TextChanged += (_, _) => ResetServiceTestStatus(TranslationProvider.Tencent);
        _pwdTencentSecretKey.PasswordChanged += (_, _) => ResetServiceTestStatus(TranslationProvider.Tencent);
        _txtAiApiUrl.TextChanged += (_, _) => ResetServiceTestStatus(TranslationProvider.OpenAI);
        _pwdAiApiKey.PasswordChanged += (_, _) => ResetServiceTestStatus(TranslationProvider.OpenAI);
        _cmbAiModel.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ResetServiceTestStatus(TranslationProvider.OpenAI)));
        _autoSave.TrackImmediate(_cmbProvider);
        _autoSave.TrackImmediate(_cmbTranslationMode);
        _autoSave.TrackImmediate(_cmbScreenshotMode);
        _autoSave.TrackImmediate(_cmbAiPlatform);
        _autoSave.TrackDebounced(_txtTencentSecretId);
        _autoSave.TrackDebounced(_pwdTencentSecretKey);
        _autoSave.TrackDebounced(_txtAiApiUrl);
        _autoSave.TrackDebounced(_pwdAiApiKey);
        _autoSave.TrackDebounced(_cmbAiModel);
        Children.Add(new Border { Height = 12 });
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get();
        var provider = (TranslationProvider)(_cmbProvider.SelectedItem as ComboBoxItem)!.Tag;
        var translationMode = (string)(_cmbTranslationMode.SelectedItem as ComboBoxItem)!.Tag;
        var screenshotMode = (ScreenshotTranslationMode)(_cmbScreenshotMode.SelectedItem as ComboBoxItem)!.Tag;
        var aiPlatform = GetSelectedAiPlatform();
        var tencentSecretId = _txtTencentSecretId.Text.Trim();
        var tencentSecretKey = _pwdTencentSecretKey.Password.Trim();
        var aiApiUrl = _txtAiApiUrl.Text.Trim();
        var aiApiKey = _pwdAiApiKey.Password.Trim();
        var aiModel = GetAiModel();

        if (config.Translation.Provider == provider &&
            config.Translation.TranslationMode == translationMode &&
            config.Translation.ScreenshotMode == screenshotMode &&
            config.Translation.AiPlatform == aiPlatform &&
            DecryptOrEmpty(config.Translation.TencentSecretIdEncrypted) == tencentSecretId &&
            DecryptOrEmpty(config.Translation.TencentSecretKeyEncrypted) == tencentSecretKey &&
            DecryptOrEmpty(config.Translation.AiApiUrlEncrypted) == aiApiUrl &&
            DecryptOrEmpty(config.Translation.AiApiKeyEncrypted) == aiApiKey &&
            (config.Translation.AiModel ?? string.Empty) == aiModel)
        {
            return false;
        }

        var secretIdEncrypted = EncryptIfChangedOrClear(tencentSecretId, config.Translation.TencentSecretIdEncrypted);
        var secretKeyEncrypted = EncryptIfChangedOrClear(tencentSecretKey, config.Translation.TencentSecretKeyEncrypted);
        var apiUrlEncrypted = EncryptIfChangedOrClear(aiApiUrl, config.Translation.AiApiUrlEncrypted);
        var apiKeyEncrypted = EncryptIfChangedOrClear(aiApiKey, config.Translation.AiApiKeyEncrypted);
        var targetLanguage = TranslationManager.ResolveTargetLanguage(string.Empty, translationMode);

        _configManager.Update(current =>
        {
            current.Translation.Provider = provider;
            current.Translation.TranslationMode = translationMode;
            current.Translation.ScreenshotMode = screenshotMode;
            current.Translation.SourceLanguage = "auto";
            current.Translation.TargetLanguage = targetLanguage;
            current.Translation.TencentSecretIdEncrypted = secretIdEncrypted;
            current.Translation.TencentSecretKeyEncrypted = secretKeyEncrypted;
            current.Translation.AiPlatform = aiPlatform;
            current.Translation.AiApiUrlEncrypted = apiUrlEncrypted;
            current.Translation.AiApiKeyEncrypted = apiKeyEncrypted;
            current.Translation.AiModel = aiModel;
        });
        return true;
    }

    private static string DecryptOrEmpty(string? encrypted)
    {
        return string.IsNullOrWhiteSpace(encrypted) ? string.Empty : SecureStorage.Decrypt(encrypted);
    }

    private static string? EncryptIfChangedOrClear(string value, string? currentEncrypted)
    {
        var normalized = value.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return SecureStorage.Decrypt(currentEncrypted ?? string.Empty) == normalized
            ? currentEncrypted
            : SecureStorage.Encrypt(normalized);
    }
}
