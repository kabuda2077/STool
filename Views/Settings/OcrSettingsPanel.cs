using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;

namespace STool.Views.Settings;

/// <summary>文字识别服务、失败策略及 Windows 本地语言。</summary>
public class OcrSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly Action<ServiceAccountTarget> _openAccounts;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbFallbackPolicy = null!;
    private System.Windows.Controls.ComboBox _cmbLocalLanguage = null!;
    private WrapPanel _accountNote = null!;
    private TextBlock _languageHint = null!;
    private System.Windows.Controls.Button _modelButton = null!;
    private FrameworkElement _modelRow = null!;
    private FrameworkElement _fallbackRow = null!;
    private FrameworkElement _localSection = null!;
    private readonly System.Collections.Generic.HashSet<string> _installedLanguages = new() { "auto" };

    public OcrSettingsPanel(ConfigManager configManager, Action<ServiceAccountTarget> openAccounts)
    {
        _configManager = configManager;
        _openAccounts = openAccounts;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
    }

    private void InitializeUI()
    {
        var baseSection = SettingsLayout.CreateSectionContent("提取图片文字");
        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "Windows 本地", Tag = OcrProvider.WindowsLocal });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云", Tag = OcrProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 文字识别", Tag = OcrProvider.AI });
        _cmbProvider.HorizontalAlignment = HorizontalAlignment.Right;
        baseSection.Children.Add(SettingsLayout.CreateInlineField("识别服务", _cmbProvider));
        _modelButton = SettingsLayout.CreateFieldButton("选择模型", () => _openAccounts(ServiceAccountTarget.AiVision));
        System.Windows.Automation.AutomationProperties.SetName(_modelButton, "配置识别模型");
        _modelRow = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            SnapsToDevicePixels = true,
            Child = SettingsLayout.CreateInlineField("识别模型", _modelButton)
        };
        baseSection.Children.Add(_modelRow);

        _accountNote = SettingsLayout.CreateSectionNote("AI 文字识别尚未填写完整。", "去配置", () =>
            _openAccounts(SelectedProvider == OcrProvider.Tencent ? ServiceAccountTarget.TencentOcr : ServiceAccountTarget.AiVision));
        baseSection.Children.Add(_accountNote);
        Children.Add(SettingsLayout.CreateSection(baseSection));

        var moreSettings = SettingsLayout.CreateCompactCollapsibleSection("更多识别设置");
        _cmbFallbackPolicy = SettingsLayout.CreateComboBox();
        _cmbFallbackPolicy.HorizontalAlignment = HorizontalAlignment.Right;
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "改用 Windows 本地", Tag = true });
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "停止并提示错误", Tag = false });
        _fallbackRow = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            SnapsToDevicePixels = true,
            Child = SettingsLayout.CreateInlineField("识别失败时", _cmbFallbackPolicy)
        };
        moreSettings.content.Children.Add(_fallbackRow);

        _cmbLocalLanguage = SettingsLayout.CreateComboBox();
        _cmbLocalLanguage.HorizontalAlignment = HorizontalAlignment.Right;
        _cmbLocalLanguage.Items.Add(new ComboBoxItem { Content = "自动", Tag = "auto" });
        foreach (var (tag, displayName) in WindowsOcrService.GetAvailableLanguages())
        {
            _installedLanguages.Add(tag);
            _cmbLocalLanguage.Items.Add(new ComboBoxItem { Content = displayName, Tag = tag });
        }
        _languageHint = SettingsLayout.CreateHint(string.Empty);
        _languageHint.Visibility = Visibility.Collapsed;
        _localSection = SettingsLayout.CreateInlineFieldWithHint("Windows 识别语言", _cmbLocalLanguage, _languageHint);
        moreSettings.content.Children.Add(_localSection);
        Children.Add(moreSettings.section);

        _cmbProvider.SelectionChanged += (_, _) => UpdateState();
        _cmbFallbackPolicy.SelectionChanged += (_, _) => UpdateState();
        _cmbLocalLanguage.SelectionChanged += (_, _) => UpdateLanguageHint();
    }

    private OcrProvider SelectedProvider => (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is OcrProvider provider
        ? provider : OcrProvider.WindowsLocal;

    private void UpdateState()
    {
        var accounts = _configManager.Get().Accounts;
        var provider = SelectedProvider;
        var configured = provider switch
        {
            OcrProvider.Tencent => accounts.ResolveTencentOcr().IsConfigured,
            OcrProvider.AI => accounts.ResolveVision().IsConfigured,
            _ => true
        };
        _accountNote.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        SettingsLayout.SetSectionNoteText(_accountNote,
            provider == OcrProvider.Tencent ? "腾讯云 OCR尚未填写完整。" : "AI 文字识别尚未填写完整。");
        _fallbackRow.Visibility = provider == OcrProvider.WindowsLocal ? Visibility.Collapsed : Visibility.Visible;
        _localSection.Visibility = Visibility.Visible;
        _modelRow.Visibility = provider == OcrProvider.AI ? Visibility.Visible : Visibility.Collapsed;
        var model = accounts.AiVision.Model ?? string.Empty;
        SettingsLayout.SetFieldButtonLabel(_modelButton, model.Length == 0 ? "选择模型" : AiServiceSettingsSection.DisplayModelName(model));
        SettingsLayout.SetTextHelp((TextBlock)_modelButton.Tag, model, _modelButton);
        SettingsLayout.SetFieldHelp(_cmbProvider, provider switch
        {
            OcrProvider.Tencent => "截图翻译也会发送选区图片；失败后改用 Windows 本地。",
            OcrProvider.AI => "截图翻译仍使用 Windows 本地识别。",
            _ => string.Empty
        });
    }

    private void UpdateLanguageHint()
    {
        var unavailable = _cmbLocalLanguage.SelectedItem is ComboBoxItem { Tag: string language }
            && !_installedLanguages.Contains(language);
        _languageHint.Text = unavailable
            ? "此语言未安装，请在 Windows 设置 → 语言中安装，或选择其他语言。"
            : string.Empty;
        _languageHint.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        _languageHint.Foreground = (System.Windows.Media.Brush)FindResource("SettingsErrorBrush");
    }

    // Preserve a saved language missing on this computer; changing another setting must not replace it.
    internal static void SelectLanguage(System.Windows.Controls.ComboBox comboBox, string language)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (Equals(item.Tag, language))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
        var missing = new ComboBoxItem
        {
            Content = $"{language}（未安装）", Tag = language,
            ToolTip = "安装对应的 Windows OCR 语言包后可用"
        };
        comboBox.Items.Add(missing);
        comboBox.SelectedItem = missing;
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Ocr;
        foreach (ComboBoxItem item in _cmbProvider.Items)
            if (Equals(item.Tag, config.Provider)) _cmbProvider.SelectedItem = item;
        foreach (ComboBoxItem item in _cmbFallbackPolicy.Items)
            if (Equals(item.Tag, config.FallbackToLocal)) _cmbFallbackPolicy.SelectedItem = item;
        SelectLanguage(_cmbLocalLanguage, config.LocalLanguage);
        UpdateState();
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        autoSave.TrackImmediate(_cmbProvider);
        autoSave.TrackImmediate(_cmbFallbackPolicy);
        autoSave.TrackImmediate(_cmbLocalLanguage);
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get().Ocr;
        var provider = SelectedProvider;
        var fallback = (_cmbFallbackPolicy.SelectedItem as ComboBoxItem)?.Tag is true;
        var language = (string)((ComboBoxItem)_cmbLocalLanguage.SelectedItem).Tag;
        if (config.Provider == provider && config.FallbackToLocal == fallback && config.LocalLanguage == language)
            return false;
        _configManager.Update(current =>
        {
            if (current.Ocr.Provider != provider)
                current.Translation.ScreenshotOcrMigrationPending = false;
            current.Ocr.Provider = provider;
            current.Ocr.FallbackToLocal = fallback;
            current.Ocr.LocalLanguage = language;
        });
        return true;
    }
}
