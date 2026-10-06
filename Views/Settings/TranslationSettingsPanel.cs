using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using STool.Core;
using STool.Models;
using STool.Modules.Translation;

namespace STool.Views.Settings;

public class TranslationSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbTranslationMode = null!;
    private System.Windows.Controls.ComboBox _cmbScreenshotMode = null!;
    private Expander _tencentExpander = null!;
    private Expander _aiExpander = null!;
    private bool _loadingSettings = true;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private SecurePasswordField _pwdTencentSecretKey = null!;
    private EncryptedSetting _tencentSecretId = new(null);
    private EncryptedSetting _tencentSecretKey = new(null);

    // AI
    private AiServiceSettingsSection _ai = null!;

    public TranslationSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
        _loadingSettings = false;
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
        providerSection.Children.Add(SettingsLayout.CreateInlineField("当前引擎", _cmbProvider, isLast: true));
        Children.Add(SettingsLayout.CreateSection(providerSection));

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
        strategySection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("截图翻译", _cmbScreenshotMode, "智能模式使用 AI，不可用时改用整段翻译。", isLast: true));
        Children.Add(SettingsLayout.CreateSection(strategySection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard, tencentExpander) = SettingsLayout.CreateCollapsibleSection("腾讯云设置");
        _tencentExpander = tencentExpander;

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey, isLast: true));

        Children.Add(tencentCard);

        // ── AI 翻译设置(可折叠,行内布局) ──
        var (aiContent, aiCard, aiExpander) = SettingsLayout.CreateCollapsibleSection("AI 翻译设置");
        _aiExpander = aiExpander;
        _ai = new AiServiceSettingsSection(
            aiContent,
            new[]
            {
                new AiPlatformOption("OpenAI", TranslationAiPlatform.OpenAI, AiPlatformPreset.OpenAi),
                new AiPlatformOption("Google AI Studio", TranslationAiPlatform.GoogleAiStudio, AiPlatformPreset.GoogleAiStudio),
                new AiPlatformOption("DeepSeek", TranslationAiPlatform.DeepSeek, AiPlatformPreset.DeepSeek),
                new AiPlatformOption("自定义", TranslationAiPlatform.Custom, null)
            },
            TestAiAsync);
        Children.Add(aiCard);
    }

    private static async Task<(bool Success, string Message)> TestAiAsync(string url, string key, string model)
    {
        var result = await AiTranslationService.TestAsync(url, key, model);
        return result.Success
            ? (true, result.TranslatedText)
            : (false, result.ErrorMessage ?? "未知错误");
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

    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Initial configuration loading should not force a credentials section open.
        // Only an explicit provider change by the user expands its relevant section.
        if (_loadingSettings)
            return;

        var provider = (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider selected
            ? selected
            : TranslationProvider.Google;

        _tencentExpander.IsExpanded = provider == TranslationProvider.Tencent;
        _aiExpander.IsExpanded = provider == TranslationProvider.OpenAI;
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Translation;

        SelectByTag(_cmbProvider, config.Provider);
        SelectByTag(_cmbTranslationMode, config.TranslationMode);
        SelectByTag(_cmbScreenshotMode, config.ScreenshotMode);

        _tencentSecretId = new EncryptedSetting(config.TencentSecretIdEncrypted);
        _tencentSecretKey = new EncryptedSetting(config.TencentSecretKeyEncrypted);
        _txtTencentSecretId.Text = _tencentSecretId.Plain;
        _pwdTencentSecretKey.Password = _tencentSecretKey.Plain;

        _ai.Load(config.AiPlatform, config.AiApiUrlEncrypted, config.AiApiKeyEncrypted, config.AiModel);

        if (_tencentSecretId.IsUnreadable || _tencentSecretKey.IsUnreadable || _ai.HasUnreadableSecrets)
            SettingsLayout.WarnUnreadableSecrets();
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox comboBox, object value)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (Equals(item.Tag, value))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        autoSave.TrackImmediate(_cmbProvider);
        autoSave.TrackImmediate(_cmbTranslationMode);
        autoSave.TrackImmediate(_cmbScreenshotMode);
        autoSave.TrackImmediate(_ai.Platform);
        autoSave.TrackDebounced(_txtTencentSecretId);
        autoSave.TrackDebounced(_pwdTencentSecretKey);
        autoSave.TrackDebounced(_ai.ApiUrl);
        autoSave.TrackDebounced(_ai.ApiKey);
        autoSave.TrackDebounced(_ai.Model);
        Children.Add(new Border { Height = 12 });
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get().Translation;
        var provider = (TranslationProvider)((ComboBoxItem)_cmbProvider.SelectedItem).Tag;
        var translationMode = (string)((ComboBoxItem)_cmbTranslationMode.SelectedItem).Tag;
        var screenshotMode = (ScreenshotTranslationMode)((ComboBoxItem)_cmbScreenshotMode.SelectedItem).Tag;
        var aiPlatform = (TranslationAiPlatform)_ai.SelectedPlatformTag;

        if (config.Provider == provider &&
            config.TranslationMode == translationMode &&
            config.ScreenshotMode == screenshotMode &&
            _tencentSecretId.Matches(_txtTencentSecretId.Text) &&
            _tencentSecretKey.Matches(_pwdTencentSecretKey.Password) &&
            _ai.IsUnchanged(config.AiPlatform, config.AiModel))
        {
            return false;
        }

        var secretId = _tencentSecretId.Resolve(_txtTencentSecretId.Text);
        var secretKey = _tencentSecretKey.Resolve(_pwdTencentSecretKey.Password);
        var (apiUrl, apiKey) = _ai.ResolveSecrets();
        var aiModel = _ai.ModelName;
        var targetLanguage = TranslationManager.ResolveTargetLanguage(string.Empty, translationMode);

        _configManager.Update(current =>
        {
            current.Translation.Provider = provider;
            current.Translation.TranslationMode = translationMode;
            current.Translation.ScreenshotMode = screenshotMode;
            current.Translation.SourceLanguage = "auto";
            current.Translation.TargetLanguage = targetLanguage;
            current.Translation.TencentSecretIdEncrypted = secretId;
            current.Translation.TencentSecretKeyEncrypted = secretKey;
            current.Translation.AiPlatform = aiPlatform;
            current.Translation.AiApiUrlEncrypted = apiUrl;
            current.Translation.AiApiKeyEncrypted = apiKey;
            current.Translation.AiModel = aiModel;
        });

        _tencentSecretId.MarkSaved(_txtTencentSecretId.Text, secretId);
        _tencentSecretKey.MarkSaved(_pwdTencentSecretKey.Password, secretKey);
        _ai.MarkSaved(apiUrl, apiKey);
        return true;
    }
}
