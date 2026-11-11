using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace STool.Views.Settings;

/// <summary>AI 平台预设：接口地址与推荐模型。</summary>
internal sealed record AiPlatformPreset(string Url, string DefaultModel)
{
    public static readonly AiPlatformPreset OpenAi = new("https://api.openai.com/v1/chat/completions", "gpt-6-luna");

    // 使用 -latest 别名，Google 发布新一代 Flash 模型后无需修改预设。
    public static readonly AiPlatformPreset GoogleAiStudio = new("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-flash-latest");

    public static readonly AiPlatformPreset DeepSeek = new("https://api.deepseek.com/chat/completions", "deepseek-flash");

    public static readonly IReadOnlyList<AiPlatformPreset> All = new[] { OpenAi, GoogleAiStudio, DeepSeek };
}

/// <summary>平台下拉项：Preset 为 null 表示自定义接口。</summary>
internal sealed record AiPlatformOption(string Label, object Tag, AiPlatformPreset? Preset);

/// <summary>
/// 服务账号页的 AI 账号字段组：平台预设、接口地址预览、API Key、模型、获取模型与测试。
/// </summary>
internal sealed class AiServiceSettingsSection
{
    private readonly IReadOnlyList<AiPlatformOption> _platforms;
    private readonly Func<string, string, string, Task<(bool Success, string Message)>> _test;
    private readonly TextBlock _apiUrlHint;
    private readonly TextBlock _testStatus;
    private EncryptedSetting _apiUrlSetting = new(null);
    private EncryptedSetting _apiKeySetting = new(null);
    private bool _loading;
    private readonly FrameworkElement _platformRow;
    private readonly FrameworkElement _apiUrlRow;
    private readonly FrameworkElement _apiKeyRow;
    private Func<(string ApiUrl, string ApiKey)>? _sharedCredentials;

    public AiServiceSettingsSection(
        StackPanel content,
        IReadOnlyList<AiPlatformOption> platforms,
        Func<string, string, string, Task<(bool Success, string Message)>> test)
    {
        _platforms = platforms;
        _test = test;

        Platform = SettingsLayout.CreateComboBox();
        foreach (var platform in platforms)
            Platform.Items.Add(new ComboBoxItem { Content = platform.Label, Tag = platform.Tag });
        Platform.SelectionChanged += Platform_SelectionChanged;
        _platformRow = SettingsLayout.CreateInlineField("平台", Platform);
        content.Children.Add(_platformRow);

        ApiUrl = SettingsLayout.CreateTextBox();
        _apiUrlHint = SettingsLayout.CreateHint(string.Empty, inline: false);
        ApiUrl.TextChanged += (_, _) => UpdateApiUrlPreview();
        _apiUrlRow = SettingsLayout.CreateInlineFieldWithHint("API URL", ApiUrl, _apiUrlHint);
        content.Children.Add(_apiUrlRow);

        ApiKey = SettingsLayout.CreatePasswordField();
        _apiKeyRow = SettingsLayout.CreateInlineField("API Key", ApiKey);
        content.Children.Add(_apiKeyRow);

        Model = SettingsLayout.CreateEditableComboBox();
        content.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模型", Model, "可获取列表，也可手动输入。", isLast: true));

        var fetchModels = SettingsLayout.CreateSecondaryActionButton("获取模型");
        fetchModels.Click += FetchModels_Click;
        var testButton = SettingsLayout.CreateSecondaryActionButton("测试模型");
        testButton.Click += Test_Click;
        content.Children.Add(SettingsLayout.CreateActionRow(fetchModels, testButton));
        _testStatus = SettingsLayout.CreateHint("未测试", inline: true);
        _testStatus.Margin = new Thickness(24 + SettingsLayout.InlineLabelWidth, 0, 24, 16);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_testStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        content.Children.Add(_testStatus);
        ApiUrl.TextChanged += (_, _) => InvalidateTest();
        ApiKey.PasswordChanged += (_, _) => InvalidateTest();
        Model.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => InvalidateTest()));
    }

    /// <summary>
    /// 与另一个账号共用平台、地址和密钥时隐藏这三行；获取模型与测试改用 <paramref name="sharedCredentials"/> 提供的凭据。
    /// 传 null 恢复为独立账号。
    /// </summary>
    public void UseSharedCredentials(Func<(string ApiUrl, string ApiKey)>? sharedCredentials)
    {
        _sharedCredentials = sharedCredentials;
        var visibility = sharedCredentials == null ? Visibility.Visible : Visibility.Collapsed;
        _platformRow.Visibility = visibility;
        _apiUrlRow.Visibility = visibility;
        _apiKeyRow.Visibility = visibility;
        InvalidateTest();
    }

    public void InvalidateTest()
    {
        _testStatus.Text = "未测试";
        _testStatus.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush");
    }

    private (string ApiUrl, string ApiKey) CurrentCredentials() =>
        _sharedCredentials?.Invoke() ?? (ApiUrl.Text, ApiKey.Password);

    public ComboBox Platform { get; }
    public TextBox ApiUrl { get; }
    public SecurePasswordField ApiKey { get; }
    public ComboBox Model { get; }

    public object SelectedPlatformTag =>
        (Platform.SelectedItem as ComboBoxItem)?.Tag ?? _platforms[^1].Tag;

    public string ModelName => Model.Text.Trim();

    /// <summary>已保存的地址或密钥无法用当前 secure.key 解密。</summary>
    public bool HasUnreadableSecrets => _apiUrlSetting.IsUnreadable || _apiKeySetting.IsUnreadable;

    public void Load(object platformTag, string? apiUrlEncrypted, string? apiKeyEncrypted, string? model)
    {
        _loading = true;
        try
        {
            var index = _platforms.ToList().FindIndex(platform => Equals(platform.Tag, platformTag));
            Platform.SelectedIndex = index >= 0 ? index : 0;

            _apiUrlSetting = new EncryptedSetting(apiUrlEncrypted);
            _apiKeySetting = new EncryptedSetting(apiKeyEncrypted);
            ApiUrl.Text = _apiUrlSetting.Plain;
            ApiKey.Password = _apiKeySetting.Plain;
            Model.Text = model ?? string.Empty;
            UpdateApiUrlPreview();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>与已保存的配置相比是否有改动。</summary>
    public bool IsUnchanged(object savedPlatformTag, string? savedModel) =>
        Equals(SelectedPlatformTag, savedPlatformTag) &&
        _apiUrlSetting.Matches(ApiUrl.Text) &&
        _apiKeySetting.Matches(ApiKey.Password) &&
        (savedModel ?? string.Empty) == ModelName;

    public (string? ApiUrlEncrypted, string? ApiKeyEncrypted) ResolveSecrets() =>
        (_apiUrlSetting.Resolve(ApiUrl.Text), _apiKeySetting.Resolve(ApiKey.Password));

    public void MarkSaved(string? apiUrlEncrypted, string? apiKeyEncrypted)
    {
        _apiUrlSetting.MarkSaved(ApiUrl.Text, apiUrlEncrypted);
        _apiKeySetting.MarkSaved(ApiKey.Password, apiKeyEncrypted);
    }

    private void Platform_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        var preset = _platforms.FirstOrDefault(platform => Equals(platform.Tag, SelectedPlatformTag))?.Preset;
        if (preset == null)
            return;

        ApiUrl.Text = preset.Url;

        // 只替换空模型或其他平台的默认模型，保留用户手动填写的模型名。
        var current = ModelName;
        if (current.Length == 0 || AiPlatformPreset.All.Any(item => string.Equals(item.DefaultModel, current, StringComparison.OrdinalIgnoreCase)))
            Model.Text = preset.DefaultModel;
    }

    private void UpdateApiUrlPreview()
    {
        var apiUrl = ApiUrl.Text.Trim();
        if (string.IsNullOrEmpty(apiUrl))
        {
            _apiUrlHint.Text = _apiUrlSetting.IsUnreadable
                ? "已保存的地址无法解密，请重新填写"
                : "输入域名或完整的 Chat Completions 地址";
            return;
        }

        try
        {
            _apiUrlHint.Text = $"实际请求：{AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(apiUrl)}";
        }
        catch (InvalidOperationException ex)
        {
            _apiUrlHint.Text = ex.Message;
        }
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        await UiBusyState.RunWithBusyStateAsync(button, "获取中...", async () =>
        {
            try
            {
                var (apiUrl, apiKey) = CurrentCredentials();
                var models = await OpenAiChatClient.FetchModelsAsync(apiUrl, apiKey);
                var currentModel = ModelName;

                Model.Items.Clear();
                foreach (var model in models)
                    Model.Items.Add(model);

                Model.Text = !string.IsNullOrWhiteSpace(currentModel)
                    ? currentModel
                    : models.FirstOrDefault() ?? string.Empty;

                _testStatus.Text = $"已获取 {models.Count} 个模型 · 尚未测试";
            }
            catch (Exception ex)
            {
                _testStatus.Text = $"获取模型失败：{NetworkErrorMessages.FromException(ex)}";
                _testStatus.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ErrorBrush");
            }
        });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var credentials = CurrentCredentials();
        var model = ModelName;
        _testStatus.Text = "正在测试模型…";
        await UiBusyState.RunWithBusyStateAsync(button, "测试中...", async () =>
        {
            try
            {
                var (success, message) = await _test(credentials.ApiUrl, credentials.ApiKey, model);
                if (credentials != CurrentCredentials() || model != ModelName) return;
                _testStatus.Text = success ? "测试通过 · 模型可用" : $"测试失败：{message}";
                _testStatus.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource(success ? "SuccessBrush" : "ErrorBrush");
            }
            catch (Exception ex)
            {
                if (credentials != CurrentCredentials() || model != ModelName) return;
                _testStatus.Text = $"测试失败：{NetworkErrorMessages.FromException(ex)}";
                _testStatus.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ErrorBrush");
            }
        });
    }
}
