using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;
using STool.Modules.Translation;

namespace STool.Views.Settings;

public class OcrSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private SettingsAutoSaveController _autoSave = null!;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbFallbackPolicy = null!;

    // 腾讯云
    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private SecurePasswordField _pwdTencentSecretKey = null!;

    // AI Vision
    private System.Windows.Controls.ComboBox _cmbAiPlatform = null!;
    private System.Windows.Controls.TextBox _txtAiApiUrl = null!;
    private TextBlock _txtAiApiUrlHint = null!;
    private SecurePasswordField _pwdAiApiKey = null!;
    private System.Windows.Controls.ComboBox _cmbAiModel = null!;

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
        Children.Add(WrapSection(baseSection));

        // ── 腾讯云设置(可折叠,行内布局) ──
        var (tencentContent, tencentCard) = CreateCollapsibleSection("腾讯云设置");

        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));

        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencentContent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey, isLast: true));

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
        _txtAiApiUrlHint = SettingsLayout.CreateHint(string.Empty, inline: false);
        _txtAiApiUrl.TextChanged += (_, _) => UpdateApiUrlPreview();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("API URL", _txtAiApiUrl, _txtAiApiUrlHint));

        _pwdAiApiKey = SettingsLayout.CreatePasswordField();
        aiContent.Children.Add(SettingsLayout.CreateInlineField("API Key", _pwdAiApiKey));

        _cmbAiModel = SettingsLayout.CreateEditableComboBox();
        aiContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模型", _cmbAiModel, "可获取列表，也可手动输入。", isLast: true));

        var btnFetchModels = SettingsLayout.CreateSecondaryActionButton("获取模型");
        btnFetchModels.Click += BtnFetchModels_Click;
        aiContent.Children.Add(SettingsLayout.CreateActionRow(btnFetchModels));

        Children.Add(aiCard);

    }

    private Border WrapSection(StackPanel section)
    {
        return SettingsLayout.CreateSection(section);
    }

    private (StackPanel content, Border card) CreateCollapsibleSection(string title)
    {
        var (content, card, _) = SettingsLayout.CreateCompactCollapsibleSection(title);
        return (content, card);
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
        _cmbProvider.SelectedIndex = _cmbProvider.SelectedIndex < 0 ? 0 : _cmbProvider.SelectedIndex;

        foreach (ComboBoxItem item in _cmbFallbackPolicy.Items)
        {
            if (item.Tag is bool enabled && enabled == config.FallbackToLocal)
            {
                _cmbFallbackPolicy.SelectedItem = item;
                break;
            }
        }
        _cmbFallbackPolicy.SelectedIndex = _cmbFallbackPolicy.SelectedIndex < 0 ? 0 : _cmbFallbackPolicy.SelectedIndex;

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
        _cmbAiModel.Text = config.AiModel ?? "";
        UpdateApiUrlPreview();
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
        _autoSave.TrackImmediate(_cmbProvider);
        _autoSave.TrackImmediate(_cmbFallbackPolicy);
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
        var provider = (OcrProvider)(_cmbProvider.SelectedItem as ComboBoxItem)!.Tag;
        var fallbackToLocal = (_cmbFallbackPolicy.SelectedItem as ComboBoxItem)?.Tag is true;
        var aiPlatform = GetSelectedAiPlatform();
        var tencentSecretId = _txtTencentSecretId.Text.Trim();
        var tencentSecretKey = _pwdTencentSecretKey.Password.Trim();
        var aiApiUrl = _txtAiApiUrl.Text.Trim();
        var aiApiKey = _pwdAiApiKey.Password.Trim();
        var aiModel = GetAiModel();

        if (config.Ocr.Provider == provider &&
            config.Ocr.FallbackToLocal == fallbackToLocal &&
            config.Ocr.AiPlatform == aiPlatform &&
            DecryptOrEmpty(config.Ocr.TencentSecretIdEncrypted) == tencentSecretId &&
            DecryptOrEmpty(config.Ocr.TencentSecretKeyEncrypted) == tencentSecretKey &&
            DecryptOrEmpty(config.Ocr.AiApiUrlEncrypted) == aiApiUrl &&
            DecryptOrEmpty(config.Ocr.AiApiKeyEncrypted) == aiApiKey &&
            (config.Ocr.AiModel ?? string.Empty) == aiModel)
        {
            return false;
        }

        var secretIdEncrypted = EncryptIfChangedOrClear(tencentSecretId, config.Ocr.TencentSecretIdEncrypted);
        var secretKeyEncrypted = EncryptIfChangedOrClear(tencentSecretKey, config.Ocr.TencentSecretKeyEncrypted);
        var apiUrlEncrypted = EncryptIfChangedOrClear(aiApiUrl, config.Ocr.AiApiUrlEncrypted);
        var apiKeyEncrypted = EncryptIfChangedOrClear(aiApiKey, config.Ocr.AiApiKeyEncrypted);

        _configManager.Update(current =>
        {
            current.Ocr.Provider = provider;
            current.Ocr.FallbackToLocal = fallbackToLocal;
            current.Ocr.TencentSecretIdEncrypted = secretIdEncrypted;
            current.Ocr.TencentSecretKeyEncrypted = secretKeyEncrypted;
            current.Ocr.AiPlatform = aiPlatform;
            current.Ocr.AiApiUrlEncrypted = apiUrlEncrypted;
            current.Ocr.AiApiKeyEncrypted = apiKeyEncrypted;
            current.Ocr.AiModel = aiModel;
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

        _cmbAiModel.Text = platform switch
        {
            OcrAiPlatform.OpenAI => "gpt-4o-mini",
            OcrAiPlatform.GoogleAiStudio => "gemini-1.5-flash",
            _ => string.Empty
        };
    }

    private string GetAiModel()
    {
        return _cmbAiModel.Text.Trim();
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

                ToastNotification.Show("模型已获取", $"共 {models.Count} 个模型", ToastNotification.ToastType.Success);
            }
            catch (Exception ex)
            {
                ToastNotification.Show("获取模型失败", ex.Message, ToastNotification.ToastType.Error);
            }
        });
    }

    private OcrAiPlatform GetSelectedAiPlatform()
    {
        return (_cmbAiPlatform.SelectedItem as ComboBoxItem)?.Tag is OcrAiPlatform platform
            ? platform
            : OcrAiPlatform.Custom;
    }
}
