using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private readonly Func<string, string, string, CancellationToken, Task<(bool Success, string Message)>> _test;
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<string>>> _fetchModels;
    private readonly TextBlock _apiUrlHint;
    private readonly TextBlock _testStatus;
    private readonly Func<string?, EncryptedSetting> _secretSetting;
    private EncryptedSetting _apiUrlSetting = new(null);
    private EncryptedSetting _apiKeySetting = new(null);
    private bool _loading;
    private bool _updatingModels;
    private readonly ComboBox _sharedPlatform;
    private readonly StackPanel _connectionFields = new();
    private Func<(string ApiUrl, string ApiKey)>? _sharedCredentials;
    private long _requestGeneration;
    private CancellationTokenSource? _requestCancellation;
    private Action? _restoreBusyState;
    public bool TestSucceeded { get; private set; }

    public AiServiceSettingsSection(
        StackPanel content,
        IReadOnlyList<AiPlatformOption> platforms,
        Func<string, string, string, CancellationToken, Task<(bool Success, string Message)>> test,
        Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? fetchModels = null,
        Func<string?, EncryptedSetting>? secretSetting = null,
        string? accountName = null)
    {
        _platforms = platforms;
        _secretSetting = secretSetting ?? (value => new EncryptedSetting(value));
        // Retired pages must not accept responses that change models or restart auto-save.
        content.Unloaded += (_, _) => CancelHiddenRequest();
        content.IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) CancelHiddenRequest();
        };
        _test = test;
        _fetchModels = fetchModels ?? OpenAiChatClient.FetchModelsAsync;

        Platform = SettingsLayout.CreateComboBox();
        Platform.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var platform in platforms)
            Platform.Items.Add(new ComboBoxItem { Content = platform.Label, Tag = platform.Tag });
        Platform.SelectionChanged += Platform_SelectionChanged;
        var platformRow = SettingsLayout.CreateAccountField("平台", Platform);
        _sharedPlatform = SettingsLayout.CreateComboBox();
        _sharedPlatform.HorizontalAlignment = HorizontalAlignment.Right;
        _sharedPlatform.IsEnabled = false;
        _sharedPlatform.Visibility = Visibility.Collapsed;
        _sharedPlatform.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(_sharedPlatform, "复用来源平台");
        Grid.SetColumn(_sharedPlatform, Grid.GetColumn(Platform));
        platformRow.Children.Add(_sharedPlatform);
        content.Children.Add(platformRow);

        ApiUrl = SettingsLayout.CreateTextBox();
        _apiUrlHint = SettingsLayout.CreateHint(string.Empty, inline: false);
        _apiUrlHint.SetResourceReference(TextBlock.ForegroundProperty, "SettingsErrorBrush");
        ApiUrl.TextChanged += (_, _) => UpdateApiUrlPreview();
        var apiUrlRow = SettingsLayout.CreateAccountFieldWithHint("接口地址", ApiUrl, _apiUrlHint);
        AddTopDivider(apiUrlRow);
        _connectionFields.Children.Add(apiUrlRow);

        ApiKey = SettingsLayout.CreatePasswordField();
        ApiKey.Placeholder = accountName == null ? "输入 API Key" : $"输入 {accountName} API Key";
        var apiKeyRow = SettingsLayout.CreateAccountField("API Key", ApiKey);
        AddTopDivider(apiKeyRow);
        _connectionFields.Children.Add(apiKeyRow);
        content.Children.Add(_connectionFields);

        Model = SettingsLayout.CreateEditableComboBox();
        Model.HorizontalAlignment = HorizontalAlignment.Stretch;
        var modelRow = SettingsLayout.CreateAccountField("模型", Model);
        AddTopDivider(modelRow);
        content.Children.Add(modelRow);

        var fetchModelsButton = SettingsLayout.CreateSecondaryActionButton("选择模型");
        fetchModelsButton.Click += FetchModels_Click;
        var testButton = SettingsLayout.CreateSecondaryActionButton("测试模型");
        testButton.Click += Test_Click;
        _testStatus = SettingsLayout.CreateHint("未测试", inline: true);
        _testStatus.Margin = new Thickness(0, 0, 12, 0);
        _testStatus.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_testStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(_testStatus);
        var buttons = SettingsLayout.CreateActionRow(fetchModelsButton, testButton);
        buttons.Margin = new Thickness(0);
        testButton.Margin = new Thickness(4, 0, 0, 0);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        actions.Children.Add(buttons);
        content.Children.Add(new Border
        {
            Margin = new Thickness(14, 0, 14, 12), MinHeight = 40, Padding = new Thickness(0, 10, 0, 0),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0), SnapsToDevicePixels = true, Child = actions
        });
        ApiUrl.TextChanged += (_, _) => InvalidateTest();
        ApiKey.PasswordChanged += (_, _) => InvalidateTest();
        Model.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                if (!_updatingModels) InvalidateTest();
            }));
    }

    private static void AddTopDivider(Grid row)
    {
        foreach (var child in row.Children.OfType<FrameworkElement>())
            if (Grid.GetRow(child) == 0) child.Margin = new Thickness(child.Margin.Left, child.Margin.Top + 1, child.Margin.Right, child.Margin.Bottom);
        var divider = new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Top,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsRowLineBrush"),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(divider, row.ColumnDefinitions.Count);
        row.Children.Add(divider);
    }

    /// <summary>
    /// 与另一个账号共用连接时保留来源平台、隐藏独立凭据；获取模型与测试改用 <paramref name="sharedCredentials"/> 提供的凭据。
    /// 传 null 恢复为独立账号。
    /// </summary>
    public void UseSharedCredentials(Func<(string ApiUrl, string ApiKey)>? sharedCredentials)
    {
        _sharedCredentials = sharedCredentials;
        var visibility = sharedCredentials == null ? Visibility.Visible : Visibility.Collapsed;
        _connectionFields.Visibility = visibility;
        Platform.Visibility = visibility;
        _sharedPlatform.Visibility = sharedCredentials == null ? Visibility.Collapsed : Visibility.Visible;
        InvalidateTest();
    }

    public void UpdateSharedPlatform(string name)
    {
        if (_sharedCredentials == null) return;
        _sharedPlatform.Items.Clear();
        _sharedPlatform.Items.Add(new ComboBoxItem { Content = name });
        _sharedPlatform.SelectedIndex = 0;
    }

    public void InvalidateTest()
    {
        CancelRequest();
        TestSucceeded = false;
        _testStatus.Text = "未测试";
        _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ConfigurationChanged;

    private void CancelRequest()
    {
        _requestGeneration++;
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        RestoreBusyState();
        Model.IsDropDownOpen = false;
    }

    private void CancelHiddenRequest()
    {
        var wasBusy = _restoreBusyState != null;
        CancelRequest();
        if (!wasBusy) return;
        TestSucceeded = false;
        _testStatus.Text = "未测试";
        _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
    }

    private void RestoreBusyState()
    {
        var restore = _restoreBusyState;
        _restoreBusyState = null;
        restore?.Invoke();
    }

    private async Task RunWithBusyStateAsync(Button button, string busyText, Func<Task> action)
    {
        var originalContent = button.Content;
        var originalEnabled = button.IsEnabled;
        Action restore = () => { button.Content = originalContent; button.IsEnabled = originalEnabled; };
        _restoreBusyState = restore;
        try
        {
            button.IsEnabled = false;
            button.Content = busyText;
            _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
            await action();
        }
        finally
        {
            if (ReferenceEquals(_restoreBusyState, restore)) RestoreBusyState();
        }
    }

    private CancellationToken BeginRequest()
    {
        CancelRequest();
        TestSucceeded = false;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        _requestCancellation = new CancellationTokenSource();
        return _requestCancellation.Token;
    }

    public string PlatformName => (Platform.SelectedItem as ComboBoxItem)?.Content as string ?? "自定义";
    public string ModelDisplayName => DisplayModelName(ModelName);
    internal static string DisplayModelName(string model) => model switch
    {
        "gpt-6-luna" => "GPT-6 Luna",
        "gemini-flash-latest" => "Gemini Flash Latest",
        "deepseek-flash" => "DeepSeek Flash",
        _ => model
    };

    /// <summary>只描述配置完整性，不代表账号已通过服务测试。</summary>
    public string ConfigurationSummary
    {
        get
        {
            return IsConfigured ? "已填写" : "待填写";
        }
    }

    public bool IsConfigured
    {
        get
        {
            var (url, key) = CurrentCredentials();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(ModelName)) return false;
            try
            {
                AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(url);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
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

            _apiUrlSetting = _secretSetting(apiUrlEncrypted);
            _apiKeySetting = _secretSetting(apiKeyEncrypted);
            var preset = _platforms.FirstOrDefault(platform => Equals(platform.Tag, SelectedPlatformTag))?.Preset;
            ApiUrl.Text = string.IsNullOrWhiteSpace(_apiUrlSetting.Plain) && !_apiUrlSetting.IsUnreadable && preset != null
                ? preset.Url : _apiUrlSetting.Plain;
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
        InvalidateTest();
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
        string hint;
        var help = string.Empty;
        if (string.IsNullOrEmpty(apiUrl))
        {
            hint = _apiUrlSetting.IsUnreadable
                ? "已保存的地址无法解密，请重新填写"
                : string.Empty;
        }
        else
        {
            try
            {
                var effectiveUrl = AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(apiUrl);
                hint = string.Empty;
                if (!string.Equals(apiUrl, effectiveUrl, StringComparison.Ordinal))
                    help = $"实际请求：{effectiveUrl}";
            }
            catch (InvalidOperationException ex)
            {
                hint = ex.Message;
            }
        }

        SettingsLayout.SetFieldHelp(ApiUrl, help);
        System.Windows.Automation.AutomationProperties.SetHelpText(ApiUrl, hint.Length > 0 ? hint : help);
        _apiUrlHint.Text = hint;
        _apiUrlHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var credentials = CurrentCredentials();
        var platform = SelectedPlatformTag;
        var cancellationToken = BeginRequest();
        var generation = _requestGeneration;
        bool IsCurrent() => generation == _requestGeneration
            && credentials == CurrentCredentials() && Equals(platform, SelectedPlatformTag);

        _testStatus.Text = "正在获取模型…";
        await RunWithBusyStateAsync(button, "获取中…", async () =>
        {
            try
            {
                var models = await _fetchModels(credentials.ApiUrl, credentials.ApiKey, cancellationToken).WaitAsync(cancellationToken);
                if (!IsCurrent()) return;
                var currentModel = ModelName;

                _updatingModels = true;
                try
                {
                    Model.Items.Clear();
                    foreach (var model in models)
                    {
                        var item = new ComboBoxItem { Content = DisplayModelName(model), Tag = model };
                        TextSearch.SetText(item, model);
                        System.Windows.Automation.AutomationProperties.SetName(item, $"{DisplayModelName(model)}，模型 ID {model}");
                        Model.Items.Add(item);
                        if (model == currentModel) Model.SelectedItem = item;
                    }
                    Model.Text = currentModel;
                }
                finally { _updatingModels = false; }

                _testStatus.Text = $"已获取 {models.Count} 个模型 · 尚未测试";
                _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
                if (Model.IsLoaded && Model.IsVisible && Model.Items.Count > 0)
                {
                    SettingsPickerPlacement.SetMenuAnchor(Model, button);
                    Model.Focus();
                    Model.IsDropDownOpen = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!IsCurrent()) return;
                _testStatus.Text = $"获取模型失败：{NetworkErrorMessages.FromException(ex)}";
                _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsErrorBrush");
            }
        });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var credentials = CurrentCredentials();
        var model = ModelName;
        var platform = SelectedPlatformTag;
        var cancellationToken = BeginRequest();
        var generation = _requestGeneration;
        bool IsCurrent() => generation == _requestGeneration
            && credentials == CurrentCredentials() && model == ModelName
            && Equals(platform, SelectedPlatformTag);
        _testStatus.Text = "正在测试模型…";
        await RunWithBusyStateAsync(button, "测试中…", async () =>
        {
            try
            {
                var (success, message) = await _test(credentials.ApiUrl, credentials.ApiKey, model, cancellationToken).WaitAsync(cancellationToken);
                if (!IsCurrent()) return;
                TestSucceeded = success;
                _testStatus.Text = success ? "测试通过" : $"测试失败：{message}";
                _testStatus.SetResourceReference(TextBlock.ForegroundProperty, success ? "SettingsSuccessBrush" : "SettingsErrorBrush");
                ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!IsCurrent()) return;
                _testStatus.Text = $"测试失败：{NetworkErrorMessages.FromException(ex)}";
                _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsErrorBrush");
            }
        });
    }
}
