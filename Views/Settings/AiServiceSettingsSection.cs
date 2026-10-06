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
    public static readonly AiPlatformPreset OpenAi = new("https://api.openai.com/v1/chat/completions", "gpt-4o-mini");

    // 使用 -latest 别名，Google 发布新一代 Flash 模型后无需修改预设。
    public static readonly AiPlatformPreset GoogleAiStudio = new("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-flash-latest");

    public static readonly AiPlatformPreset DeepSeek = new("https://api.deepseek.com/chat/completions", "deepseek-chat");

    public static readonly IReadOnlyList<AiPlatformPreset> All = new[] { OpenAi, GoogleAiStudio, DeepSeek };
}

/// <summary>平台下拉项：Preset 为 null 表示自定义接口。</summary>
internal sealed record AiPlatformOption(string Label, object Tag, AiPlatformPreset? Preset);

/// <summary>
/// OCR 与翻译设置页共用的 AI 服务字段组：平台预设、接口地址预览、API Key、模型、获取模型与测试。
/// </summary>
internal sealed class AiServiceSettingsSection
{
    private readonly IReadOnlyList<AiPlatformOption> _platforms;
    private readonly Func<string, string, string, Task<(bool Success, string Message)>> _test;
    private readonly TextBlock _apiUrlHint;
    private EncryptedSetting _apiUrlSetting = new(null);
    private EncryptedSetting _apiKeySetting = new(null);
    private bool _loading;

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
        content.Children.Add(SettingsLayout.CreateInlineField("平台", Platform));

        ApiUrl = SettingsLayout.CreateTextBox();
        _apiUrlHint = SettingsLayout.CreateHint(string.Empty, inline: false);
        ApiUrl.TextChanged += (_, _) => UpdateApiUrlPreview();
        content.Children.Add(SettingsLayout.CreateInlineFieldWithHint("API URL", ApiUrl, _apiUrlHint));

        ApiKey = SettingsLayout.CreatePasswordField();
        content.Children.Add(SettingsLayout.CreateInlineField("API Key", ApiKey));

        Model = SettingsLayout.CreateEditableComboBox();
        content.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模型", Model, "可获取列表，也可手动输入。", isLast: true));

        var fetchModels = SettingsLayout.CreateSecondaryActionButton("获取模型");
        fetchModels.Click += FetchModels_Click;
        var testButton = SettingsLayout.CreateSecondaryActionButton("测试");
        testButton.Click += Test_Click;
        content.Children.Add(SettingsLayout.CreateActionRow(fetchModels, testButton));
    }

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
                var models = await OpenAiChatClient.FetchModelsAsync(ApiUrl.Text, ApiKey.Password);
                var currentModel = ModelName;

                Model.Items.Clear();
                foreach (var model in models)
                    Model.Items.Add(model);

                Model.Text = !string.IsNullOrWhiteSpace(currentModel)
                    ? currentModel
                    : models.FirstOrDefault() ?? string.Empty;

                ToastNotification.Show("模型已获取", $"共 {models.Count} 个模型", ToastNotification.ToastType.Success);
            }
            catch (Exception ex)
            {
                ToastNotification.Show("获取模型失败", NetworkErrorMessages.FromException(ex), ToastNotification.ToastType.Error);
            }
        });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        await UiBusyState.RunWithBusyStateAsync(button, "测试中...", async () =>
        {
            try
            {
                var (success, message) = await _test(ApiUrl.Text, ApiKey.Password, ModelName);
                ToastNotification.Show(
                    success ? "测试成功" : "测试失败",
                    message,
                    success ? ToastNotification.ToastType.Success : ToastNotification.ToastType.Error);
            }
            catch (Exception ex)
            {
                ToastNotification.Show("测试失败", NetworkErrorMessages.FromException(ex), ToastNotification.ToastType.Error);
            }
        });
    }
}
