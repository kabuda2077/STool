using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;
using STool.Modules.Translation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace STool.Views.Settings;

/// <summary>按文字识别与翻译管理独立账号；凭据仅在明确勾选复用后关联，模型始终独立。</summary>
public class AccountsSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly Func<string?, EncryptedSetting> _secretSetting;
    private CloudAccountEditor _tencentOcr = null!;
    private CloudAccountEditor _tencentTranslation = null!;
    private AiServiceSettingsSection _aiText = null!;
    private AiServiceSettingsSection _aiVision = null!;
    private ReuseOffer _visionReuse = null!;
    private ReuseOffer _textReuse = null!;
    private Expander _aiTextExpander = null!;
    private Expander _aiVisionExpander = null!;
    private bool _loading;
    private bool _updating;

    public AccountsSettingsPanel(ConfigManager configManager, ServiceAccountTarget? target = null)
        : this(configManager, target, value => new EncryptedSetting(value)) { }

    internal AccountsSettingsPanel(ConfigManager configManager, ServiceAccountTarget? target, Func<string?, EncryptedSetting> secretSetting)
    {
        _configManager = configManager;
        _secretSetting = secretSetting;
        InitializeUI();
        LoadSettings();
        WireChanges();
        EnableAutoSave();
        UpdateAccountViews();
        if (target.HasValue)
        {
            var initialExpander = GetExpander(target.Value);
            initialExpander.IsExpanded = true;
            Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!IsLoaded) return;
                OpenSource(target.Value);
            }));
        }
    }

    private Expander GetExpander(ServiceAccountTarget target) => target switch
    {
        ServiceAccountTarget.TencentOcr => _tencentOcr.Expander,
        ServiceAccountTarget.TencentTranslation => _tencentTranslation.Expander,
        ServiceAccountTarget.AiVision => _aiVisionExpander,
        _ => _aiTextExpander
    };

    private void InitializeUI()
    {
        var recognition = SettingsLayout.CreateSectionContent("文字识别");
        _tencentOcr = new CloudAccountEditor("腾讯云 OCR", "复用翻译的腾讯云账号", "翻译", "测试识别", TencentOcrService.TestAsync, _secretSetting);
        recognition.Children.Add(_tencentOcr.Expander);
        var vision = CreateAccountEditor("AI 文字识别");
        _aiVisionExpander = vision.expander;
        _aiVision = new AiServiceSettingsSection(vision.content, Platforms(), AiVisionOcrService.TestAsync, secretSetting: _secretSetting, accountName: "AI 文字识别");
        SettingsLayout.SetFieldHelp(_aiVision.Model, "模型需支持图片输入。");
        _visionReuse = new ReuseOffer("复用翻译的 AI 连接", "翻译", () => OpenSource(ServiceAccountTarget.AiText));
        vision.content.Children.Insert(1, _visionReuse.Panel);
        recognition.Children.Add(CreateAccountDivider());
        recognition.Children.Add(_aiVisionExpander);
        Children.Add(SettingsLayout.CreateSection(recognition));

        var translation = SettingsLayout.CreateSectionContent("翻译");
        _tencentTranslation = new CloudAccountEditor("腾讯云翻译", "复用文字识别的腾讯云账号", "文字识别", "测试翻译", TencentTranslationService.TestAsync, _secretSetting);
        translation.Children.Add(_tencentTranslation.Expander);
        var text = CreateAccountEditor("AI 翻译");
        _aiTextExpander = text.expander;
        _aiText = new AiServiceSettingsSection(text.content, Platforms(), TestAiTextAsync, secretSetting: _secretSetting, accountName: "AI 翻译");
        _textReuse = new ReuseOffer("复用文字识别的 AI 连接", "文字识别", () => OpenSource(ServiceAccountTarget.AiVision));
        text.content.Children.Insert(1, _textReuse.Panel);
        translation.Children.Add(CreateAccountDivider());
        translation.Children.Add(_aiTextExpander);
        Children.Add(SettingsLayout.CreateSection(translation));

        _tencentOcr.Reuse.OpenSource = () => OpenSource(ServiceAccountTarget.TencentTranslation);
        _tencentTranslation.Reuse.OpenSource = () => OpenSource(ServiceAccountTarget.TencentOcr);
        foreach (var expander in AllExpanders())
        {
            ((StackPanel)expander.Content).SetBinding(IsEnabledProperty,
                new System.Windows.Data.Binding(nameof(Expander.IsExpanded)) { Source = expander });
            expander.Expanded += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, expander)) return;
                foreach (var other in AllExpanders())
                    if (!ReferenceEquals(other, expander)) other.IsExpanded = false;
            };
        }
    }

    private Expander[] AllExpanders() => [_tencentOcr.Expander, _aiVisionExpander, _tencentTranslation.Expander, _aiTextExpander];
    private static (StackPanel content, Expander expander) CreateAccountEditor(string title)
    {
        var content = new StackPanel();
        return (content, new Expander
        {
            Header = title, Content = content,
            Style = (Style)Application.Current.FindResource("SettingsExpander")
        });
    }

    private static Border CreateAccountDivider() => new()
    {
        Height = 1,
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsRowLineBrush"),
        SnapsToDevicePixels = true,
        IsHitTestVisible = false
    };

    private static Grid CreateDetailField(string label, FrameworkElement input)
    {
        var row = SettingsLayout.CreateAccountField(label, input);
        foreach (var child in row.Children.OfType<FrameworkElement>())
            child.Margin = new Thickness(child.Margin.Left, child.Margin.Top + 1, child.Margin.Right, child.Margin.Bottom);
        var divider = CreateAccountDivider();
        divider.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumnSpan(divider, row.ColumnDefinitions.Count);
        row.Children.Add(divider);
        return row;
    }
    private void OpenSource(ServiceAccountTarget target)
    {
        var source = target switch
        {
            ServiceAccountTarget.TencentOcr when _tencentOcr.Reuse.IsChecked => ServiceAccountTarget.TencentTranslation,
            ServiceAccountTarget.TencentTranslation when _tencentTranslation.Reuse.IsChecked => ServiceAccountTarget.TencentOcr,
            ServiceAccountTarget.AiVision when _visionReuse.IsChecked => ServiceAccountTarget.AiText,
            ServiceAccountTarget.AiText when _textReuse.IsChecked => ServiceAccountTarget.AiVision,
            _ => target
        };
        var missing = FirstMissingCredential(source);
        if (missing != null) target = source;
        var expander = GetExpander(target);
        expander.IsExpanded = true;
        expander.UpdateLayout();
        FrameworkElement field = missing ?? (target switch
        {
            ServiceAccountTarget.AiVision => _aiVision.Model,
            ServiceAccountTarget.AiText => _aiText.Model,
            _ => expander
        });
        if (field is SecurePasswordField password)
            password.Children.OfType<System.Windows.Controls.Control>().First(control =>
                (control is PasswordBox or TextBox) && control.Visibility == Visibility.Visible).Focus();
        else if (ReferenceEquals(field, expander))
            expander.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        else
            field.Focus();

        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(field); parent != null;
             parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer viewer) continue;
            var top = field.TranslatePoint(new System.Windows.Point(), viewer).Y;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top - 50);
            break;
        }
    }

    private FrameworkElement? FirstMissingCredential(ServiceAccountTarget target)
    {
        var (identifier, secret) = target switch
        {
            ServiceAccountTarget.TencentOcr => (_tencentOcr.SecretId, _tencentOcr.SecretKey),
            ServiceAccountTarget.TencentTranslation => (_tencentTranslation.SecretId, _tencentTranslation.SecretKey),
            ServiceAccountTarget.AiVision => (_aiVision.ApiUrl, _aiVision.ApiKey),
            _ => (_aiText.ApiUrl, _aiText.ApiKey)
        };
        return string.IsNullOrWhiteSpace(identifier.Text) ? identifier
            : string.IsNullOrWhiteSpace(secret.Password) ? secret : null;
    }

    private static AiPlatformOption[] Platforms() =>
    [
        new("OpenAI", AiPlatform.OpenAI, AiPlatformPreset.OpenAi),
        new("Google AI Studio", AiPlatform.GoogleAiStudio, AiPlatformPreset.GoogleAiStudio),
        new("DeepSeek", AiPlatform.DeepSeek, AiPlatformPreset.DeepSeek),
        new("自定义", AiPlatform.Custom, null)
    ];

    private static async Task<(bool Success, string Message)> TestAiTextAsync(string url, string key, string model, CancellationToken token)
    {
        var result = await AiTranslationService.TestAsync(url, key, model, token);
        return result.Success ? (true, $"翻译结果：{result.TranslatedText}") : (false, result.ErrorMessage ?? "未知错误");
    }

    private void WireChanges()
    {
        _tencentOcr.ConfigurationChanged += (_, _) => CloudChanged(true);
        _tencentTranslation.ConfigurationChanged += (_, _) => CloudChanged(false);
        _tencentOcr.TestStateChanged += (_, _) => UpdateAccountViews();
        _tencentTranslation.TestStateChanged += (_, _) => UpdateAccountViews();
        _aiText.ConfigurationChanged += (_, _) => UpdateAccountViews();
        _aiVision.ConfigurationChanged += (_, _) => UpdateAccountViews();
        void AiCredentialsChanged(bool forOcr)
        {
            if (_loading || _updating) return;
            var dependent = forOcr ? _aiText : _aiVision;
            var reuse = forOcr ? _textReuse : _visionReuse;
            if (reuse.IsChecked) dependent.InvalidateTest();
            UpdateAccountViews();
        }
        _aiText.ApiUrl.TextChanged += (_, _) => AiCredentialsChanged(false);
        _aiText.ApiKey.PasswordChanged += (_, _) => AiCredentialsChanged(false);
        _aiText.Platform.SelectionChanged += (_, _) => AiCredentialsChanged(false);
        _aiVision.ApiUrl.TextChanged += (_, _) => AiCredentialsChanged(true);
        _aiVision.ApiKey.PasswordChanged += (_, _) => AiCredentialsChanged(true);
        _aiVision.Platform.SelectionChanged += (_, _) => AiCredentialsChanged(true);
        _visionReuse.CheckBox.Checked += (_, _) => ReuseChanged(true, true);
        _visionReuse.CheckBox.Unchecked += (_, _) => ReuseChanged(true, true);
        _textReuse.CheckBox.Checked += (_, _) => ReuseChanged(false, true);
        _textReuse.CheckBox.Unchecked += (_, _) => ReuseChanged(false, true);
        _tencentOcr.Reuse.CheckBox.Checked += (_, _) => ReuseChanged(true, false);
        _tencentOcr.Reuse.CheckBox.Unchecked += (_, _) => ReuseChanged(true, false);
        _tencentTranslation.Reuse.CheckBox.Checked += (_, _) => ReuseChanged(false, false);
        _tencentTranslation.Reuse.CheckBox.Unchecked += (_, _) => ReuseChanged(false, false);
    }

    private void CloudChanged(bool forOcr)
    {
        if (_loading || _updating) return;
        var dependent = forOcr ? _tencentTranslation : _tencentOcr;
        if (dependent.Reuse.IsChecked) dependent.InvalidateTest();
        UpdateAccountViews();
    }

    private void ReuseChanged(bool forOcr, bool ai)
    {
        if (_loading || _updating) return;
        var offer = ai ? (forOcr ? _visionReuse : _textReuse) : (forOcr ? _tencentOcr.Reuse : _tencentTranslation.Reuse);
        var draft = DraftAccounts();
        var accepted = ai ? draft.TrySetAiReuse(forOcr, offer.IsChecked, value => value)
            : draft.TrySetTencentReuse(forOcr, offer.IsChecked);
        if (!accepted)
        {
            _updating = true;
            offer.CheckBox.IsChecked = false;
            _updating = false;
        }
        ApplyReuseModes();
        UpdateAccountViews();
    }

    // Plain draft values are used only for compatibility checks and effective UI credentials.
    // They never go to ConfigManager; persistence resolves each original encrypted field below.
    private AccountsConfig DraftAccounts() => new()
    {
        TencentOcr = _tencentOcr.Draft(AccountReuseSource.Translation),
        TencentTranslation = _tencentTranslation.Draft(AccountReuseSource.Ocr),
        AiVision = new AiVisionAccountConfig
        {
            Platform = (AiPlatform)_aiVision.SelectedPlatformTag,
            ApiUrlEncrypted = _aiVision.ApiUrl.Text.Trim(), ApiKeyEncrypted = _aiVision.ApiKey.Password.Trim(), Model = _aiVision.ModelName,
            ReuseFrom = _visionReuse.IsChecked ? AccountReuseSource.Translation : AccountReuseSource.None
        },
        AiText = new AiAccountConfig
        {
            Platform = (AiPlatform)_aiText.SelectedPlatformTag,
            ApiUrlEncrypted = _aiText.ApiUrl.Text.Trim(), ApiKeyEncrypted = _aiText.ApiKey.Password.Trim(), Model = _aiText.ModelName,
            ReuseFrom = _textReuse.IsChecked ? AccountReuseSource.Ocr : AccountReuseSource.None
        }
    };

    private void ApplyReuseModes()
    {
        _aiVision.UseSharedCredentials(_visionReuse.IsChecked ? () =>
        {
            var source = DraftAccounts().ResolveVision(value => value);
            return (source.ApiUrlEncrypted ?? "", source.ApiKeyEncrypted ?? "");
        } : null);
        _aiText.UseSharedCredentials(_textReuse.IsChecked ? () =>
        {
            var source = DraftAccounts().ResolveText(value => value);
            return (source.ApiUrlEncrypted ?? "", source.ApiKeyEncrypted ?? "");
        } : null);
        _tencentOcr.UseSharedCredentials(_tencentOcr.Reuse.IsChecked ? () =>
        {
            var source = DraftAccounts().ResolveTencentOcr();
            return (source.SecretIdEncrypted ?? "", source.SecretKeyEncrypted ?? "");
        } : null);
        _tencentTranslation.UseSharedCredentials(_tencentTranslation.Reuse.IsChecked ? () =>
        {
            var source = DraftAccounts().ResolveTencentTranslation();
            return (source.SecretIdEncrypted ?? "", source.SecretKeyEncrypted ?? "");
        } : null);
    }

    private void UpdateAccountViews()
    {
        if (_loading || _updating) return;
        _updating = true;
        try
        {
            var draft = DraftAccounts();
            _tencentOcr.Reuse.Update(draft.CanReuseTencent(true), draft.ResolveTencentOcr().IsConfigured, "腾讯云");
            _tencentTranslation.Reuse.Update(draft.CanReuseTencent(false), draft.ResolveTencentTranslation().IsConfigured, "腾讯云");
            var visionSource = draft.ResolveVision(value => value);
            var textSource = draft.ResolveText(value => value);
            _visionReuse.Update(draft.CanReuseAi(true, value => value), visionSource.IsConnectionConfigured,
                PlatformLabel(draft.AiText.Platform), SourceAddress(draft.AiText.ApiUrlEncrypted));
            _textReuse.Update(draft.CanReuseAi(false, value => value), textSource.IsConnectionConfigured,
                PlatformLabel(draft.AiVision.Platform), SourceAddress(draft.AiVision.ApiUrlEncrypted));
            SetHeader(_tencentOcr.Expander, "腾讯云 OCR", null, _tencentOcr.IsConfigured);
            SetHeader(_tencentTranslation.Expander, "腾讯云翻译", null, _tencentTranslation.IsConfigured);
            var visionPlatform = _visionReuse.IsChecked ? draft.AiText.Platform : draft.AiVision.Platform;
            var textPlatform = _textReuse.IsChecked ? draft.AiVision.Platform : draft.AiText.Platform;
            _aiVision.UpdateSharedPlatform(PlatformLabel(visionPlatform));
            _aiText.UpdateSharedPlatform(PlatformLabel(textPlatform));
            SetHeader(_aiVisionExpander, "AI 文字识别", $"{PlatformLabel(visionPlatform)} · {ModelLabel(_aiVision)}", _aiVision.IsConfigured);
            SetHeader(_aiTextExpander, "AI 翻译", $"{PlatformLabel(textPlatform)} · {ModelLabel(_aiText)}", _aiText.IsConfigured);
        }
        finally { _updating = false; }
    }

    private static string PlatformLabel(AiPlatform platform) => platform switch
    {
        AiPlatform.OpenAI => "OpenAI", AiPlatform.GoogleAiStudio => "Google AI Studio", AiPlatform.DeepSeek => "DeepSeek", _ => "自定义"
    };
    private static string ModelLabel(AiServiceSettingsSection section) => section.ModelName.Length == 0 ? "未选择模型" : section.ModelDisplayName;

    private static string SourceAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "未填写接口地址";
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
    }

    private static void SetHeader(Expander expander, string title, string? summary, bool configured)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = (System.Windows.Media.Geometry)Application.Current.FindResource("IconServiceAccount"),
            Stroke = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsMutedBrush"),
            StrokeThickness = 1.5,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        };
        var iconCanvas = new Canvas { Width = 24, Height = 24 };
        iconCanvas.Children.Add(iconPath);
        var icon = new Viewbox
        {
            Width = 22, Height = 22, Child = iconCanvas,
            Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(icon);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.Normal, FontSize = SettingsLayout.BodyFontSize,
            Height = 20.5, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (summary != null)
        {
            var sub = SettingsLayout.CreateHint(summary);
            sub.ClearValue(MarginProperty);
            sub.SetResourceReference(StyleProperty, "SettingsAccountSummary");
            SettingsLayout.SetTextHelp(sub);
            label.Children.Add(sub);
        }
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        var stateText = configured ? "已填写" : "待填写";
        var state = SettingsLayout.CreateHint(stateText);
        state.Margin = new Thickness(12, 0, 12, 0);
        state.VerticalAlignment = VerticalAlignment.Center;
        state.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(state, 2);
        header.Children.Add(state);
        var details = SettingsLayout.CreateHint("详情");
        details.SetResourceReference(TextBlock.FontSizeProperty, "SettingsDetailHeaderFontSize");
        details.Margin = new Thickness(0);
        details.VerticalAlignment = VerticalAlignment.Center;
        details.ClearValue(TextBlock.ForegroundProperty);
        details.SetResourceReference(TextBlock.StyleProperty, "SettingsAccountDetailLabel");
        Grid.SetColumn(details, 3);
        header.Children.Add(details);
        expander.Header = header;
        System.Windows.Automation.AutomationProperties.SetName(expander,
            summary == null ? $"{title}，{stateText}，详情" : $"{title}，{summary}，{stateText}，详情");
    }

    private void LoadSettings()
    {
        _loading = true;
        var accounts = _configManager.Get().Accounts;
        _tencentOcr.Load(accounts.TencentOcr, AccountReuseSource.Translation);
        _tencentTranslation.Load(accounts.TencentTranslation, AccountReuseSource.Ocr);
        _aiText.Load(accounts.AiText.Platform, accounts.AiText.ApiUrlEncrypted, accounts.AiText.ApiKeyEncrypted, accounts.AiText.Model);
        _aiVision.Load(accounts.AiVision.Platform, accounts.AiVision.ApiUrlEncrypted, accounts.AiVision.ApiKeyEncrypted, accounts.AiVision.Model);
        _visionReuse.CheckBox.IsChecked = accounts.AiVision.ReuseFrom == AccountReuseSource.Translation;
        _textReuse.CheckBox.IsChecked = accounts.AiText.ReuseFrom == AccountReuseSource.Ocr;
        ApplyReuseModes();
        _loading = false;
        if (_tencentOcr.HasUnreadableSecrets || _tencentTranslation.HasUnreadableSecrets || _aiText.HasUnreadableSecrets || _aiVision.HasUnreadableSecrets)
            SettingsLayout.WarnUnreadableSecrets();
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        foreach (var cloud in new[] { _tencentOcr, _tencentTranslation })
        {
            autoSave.TrackDebounced(cloud.SecretId);
            autoSave.TrackDebounced(cloud.SecretKey);
            autoSave.TrackImmediate(cloud.Reuse.CheckBox);
        }
        foreach (var section in new[] { _aiText, _aiVision })
        {
            autoSave.TrackImmediate(section.Platform);
            autoSave.TrackDebounced(section.ApiUrl);
            autoSave.TrackDebounced(section.ApiKey);
            autoSave.TrackDebounced(section.Model);
        }
        autoSave.TrackImmediate(_visionReuse.CheckBox);
        autoSave.TrackImmediate(_textReuse.CheckBox);
    }

    private bool SaveSettings()
    {
        var accounts = _configManager.Get().Accounts;
        var visionReuse = _visionReuse.IsChecked ? AccountReuseSource.Translation : AccountReuseSource.None;
        var textReuse = _textReuse.IsChecked ? AccountReuseSource.Ocr : AccountReuseSource.None;
        if (_tencentOcr.IsUnchanged(accounts.TencentOcr, AccountReuseSource.Translation) &&
            _tencentTranslation.IsUnchanged(accounts.TencentTranslation, AccountReuseSource.Ocr) &&
            _aiText.IsUnchanged(accounts.AiText.Platform, accounts.AiText.Model) && _aiVision.IsUnchanged(accounts.AiVision.Platform, accounts.AiVision.Model) &&
            accounts.AiVision.ReuseFrom == visionReuse && accounts.AiText.ReuseFrom == textReuse) return false;
        var ocr = _tencentOcr.Resolve(AccountReuseSource.Translation);
        var translation = _tencentTranslation.Resolve(AccountReuseSource.Ocr);
        var (textUrl, textKey) = _aiText.ResolveSecrets();
        var (visionUrl, visionKey) = _aiVision.ResolveSecrets();
        _configManager.Update(current =>
        {
            current.Accounts.TencentOcr = ocr;
            current.Accounts.TencentTranslation = translation;
            current.Accounts.AiText.Platform = (AiPlatform)_aiText.SelectedPlatformTag;
            current.Accounts.AiText.ApiUrlEncrypted = textUrl;
            current.Accounts.AiText.ApiKeyEncrypted = textKey;
            current.Accounts.AiText.Model = _aiText.ModelName;
            current.Accounts.AiText.ReuseFrom = textReuse;
            current.Accounts.AiVision.Platform = (AiPlatform)_aiVision.SelectedPlatformTag;
            current.Accounts.AiVision.ApiUrlEncrypted = visionUrl;
            current.Accounts.AiVision.ApiKeyEncrypted = visionKey;
            current.Accounts.AiVision.Model = _aiVision.ModelName;
            current.Accounts.AiVision.ReuseFrom = visionReuse;
        });
        _tencentOcr.MarkSaved(ocr);
        _tencentTranslation.MarkSaved(translation);
        _aiText.MarkSaved(textUrl, textKey);
        _aiVision.MarkSaved(visionUrl, visionKey);
        return true;
    }

    private sealed class ReuseOffer
    {
        public StackPanel Panel { get; } = new() { Margin = new Thickness(14, 0, 14, 0) };
        public CheckBox CheckBox { get; }
        public bool IsChecked => CheckBox.IsChecked == true;
        private readonly WrapPanel _source;
        private readonly TextBlock _sourceText;
        private readonly TextBlock _warning;
        private readonly string _sourceTask;
        public Action? OpenSource { get; set; }

        public ReuseOffer(string label, string sourceTask, Action? openSource = null)
        {
            OpenSource = openSource;
            _sourceTask = sourceTask;
            Panel.Children.Add(CreateAccountDivider());
            var content = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
            Panel.Children.Add(content);
            CheckBox = new CheckBox
            {
                Content = label, MinHeight = 24,
                Style = (Style)Application.Current.FindResource("SettingsReuseCheckBox"),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            System.Windows.Automation.AutomationProperties.SetName(CheckBox, label);
            content.Children.Add(CheckBox);
            _source = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 3, 0, 0) };
            _sourceText = SettingsLayout.CreateHint(string.Empty);
            _sourceText.Margin = new Thickness(0);
            _sourceText.VerticalAlignment = VerticalAlignment.Center;
            _sourceText.SetBinding(TextBlock.MaxWidthProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.ActualWidth)) { Source = _source });
            _source.Children.Add(_sourceText);
            var button = SettingsLayout.CreateSecondaryActionButton("查看来源");
            button.Margin = new Thickness(8, 0, 0, 0);
            button.FontSize = SettingsLayout.HintFontSize;
            button.Height = 22.5;
            button.Click += (_, _) => OpenSource?.Invoke();
            _source.Children.Add(button);
            content.Children.Add(_source);
            _warning = SettingsLayout.CreateHint("来源连接尚未填写完整，请检查来源。");
            _warning.Margin = new Thickness(22, 3, 0, 0);
            _warning.LineHeight = 17;
            _warning.SetResourceReference(TextBlock.ForegroundProperty, "SettingsErrorBrush");
            System.Windows.Automation.AutomationProperties.SetLiveSetting(_warning, System.Windows.Automation.AutomationLiveSetting.Polite);
            content.Children.Add(_warning);
        }
        public void Update(bool available, bool sourceConfigured, string provider, string? address = null)
        {
            Panel.Visibility = IsChecked || available ? Visibility.Visible : Visibility.Collapsed;
            _source.Visibility = IsChecked ? Visibility.Visible : Visibility.Collapsed;
            _sourceText.Text = $"来自{_sourceTask} · {provider}" + (address == null ? string.Empty : $" · {address}");
            _warning.Visibility = IsChecked && !sourceConfigured ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private sealed class CloudAccountEditor
    {
        public Expander Expander { get; }
        public TextBox SecretId { get; } = SettingsLayout.CreateTextBox();
        public SecurePasswordField SecretKey { get; } = SettingsLayout.CreatePasswordField();
        public ReuseOffer Reuse { get; }
        private readonly StackPanel _fields = new();
        private readonly System.Windows.Controls.ComboBox _provider;
        private readonly TextBlock _testStatus;
        private readonly Func<string, string, CancellationToken, Task<(bool Success, string Message)>> _test;
        private readonly Func<string?, EncryptedSetting> _secretSetting;
        private EncryptedSetting _id = new(null);
        private EncryptedSetting _key = new(null);
        private Func<(string Id, string Key)>? _sharedCredentials;
        private long _generation;
        private CancellationTokenSource? _cancellation;
        private Action? _restoreBusyState;
        public bool TestSucceeded { get; private set; }
        public bool HasUnreadableSecrets => _id.IsUnreadable || _key.IsUnreadable;
        public event EventHandler? ConfigurationChanged;
        public event EventHandler? TestStateChanged;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Credentials().Id) && !string.IsNullOrWhiteSpace(Credentials().Key);
        private (string Id, string Key) Credentials() => _sharedCredentials?.Invoke() ?? (SecretId.Text.Trim(), SecretKey.Password.Trim());

        public CloudAccountEditor(string title, string reuseLabel, string sourceName, string testLabel,
            Func<string, string, CancellationToken, Task<(bool Success, string Message)>> test, Func<string?, EncryptedSetting> secretSetting)
        {
            _test = test;
            _secretSetting = secretSetting;
            var section = CreateAccountEditor(title);
            Expander = section.expander;
            _provider = SettingsLayout.CreateComboBox();
            _provider.Items.Add(new ComboBoxItem { Content = "腾讯云", Tag = "tencent" });
            _provider.SelectedIndex = 0;
            _provider.HorizontalAlignment = HorizontalAlignment.Right;
            section.content.Children.Add(SettingsLayout.CreateAccountField("服务商", _provider));
            Reuse = new ReuseOffer(reuseLabel, sourceName);
            section.content.Children.Add(Reuse.Panel);
            _fields.Children.Add(CreateDetailField("Secret ID", SecretId));
            _fields.Children.Add(CreateDetailField("Secret Key", SecretKey));
            SecretKey.Placeholder = $"输入 {title} Secret Key";
            section.content.Children.Add(_fields);
            var button = SettingsLayout.CreateSecondaryActionButton(testLabel);
            button.Click += Test_Click;
            _testStatus = SettingsLayout.CreateHint("未测试");
            _testStatus.Margin = new Thickness(0, 0, 12, 0);
            _testStatus.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetLiveSetting(_testStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.Children.Add(_testStatus);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            button.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, 1);
            actions.Children.Add(button);
            section.content.Children.Add(new Border
            {
                Margin = new Thickness(14, 0, 14, 12), MinHeight = 40, Padding = new Thickness(0, 10, 0, 0),
                BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsRowLineBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0), SnapsToDevicePixels = true, Child = actions
            });
            SecretId.TextChanged += (_, _) => InvalidateTest();
            SecretKey.PasswordChanged += (_, _) => InvalidateTest();
            section.content.Unloaded += (_, _) => CancelHiddenRequest();
            section.content.IsVisibleChanged += (_, e) =>
            {
                if (!(bool)e.NewValue) CancelHiddenRequest();
            };
        }
        private void Cancel()
        {
            _generation++;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            RestoreBusyState();
        }
        private void CancelHiddenRequest()
        {
            var wasBusy = _restoreBusyState != null;
            Cancel();
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
        public void InvalidateTest()
        {
            Cancel(); TestSucceeded = false;
            _testStatus.Text = "未测试";
            _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        public void UseSharedCredentials(Func<(string Id, string Key)>? shared)
        {
            _sharedCredentials = shared;
            _fields.Visibility = shared == null ? Visibility.Visible : Visibility.Collapsed;
            _provider.IsEnabled = shared == null;
            InvalidateTest();
        }
        public void Load(TencentAccountConfig saved, AccountReuseSource source)
        {
            _id = _secretSetting(saved.SecretIdEncrypted); _key = _secretSetting(saved.SecretKeyEncrypted);
            SecretId.Text = _id.Plain; SecretKey.Password = _key.Plain;
            Reuse.CheckBox.IsChecked = saved.ReuseFrom == source;
        }
        public TencentAccountConfig Draft(AccountReuseSource source) => new()
        {
            SecretIdEncrypted = SecretId.Text.Trim(), SecretKeyEncrypted = SecretKey.Password.Trim(),
            ReuseFrom = Reuse.IsChecked ? source : AccountReuseSource.None
        };
        public TencentAccountConfig Resolve(AccountReuseSource source) => new()
        {
            SecretIdEncrypted = _id.Resolve(SecretId.Text), SecretKeyEncrypted = _key.Resolve(SecretKey.Password),
            ReuseFrom = Reuse.IsChecked ? source : AccountReuseSource.None
        };
        public bool IsUnchanged(TencentAccountConfig saved, AccountReuseSource source) =>
            _id.Matches(SecretId.Text) && _key.Matches(SecretKey.Password) &&
            saved.ReuseFrom == (Reuse.IsChecked ? source : AccountReuseSource.None);
        public void MarkSaved(TencentAccountConfig saved)
        {
            _id.MarkSaved(SecretId.Text, saved.SecretIdEncrypted); _key.MarkSaved(SecretKey.Password, saved.SecretKeyEncrypted);
        }
        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            Cancel(); _cancellation = new CancellationTokenSource();
            TestSucceeded = false;
            TestStateChanged?.Invoke(this, EventArgs.Empty);
            var token = _cancellation.Token;
            var credentials = Credentials(); var generation = _generation;
            bool IsCurrent() => generation == _generation && credentials == Credentials();
            _testStatus.Text = "正在测试…";
            await RunWithBusyStateAsync(button, "测试中…", async () =>
            {
                try
                {
                    var (success, message) = await _test(credentials.Id, credentials.Key, token).WaitAsync(token);
                    if (!IsCurrent()) return;
                    TestSucceeded = success;
                    _testStatus.Text = success ? "测试通过" : $"测试失败：{message}";
                    _testStatus.SetResourceReference(TextBlock.ForegroundProperty, success ? "SettingsSuccessBrush" : "SettingsErrorBrush");
                    TestStateChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (!IsCurrent()) return;
                    _testStatus.Text = $"测试失败：{NetworkErrorMessages.FromException(ex)}";
                    _testStatus.SetResourceReference(TextBlock.ForegroundProperty, "SettingsErrorBrush");
                }
            });
        }
    }
}
