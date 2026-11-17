using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private TextBlock _tencentOcrTestStatus = null!;
    private Expander _tencentExpander = null!;
    private Expander _aiTextExpander = null!;
    private Expander _aiVisionExpander = null!;
    private long _tencentCredentialsGeneration;

    public AccountsSettingsPanel(ConfigManager configManager, ServiceAccountTarget? target = null)
    {
        _configManager = configManager;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
        _txtTencentSecretId.TextChanged += (_, _) => ResetTencentTest();
        _pwdTencentSecretKey.PasswordChanged += (_, _) => ResetTencentTest();
        _aiText.ApiUrl.TextChanged += (_, _) => InvalidateSharedTest();
        _aiText.ApiKey.PasswordChanged += (_, _) => InvalidateSharedTest();
        _aiText.ConfigurationChanged += (_, _) => UpdateAccountHeaders();
        _aiVision.ConfigurationChanged += (_, _) => UpdateAccountHeaders();
        UpdateAccountHeaders();
        Unloaded += (_, _) => _tencentCredentialsGeneration++;

        var initialTarget = target ?? (!IsTencentConfigured ? ServiceAccountTarget.Tencent
            : !_aiText.IsConfigured ? ServiceAccountTarget.AiText
            : !_aiVision.IsConfigured ? ServiceAccountTarget.AiVision
            : ServiceAccountTarget.AiText);
        var initialExpander = initialTarget switch
        {
            ServiceAccountTarget.Tencent => _tencentExpander,
            ServiceAccountTarget.AiVision => _aiVisionExpander,
            _ => _aiTextExpander
        };
        initialExpander.IsExpanded = true;

        if (target.HasValue)
        {
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                Loaded -= onLoaded;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!IsLoaded) return;
                    initialExpander.IsExpanded = true;
                    initialExpander.UpdateLayout();
                    initialExpander.BringIntoView(new Rect(0, 0, initialExpander.ActualWidth, 50));
                }));
            };
            Loaded += onLoaded;
        }
    }

    private bool IsTencentConfigured => !string.IsNullOrWhiteSpace(_txtTencentSecretId.Text)
        && !string.IsNullOrWhiteSpace(_pwdTencentSecretKey.Password);

    private void UpdateAccountHeaders()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(_txtTencentSecretId.Text)) missing.Add("Secret ID");
        if (string.IsNullOrWhiteSpace(_pwdTencentSecretKey.Password)) missing.Add("Secret Key");
        SetAccountHeader(_tencentExpander, "腾讯云", IsTencentConfigured ? "已配置" : $"待填写：{string.Join("、", missing)}");
        SetAccountHeader(_aiTextExpander, "AI 文本模型", _aiText.ConfigurationSummary);
        SetAccountHeader(_aiVisionExpander, "AI 视觉模型", _aiVision.ConfigurationSummary);
    }

    private static void SetAccountHeader(Expander expander, string title, string summary)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var status = SettingsLayout.CreateHint(summary);
        status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.ToolTip = summary;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        status.Margin = new Thickness(12, 0, 4, 0);
        status.Visibility = expander.IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        expander.Header = header;
        System.Windows.Automation.AutomationProperties.SetName(expander, $"{title} · {summary}");
    }

    private void ResetTencentTest()
    {
        _tencentCredentialsGeneration++;
        _tencentTestStatus.Text = "翻译服务：未测试";
        _tencentOcrTestStatus.Text = "OCR 服务：未测试";
        _tencentTestStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        _tencentOcrTestStatus.Foreground = _tencentTestStatus.Foreground;
        UpdateAccountHeaders();
    }

    private void InvalidateSharedTest()
    {
        if (_chkVisionSameAccount.IsChecked == true) _aiVision.InvalidateTest();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 腾讯云 ──
        var tencentGroup = SettingsLayout.CreateCollapsibleSection("腾讯云");
        _tencentExpander = tencentGroup.expander;
        var tencent = tencentGroup.content;
        tencent.Children.Add(SettingsLayout.CreateSectionNote("用于腾讯云 OCR、腾讯云翻译和截图翻译识别。"));
        _txtTencentSecretId = SettingsLayout.CreateTextBox();
        tencent.Children.Add(SettingsLayout.CreateInlineField("Secret ID", _txtTencentSecretId));
        _pwdTencentSecretKey = SettingsLayout.CreatePasswordField();
        tencent.Children.Add(SettingsLayout.CreateInlineField("Secret Key", _pwdTencentSecretKey, isLast: true));
        var testTencent = SettingsLayout.CreateSecondaryActionButton("测试翻译服务");
        testTencent.Click += TestTencent_Click;
        var testTencentOcr = SettingsLayout.CreateSecondaryActionButton("测试 OCR 服务");
        testTencentOcr.Click += TestTencentOcr_Click;
        tencent.Children.Add(SettingsLayout.CreateActionRow(testTencent, testTencentOcr));
        _tencentTestStatus = SettingsLayout.CreateHint("翻译服务：未测试", inline: true);
        _tencentTestStatus.Margin = new Thickness(24 + SettingsLayout.InlineLabelWidth, 0, 24, 16);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_tencentTestStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        tencent.Children.Add(_tencentTestStatus);
        _tencentOcrTestStatus = SettingsLayout.CreateHint("OCR 服务：未测试", inline: true);
        _tencentOcrTestStatus.Margin = _tencentTestStatus.Margin;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_tencentOcrTestStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        tencent.Children.Add(_tencentOcrTestStatus);
        Children.Add(tencentGroup.section);

        // ── AI 文本模型 ──
        var aiTextGroup = SettingsLayout.CreateCollapsibleSection("AI 文本模型");
        _aiTextExpander = aiTextGroup.expander;
        var aiText = aiTextGroup.content;
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
        Children.Add(aiTextGroup.section);

        // ── AI 视觉模型 ──
        var aiVisionGroup = SettingsLayout.CreateCollapsibleSection("AI 视觉模型");
        _aiVisionExpander = aiVisionGroup.expander;
        var aiVision = aiVisionGroup.content;
        aiVision.Children.Add(SettingsLayout.CreateSectionNote("用于 AI OCR，模型需要支持图片输入。"));
        _chkVisionSameAccount = SettingsLayout.CreateSwitch();
        aiVision.Children.Add(SettingsLayout.CreateSwitchRow(
            "共用文本模型账号",
            "单独选择支持图片的模型",
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
        Children.Add(aiVisionGroup.section);
        foreach (var expander in new[] { _tencentExpander, _aiTextExpander, _aiVisionExpander })
        {
            ((StackPanel)expander.Content).SetBinding(UIElement.IsEnabledProperty,
                new System.Windows.Data.Binding(nameof(Expander.IsExpanded)) { Source = expander });
            expander.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, expander)) UpdateAccountHeaders(); };
            expander.Collapsed += (_, e) => { if (ReferenceEquals(e.OriginalSource, expander)) UpdateAccountHeaders(); };
        }
    }

    private static async Task<(bool Success, string Message)> TestAiTextAsync(string url, string key, string model)
    {
        var result = await AiTranslationService.TestAsync(url, key, model);
        return result.Success
            ? (true, $"翻译结果：{result.TranslatedText}")
            : (false, result.ErrorMessage ?? "未知错误");
    }

    private async void TestTencent_Click(object sender, RoutedEventArgs e) =>
        await TestTencentServiceAsync(sender, _tencentTestStatus, "翻译服务", TencentTranslationService.TestAsync);

    private async void TestTencentOcr_Click(object sender, RoutedEventArgs e) =>
        await TestTencentServiceAsync(sender, _tencentOcrTestStatus, "OCR 服务",
            (id, key) => TencentOcrService.TestAsync(id, key));

    private async Task TestTencentServiceAsync(object sender, TextBlock status, string serviceName,
        Func<string, string, Task<(bool Success, string Message)>> test)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        var secretId = _txtTencentSecretId.Text;
        var secretKey = _pwdTencentSecretKey.Password;
        var generation = _tencentCredentialsGeneration;
        bool IsCurrent() => generation == _tencentCredentialsGeneration
            && secretId == _txtTencentSecretId.Text && secretKey == _pwdTencentSecretKey.Password;
        status.Text = $"正在测试{serviceName}…";
        status.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        await UiBusyState.RunWithBusyStateAsync(button, "测试中...", async () =>
        {
            try
            {
                var (success, message) = await test(secretId, secretKey);
                if (!IsCurrent()) return;
                status.Text = success ? $"{serviceName}测试通过" : $"{serviceName}测试失败：{message}";
                status.Foreground = (System.Windows.Media.Brush)FindResource(success ? "SuccessTextBrush" : "ErrorBrush");
            }
            catch (Exception ex)
            {
                if (!IsCurrent()) return;
                status.Text = $"{serviceName}测试失败：{NetworkErrorMessages.FromException(ex)}";
                status.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
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
