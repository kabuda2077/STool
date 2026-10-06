using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;

namespace STool.Views.Settings;

public class OcrSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbFallbackPolicy = null!;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private SecurePasswordField _pwdTencentSecretKey = null!;
    private EncryptedSetting _tencentSecretId = new(null);
    private EncryptedSetting _tencentSecretKey = new(null);

    // AI Vision
    private AiServiceSettingsSection _ai = null!;

    public OcrSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── OCR 提供商 ──
        var baseSection = SettingsLayout.CreateSectionContent("OCR 提供商");

        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "Windows 本地 OCR", Tag = OcrProvider.WindowsLocal });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云 OCR", Tag = OcrProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI Vision OCR", Tag = OcrProvider.AI });
        baseSection.Children.Add(SettingsLayout.CreateInlineField("当前引擎", _cmbProvider));

        _cmbFallbackPolicy = SettingsLayout.CreateComboBox();
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "使用 Windows 本地 OCR", Tag = true });
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "不自动处理", Tag = false });
        baseSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint(
            "失败时",
            _cmbFallbackPolicy,
            "云服务异常时执行的策略",
            isLast: true));
        Children.Add(SettingsLayout.CreateSection(baseSection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard, _) = SettingsLayout.CreateCompactCollapsibleSection("腾讯云设置");

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey, isLast: true));

        Children.Add(tencentCard);

        // ── AI Vision 设置(可折叠,行内布局) ──
        var (aiContent, aiCard, _) = SettingsLayout.CreateCompactCollapsibleSection("AI Vision 设置");
        _ai = new AiServiceSettingsSection(
            aiContent,
            new[]
            {
                new AiPlatformOption("OpenAI", OcrAiPlatform.OpenAI, AiPlatformPreset.OpenAi),
                new AiPlatformOption("Google AI Studio", OcrAiPlatform.GoogleAiStudio, AiPlatformPreset.GoogleAiStudio),
                new AiPlatformOption("自定义", OcrAiPlatform.Custom, null)
            },
            (url, key, model) => AiVisionOcrService.TestAsync(url, key, model));
        Children.Add(aiCard);
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Ocr;

        SelectByTag(_cmbProvider, config.Provider);
        SelectByTag(_cmbFallbackPolicy, config.FallbackToLocal);

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
        autoSave.TrackImmediate(_cmbFallbackPolicy);
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
        var config = _configManager.Get().Ocr;
        var provider = (OcrProvider)((ComboBoxItem)_cmbProvider.SelectedItem).Tag;
        var fallbackToLocal = (_cmbFallbackPolicy.SelectedItem as ComboBoxItem)?.Tag is true;
        var aiPlatform = (OcrAiPlatform)_ai.SelectedPlatformTag;

        if (config.Provider == provider &&
            config.FallbackToLocal == fallbackToLocal &&
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

        _configManager.Update(current =>
        {
            current.Ocr.Provider = provider;
            current.Ocr.FallbackToLocal = fallbackToLocal;
            current.Ocr.TencentSecretIdEncrypted = secretId;
            current.Ocr.TencentSecretKeyEncrypted = secretKey;
            current.Ocr.AiPlatform = aiPlatform;
            current.Ocr.AiApiUrlEncrypted = apiUrl;
            current.Ocr.AiApiKeyEncrypted = apiKey;
            current.Ocr.AiModel = aiModel;
        });

        _tencentSecretId.MarkSaved(_txtTencentSecretId.Text, secretId);
        _tencentSecretKey.MarkSaved(_pwdTencentSecretKey.Password, secretKey);
        _ai.MarkSaved(apiUrl, apiKey);
        return true;
    }
}
