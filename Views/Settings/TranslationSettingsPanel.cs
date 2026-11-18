using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;

namespace STool.Views.Settings;

/// <summary>默认翻译及截图翻译；两种截图翻译方式共用语言和文字识别规则。</summary>
public class TranslationSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly Action<ServiceAccountTarget> _openAccounts;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbTranslationMode = null!;
    private System.Windows.Controls.ComboBox _cmbScreenshotMode = null!;
    private WrapPanel _providerNote = null!;
    private WrapPanel _screenshotOcrNote = null!;
    private WrapPanel _smartAccountNote = null!;
    private WrapPanel _migrationNote = null!;
    private FrameworkElement _providerModelRow = null!;
    private FrameworkElement _smartModelRow = null!;
    private System.Windows.Controls.Button _providerModelButton = null!;
    private System.Windows.Controls.Button _smartModelButton = null!;
    private bool _followOcr;

    public TranslationSettingsPanel(ConfigManager configManager, Action<ServiceAccountTarget> openAccounts)
    {
        _configManager = configManager;
        _openAccounts = openAccounts;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
    }

    private void InitializeUI()
    {
        var providerSection = SettingsLayout.CreateSectionContent("默认翻译");
        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.HorizontalAlignment = HorizontalAlignment.Right;
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "谷歌翻译", Tag = TranslationProvider.Google });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译", Tag = TranslationProvider.OpenAI });
        providerSection.Children.Add(SettingsLayout.CreateInlineField("翻译服务", _cmbProvider));
        _providerModelButton = SettingsLayout.CreateFieldButton("选择模型", () => _openAccounts(ServiceAccountTarget.AiText));
        System.Windows.Automation.AutomationProperties.SetName(_providerModelButton, "配置翻译模型");
        _providerModelRow = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            SnapsToDevicePixels = true,
            Child = SettingsLayout.CreateInlineField("翻译模型", _providerModelButton)
        };
        providerSection.Children.Add(_providerModelRow);
        _providerNote = SettingsLayout.CreateSectionNote("AI 翻译尚未填写完整。", "去配置", () =>
            _openAccounts((_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider.Tencent
                ? ServiceAccountTarget.TencentTranslation : ServiceAccountTarget.AiText));
        providerSection.Children.Add(_providerNote);

        _cmbTranslationMode = SettingsLayout.CreateComboBox();
        _cmbTranslationMode.HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var (label, value) in new[]
        {
            ("中英互译", "zh-en"), ("翻译为中文", "auto-zh"), ("翻译为英文", "auto-en"),
            ("翻译为日文", "auto-ja"), ("翻译为韩文", "auto-ko")
        })
            _cmbTranslationMode.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        SettingsLayout.SetFieldHelp(_cmbTranslationMode, "默认翻译与截图翻译共用。");
        providerSection.Children.Add(new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            SnapsToDevicePixels = true,
            Child = SettingsLayout.CreateInlineField("语言", _cmbTranslationMode, minHeight: 51)
        });
        Children.Add(SettingsLayout.CreateSection(providerSection));

        var screenshotSection = SettingsLayout.CreateSectionContent("截图翻译");
        _cmbScreenshotMode = SettingsLayout.CreateComboBox();
        _cmbScreenshotMode.HorizontalAlignment = HorizontalAlignment.Right;
        System.Windows.Automation.AutomationProperties.SetName(_cmbScreenshotMode, "截图翻译方式");
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "标准", Tag = ScreenshotTranslationMode.Fast });
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "AI 模式", Tag = ScreenshotTranslationMode.Smart });
        SettingsLayout.SetFieldHelp(_cmbScreenshotMode, "标准：按规则分段，使用默认翻译服务。\nAI：由 AI 选段并翻译，失败后改用默认服务。");
        screenshotSection.Children.Add(SettingsLayout.CreateInlineField("翻译方式", _cmbScreenshotMode));
        _smartModelButton = SettingsLayout.CreateFieldButton("选择模型", () => _openAccounts(ServiceAccountTarget.AiText));
        System.Windows.Automation.AutomationProperties.SetName(_smartModelButton, "配置翻译模型");
        _smartModelRow = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            SnapsToDevicePixels = true,
            Child = SettingsLayout.CreateInlineField("翻译模型", _smartModelButton)
        };
        screenshotSection.Children.Add(_smartModelRow);
        _screenshotOcrNote = SettingsLayout.CreateSectionNote("腾讯云 OCR尚未填写完整。", "去配置", () => _openAccounts(ServiceAccountTarget.TencentOcr));
        _smartAccountNote = SettingsLayout.CreateSectionNote("AI 翻译尚未填写完整。", "去配置", () => _openAccounts(ServiceAccountTarget.AiText));
        _migrationNote = SettingsLayout.CreateSectionNote(
            "截图翻译仍使用 Windows 本地识别。跟随后会将选区图片发送到腾讯云。", "跟随文字识别", FollowOcr);
        screenshotSection.Children.Add(_screenshotOcrNote);
        screenshotSection.Children.Add(_smartAccountNote);
        screenshotSection.Children.Add(_migrationNote);
        Children.Add(SettingsLayout.CreateSection(screenshotSection));
        _cmbProvider.SelectionChanged += (_, _) => UpdateNotes();
        _cmbScreenshotMode.SelectionChanged += (_, _) => UpdateNotes();
    }

    private void FollowOcr()
    {
        _followOcr = true;
        SettingsAutoSaveController.GetController(this)!.SaveChange();
        UpdateNotes();
    }

    private void UpdateNotes()
    {
        var config = _configManager.Get();
        var accounts = config.Accounts;
        var provider = (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider p ? p : TranslationProvider.Google;
        var providerConfigured = provider switch
        {
            TranslationProvider.Tencent => accounts.ResolveTencentTranslation().IsConfigured,
            TranslationProvider.OpenAI => accounts.ResolveText().IsConfigured,
            _ => true
        };
        _providerNote.Visibility = providerConfigured ? Visibility.Collapsed : Visibility.Visible;
        SettingsLayout.SetSectionNoteText(_providerNote,
            provider == TranslationProvider.Tencent ? "腾讯云翻译尚未填写完整。" : "AI 翻译尚未填写完整。");
        var smart = _cmbScreenshotMode.SelectedItem is ComboBoxItem { Tag: ScreenshotTranslationMode.Smart };
        var pending = config.Translation.ScreenshotOcrMigrationPending;
        _providerModelRow.Visibility = provider == TranslationProvider.OpenAI ? Visibility.Visible : Visibility.Collapsed;
        _smartModelRow.Visibility = smart ? Visibility.Visible : Visibility.Collapsed;
        _screenshotOcrNote.Visibility = config.Ocr.Provider == OcrProvider.Tencent && !pending && !accounts.ResolveTencentOcr().IsConfigured
            ? Visibility.Visible : Visibility.Collapsed;
        _smartAccountNote.Visibility = smart && !accounts.ResolveText().IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        _migrationNote.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        var model = accounts.AiText.Model ?? string.Empty;
        foreach (var button in new[] { _providerModelButton, _smartModelButton })
        {
            SettingsLayout.SetFieldButtonLabel(button, model.Length == 0 ? "选择模型" : AiServiceSettingsSection.DisplayModelName(model));
            SettingsLayout.SetTextHelp((TextBlock)button.Tag, model, button);
        }
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Translation;
        SelectByTag(_cmbProvider, config.Provider);
        SelectByTag(_cmbTranslationMode, config.TranslationMode);
        SelectByTag(_cmbScreenshotMode, config.ScreenshotMode);
        UpdateNotes();
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox comboBox, object value)
    {
        foreach (ComboBoxItem item in comboBox.Items)
            if (Equals(item.Tag, value)) { comboBox.SelectedItem = item; return; }
        comboBox.SelectedIndex = 0;
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        autoSave.TrackImmediate(_cmbProvider);
        autoSave.TrackImmediate(_cmbTranslationMode);
        autoSave.TrackImmediate(_cmbScreenshotMode);
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get().Translation;
        var provider = (TranslationProvider)((ComboBoxItem)_cmbProvider.SelectedItem).Tag;
        var translationMode = (string)((ComboBoxItem)_cmbTranslationMode.SelectedItem).Tag;
        var screenshotMode = (ScreenshotTranslationMode)((ComboBoxItem)_cmbScreenshotMode.SelectedItem).Tag;
        if (config.Provider == provider && config.TranslationMode == translationMode &&
            config.ScreenshotMode == screenshotMode && (!_followOcr || !config.ScreenshotOcrMigrationPending)) return false;
        _configManager.Update(current =>
        {
            current.Translation.Provider = provider;
            current.Translation.TranslationMode = translationMode;
            current.Translation.ScreenshotMode = screenshotMode;
            current.Translation.SourceLanguage = "auto";
            current.Translation.TargetLanguage = Modules.Translation.TranslationManager.ResolveTargetLanguage(string.Empty, translationMode);
            if (_followOcr) current.Translation.ScreenshotOcrMigrationPending = false;
        });
        _followOcr = false;
        return true;
    }
}
