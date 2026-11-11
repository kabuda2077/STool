using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;
using STool.Modules.Translation;

namespace STool.Views.Settings;

/// <summary>
/// 服务账号：腾讯云、AI 文本模型、AI 视觉模型各配置一次，OCR、翻译、截图翻译按需引用。
/// </summary>
public class AccountsSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;

    private System.Windows.Controls.TextBox _txtTencentSecretId = null!;
    private SecurePasswordField _pwdTencentSecretKey = null!;
    private EncryptedSetting _tencentSecretId = new(null);
    private EncryptedSetting _tencentSecretKey = new(null);

    private AiServiceSettingsSection _aiText = null!;
    private AiServiceSettingsSection _aiVision = null!;
    private CheckBox _chkVisionSameAccount = null!;
    private TextBlock _tencentTestStatus = null!;

    public AccountsSettingsPanel(ConfigManager configManager)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
        _txtTencentSecretId.TextChanged += (_, _) => ResetTencentTest();
        _pwdTencentSecretKey.PasswordChanged += (_, _) => ResetTencentTest();
        _aiText.ApiUrl.TextChanged += (_, _) => InvalidateSharedTest();
        _aiText.ApiKey.PasswordChanged += (_, _) => InvalidateSharedTest();
    }

    private void ResetTencentTest()
    {
        _tencentTestStatus.Text = "未测试";
        _tencentTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    private void InvalidateSharedTest()
    {
        if (_chkVisionSameAccount.IsChecked == true) _aiVision.InvalidateTest();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 腾讯云 ──
        var tencent = SettingsLayout.CreateSectionContent("腾讯云");
        tencent.Children.Add(SettingsLayout.CreateSectionNote("用于腾讯云 OCR、腾讯云翻译和截图翻译识别。"));
        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));
        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey, isLast: true));
        var testTencent = SettingsLayout.CreateSecondaryActionButton("测试翻译服务");
        testTencent.Click += TestTencent_Click;
        tencent.Children.Add(SettingsLayout.CreateActionRow(testTencent));
        _tencentTestStatus = SettingsLayout.CreateHint("未测试", inline: true);
        _tencentTestStatus.Margin = new Thickness(24 + SettingsLayout.InlineLabelWidth, 0, 24, 16);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_tencentTestStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        tencent.Children.Add(_tencentTestStatus);
        Children.Add(SettingsLayout.CreateSection(tencent));

        // ── AI 文本模型 ──
        var aiText = SettingsLayout.CreateSectionContent("AI 文本模型");
        aiText.Children.Add(SettingsLayout.CreateSectionNote("用于 AI 翻译和截图翻译的智能模式。支持 OpenAI 兼容接口。"));
        _aiText = new AiServiceSettingsSection(
            aiText,
            new[]
            {
                new AiPlatformOption("OpenAI", AiPlatform.OpenAI, AiPlatformPreset.OpenAi),
                new AiPlatformOption("Google AI Studio", AiPlatform.GoogleAiStudio, AiPlatformPreset.GoogleAiStudio),
                new AiPlatformOption("DeepSeek", AiPlatform.DeepSeek, AiPlatformPreset.DeepSeek),
                new AiPlatformOption("自定义", AiPlatform.Custom, null)
            },
            TestAiTextAsync);
        Children.Add(SettingsLayout.CreateSection(aiText));

        // ── AI 视觉模型 ──
        var aiVision = SettingsLayout.CreateSectionContent("AI 视觉模型");
        aiVision.Children.Add(SettingsLayout.CreateSectionNote("用于 AI OCR，模型需要支持图片输入。"));
        _chkVisionSameAccount = SettingsLayout.CreateSwitch();
        aiVision.Children.Add(SettingsLayout.CreateInlineSwitchField(
            "账号",
            "与文本模型使用同一账号",
            "只需单独填写模型名",
            _chkVisionSameAccount,
            UpdateVisionAccountMode));
        _aiVision = new AiServiceSettingsSection(
            aiVision,
            new[]
            {
                new AiPlatformOption("OpenAI", AiPlatform.OpenAI, AiPlatformPreset.OpenAi),
                new AiPlatformOption("Google AI Studio", AiPlatform.GoogleAiStudio, AiPlatformPreset.GoogleAiStudio),
                new AiPlatformOption("DeepSeek", AiPlatform.DeepSeek, AiPlatformPreset.DeepSeek),
                new AiPlatformOption("自定义", AiPlatform.Custom, null)
            },
            (url, key, model) => AiVisionOcrService.TestAsync(url, key, model));
        Children.Add(SettingsLayout.CreateSection(aiVision));
    }

    private static async Task<(bool Success, string Message)> TestAiTextAsync(string url, string key, string model)
    {
        var result = await AiTranslationService.TestAsync(url, key, model);
        return result.Success
            ? (true, $"翻译结果：{result.TranslatedText}")
            : (false, result.ErrorMessage ?? "未知错误");
    }

    private async void TestTencent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        var secretId = _txtTencentSecretId.Text;
        var secretKey = _pwdTencentSecretKey.Password;
        _tencentTestStatus.Text = "正在测试翻译服务…";
        await UiBusyState.RunWithBusyStateAsync(button, "测试中...", async () =>
        {
            try
            {
                var (success, message) = await TencentTranslationService.TestAsync(secretId, secretKey);
                if (secretId != _txtTencentSecretId.Text || secretKey != _pwdTencentSecretKey.Password) return;
                _tencentTestStatus.Text = success ? "翻译服务测试通过 · OCR 权限需单独验证" : $"测试失败：{message}";
                _tencentTestStatus.Foreground = (System.Windows.Media.Brush)FindResource(success ? "SuccessBrush" : "ErrorBrush");
            }
            catch (Exception ex)
            {
                if (secretId != _txtTencentSecretId.Text || secretKey != _pwdTencentSecretKey.Password) return;
                _tencentTestStatus.Text = $"测试失败：{NetworkErrorMessages.FromException(ex)}";
                _tencentTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            }
        });
    }

    private void UpdateVisionAccountMode(bool sameAccount)
    {
        _aiVision.UseSharedCredentials(sameAccount
            ? () => (_aiText.ApiUrl.Text, _aiText.ApiKey.Password)
            : null);
    }

    private void LoadSettings()
    {
        var accounts = _configManager.Get().Accounts;

        _tencentSecretId = new EncryptedSetting(accounts.Tencent.SecretIdEncrypted);
        _tencentSecretKey = new EncryptedSetting(accounts.Tencent.SecretKeyEncrypted);
        _txtTencentSecretId.Text = _tencentSecretId.Plain;
        _pwdTencentSecretKey.Password = _tencentSecretKey.Plain;

        _aiText.Load(accounts.AiText.Platform, accounts.AiText.ApiUrlEncrypted, accounts.AiText.ApiKeyEncrypted, accounts.AiText.Model);
        _aiVision.Load(accounts.AiVision.Platform, accounts.AiVision.ApiUrlEncrypted, accounts.AiVision.ApiKeyEncrypted, accounts.AiVision.Model);
        _chkVisionSameAccount.IsChecked = accounts.AiVision.SameAccountAsText;
        UpdateVisionAccountMode(accounts.AiVision.SameAccountAsText);

        if (_tencentSecretId.IsUnreadable || _tencentSecretKey.IsUnreadable ||
            _aiText.HasUnreadableSecrets || _aiVision.HasUnreadableSecrets)
        {
            SettingsLayout.WarnUnreadableSecrets();
        }
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        autoSave.TrackDebounced(_txtTencentSecretId);
        autoSave.TrackDebounced(_pwdTencentSecretKey);
        foreach (var section in new[] { _aiText, _aiVision })
        {
            autoSave.TrackImmediate(section.Platform);
            autoSave.TrackDebounced(section.ApiUrl);
            autoSave.TrackDebounced(section.ApiKey);
            autoSave.TrackDebounced(section.Model);
        }
        autoSave.TrackImmediate(_chkVisionSameAccount);
        Children.Add(new Border { Height = 12 });
    }

    private bool SaveSettings()
    {
        var accounts = _configManager.Get().Accounts;
        var sameAccount = _chkVisionSameAccount.IsChecked == true;

        if (_tencentSecretId.Matches(_txtTencentSecretId.Text) &&
            _tencentSecretKey.Matches(_pwdTencentSecretKey.Password) &&
            _aiText.IsUnchanged(accounts.AiText.Platform, accounts.AiText.Model) &&
            _aiVision.IsUnchanged(accounts.AiVision.Platform, accounts.AiVision.Model) &&
            accounts.AiVision.SameAccountAsText == sameAccount)
        {
            return false;
        }

        var secretId = _tencentSecretId.Resolve(_txtTencentSecretId.Text);
        var secretKey = _tencentSecretKey.Resolve(_pwdTencentSecretKey.Password);
        var (textUrl, textKey) = _aiText.ResolveSecrets();
        var (visionUrl, visionKey) = _aiVision.ResolveSecrets();
        var textPlatform = (AiPlatform)_aiText.SelectedPlatformTag;
        var visionPlatform = (AiPlatform)_aiVision.SelectedPlatformTag;
        var textModel = _aiText.ModelName;
        var visionModel = _aiVision.ModelName;

        _configManager.Update(current =>
        {
            current.Accounts.Tencent.SecretIdEncrypted = secretId;
            current.Accounts.Tencent.SecretKeyEncrypted = secretKey;
            current.Accounts.AiText.Platform = textPlatform;
            current.Accounts.AiText.ApiUrlEncrypted = textUrl;
            current.Accounts.AiText.ApiKeyEncrypted = textKey;
            current.Accounts.AiText.Model = textModel;
            current.Accounts.AiVision.SameAccountAsText = sameAccount;
            current.Accounts.AiVision.Platform = visionPlatform;
            current.Accounts.AiVision.ApiUrlEncrypted = visionUrl;
            current.Accounts.AiVision.ApiKeyEncrypted = visionKey;
            current.Accounts.AiVision.Model = visionModel;
        });

        _tencentSecretId.MarkSaved(_txtTencentSecretId.Text, secretId);
        _tencentSecretKey.MarkSaved(_pwdTencentSecretKey.Password, secretKey);
        _aiText.MarkSaved(textUrl, textKey);
        _aiVision.MarkSaved(visionUrl, visionKey);
        return true;
    }
}
