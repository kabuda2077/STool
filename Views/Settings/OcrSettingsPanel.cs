using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;

namespace STool.Views.Settings;

/// <summary>截图工具栏的 OCR 引擎、失败策略及本地语言。</summary>
public class OcrSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly Action<ServiceAccountTarget> _openAccounts;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbFallbackPolicy = null!;
    private System.Windows.Controls.ComboBox _cmbLocalLanguage = null!;
    private TextBlock _accountNote = null!;
    private TextBlock _languageHint = null!;
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
        var baseSection = SettingsLayout.CreateSectionContent("截图文字识别");
        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "Windows 本地 OCR", Tag = OcrProvider.WindowsLocal });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云 OCR", Tag = OcrProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 视觉模型", Tag = OcrProvider.AI });
        _cmbProvider.ToolTip = "用于截图工具栏的「OCR 识别」。截图翻译使用独立的识别设置。";
        baseSection.Children.Add(SettingsLayout.CreateInlineField("识别引擎", _cmbProvider, isLast: true));

        _accountNote = SettingsLayout.CreateSectionNote(string.Empty, "配置账号", () =>
            _openAccounts(SelectedProvider == OcrProvider.Tencent ? ServiceAccountTarget.Tencent : ServiceAccountTarget.AiVision));
        _accountNote.Margin = new Thickness(24, 0, 24, 12);
        baseSection.Children.Add(_accountNote);

        _cmbFallbackPolicy = SettingsLayout.CreateComboBox();
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "改用 Windows 本地 OCR", Tag = true });
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "停止并提示错误", Tag = false });
        _fallbackRow = SettingsLayout.CreateInlineField("识别失败时", _cmbFallbackPolicy, isLast: true);
        baseSection.Children.Add(_fallbackRow);
        Children.Add(SettingsLayout.CreateSection(baseSection));

        var localContent = SettingsLayout.CreateSectionContent("Windows 本地 OCR");
        _cmbLocalLanguage = SettingsLayout.CreateComboBox();
        _cmbLocalLanguage.Items.Add(new ComboBoxItem { Content = "自动", Tag = "auto" });
        foreach (var (tag, displayName) in WindowsOcrService.GetAvailableLanguages())
        {
            _installedLanguages.Add(tag);
            _cmbLocalLanguage.Items.Add(new ComboBoxItem { Content = displayName, Tag = tag });
        }
        _languageHint = SettingsLayout.CreateHint("其他语言可在 Windows 设置 → 语言中安装。");
        localContent.Children.Add(SettingsLayout.CreateInlineFieldWithHint("识别语言", _cmbLocalLanguage, _languageHint, isLast: true));
        _localSection = SettingsLayout.CreateSection(localContent);
        Children.Add(_localSection);

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
            OcrProvider.Tencent => accounts.Tencent.IsConfigured,
            OcrProvider.AI => accounts.ResolveVision().IsConfigured,
            _ => true
        };
        _accountNote.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        if (_accountNote.Inlines.FirstInline is System.Windows.Documents.Run text)
            text.Text = provider == OcrProvider.Tencent ? "腾讯云账号尚未配置。" : "AI 视觉模型尚未配置。";
        _fallbackRow.Visibility = provider == OcrProvider.WindowsLocal ? Visibility.Collapsed : Visibility.Visible;
        _localSection.Visibility = provider == OcrProvider.WindowsLocal || (_cmbFallbackPolicy.SelectedItem as ComboBoxItem)?.Tag is true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLanguageHint()
    {
        var unavailable = _cmbLocalLanguage.SelectedItem is ComboBoxItem { Tag: string language }
            && !_installedLanguages.Contains(language);
        _languageHint.Text = unavailable
            ? "此语言未安装，请在 Windows 设置 → 语言中安装，或选择其他语言。"
            : "其他语言可在 Windows 设置 → 语言中安装。";
        _languageHint.Foreground = (System.Windows.Media.Brush)FindResource(unavailable ? "ErrorBrush" : "TextSecondaryBrush");
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
            current.Ocr.Provider = provider;
            current.Ocr.FallbackToLocal = fallback;
            current.Ocr.LocalLanguage = language;
        });
        return true;
    }
}
