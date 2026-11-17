using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using STool.Core;
using STool.Models;

namespace STool.Views.Settings;

/// <summary>
/// 翻译设置：翻译引擎、翻译策略，以及截图翻译的识别引擎和模式。账号在「服务账号」页配置。
/// </summary>
public class TranslationSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly Action<ServiceAccountTarget> _openAccounts;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbTranslationMode = null!;
    private System.Windows.Controls.ComboBox _cmbScreenshotOcr = null!;
    private System.Windows.Controls.ComboBox _cmbScreenshotMode = null!;
    private TextBlock _providerNote = null!;
    private TextBlock _screenshotOcrNote = null!;
    private TextBlock _smartAccountNote = null!;
    private TextBlock _modeHint = null!;
    private TextBlock _ocrHint = null!;

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
        Margin = new Thickness(0);

        // ── 翻译 ──
        var providerSection = SettingsLayout.CreateSectionContent("文字翻译");
        _providerNote = SettingsLayout.CreateSectionNote(string.Empty, "配置账号", () =>
            _openAccounts((_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider.Tencent
                ? ServiceAccountTarget.Tencent : ServiceAccountTarget.AiText));

        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "谷歌翻译", Tag = TranslationProvider.Google });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云翻译", Tag = TranslationProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 翻译", Tag = TranslationProvider.OpenAI });
        _cmbProvider.ToolTip = "用于翻译面板，以及截图翻译的快速模式。";
        providerSection.Children.Add(SettingsLayout.CreateInlineField("翻译引擎", _cmbProvider));
        providerSection.Children.Add(_providerNote);

        _cmbTranslationMode = SettingsLayout.CreateComboBox();
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("中文", "英文", "zh-en", bidirectional: true));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "中文", "auto-zh"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "英文", "auto-en"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "日文", "auto-ja"));
        _cmbTranslationMode.Items.Add(CreateLanguageModeItem("自动", "韩文", "auto-ko"));
        providerSection.Children.Add(SettingsLayout.CreateInlineField("语言", _cmbTranslationMode, isLast: true));
        Children.Add(SettingsLayout.CreateSection(providerSection));

        // ── 截图翻译 ──
        var screenshotSection = SettingsLayout.CreateSectionContent("截图翻译");
        _screenshotOcrNote = SettingsLayout.CreateSectionNote("腾讯云账号尚未配置。", "配置腾讯云", () => _openAccounts(ServiceAccountTarget.Tencent));
        _smartAccountNote = SettingsLayout.CreateSectionNote("AI 文本模型尚未配置。", "配置 AI 文本模型", () => _openAccounts(ServiceAccountTarget.AiText));

        _cmbScreenshotOcr = SettingsLayout.CreateComboBox();
        _cmbScreenshotOcr.Items.Add(new ComboBoxItem { Content = "Windows 本地 OCR", Tag = ScreenshotOcrEngine.WindowsLocal });
        _cmbScreenshotOcr.Items.Add(new ComboBoxItem { Content = "腾讯云 OCR", Tag = ScreenshotOcrEngine.Tencent });
        _cmbScreenshotOcr.ToolTip = "截图翻译需要文字位置，使用独立于 OCR 页的识别引擎。";
        _ocrHint = SettingsLayout.CreateHint(string.Empty);
        screenshotSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("识别引擎", _cmbScreenshotOcr, _ocrHint));
        screenshotSection.Children.Add(_screenshotOcrNote);

        _cmbScreenshotMode = SettingsLayout.CreateComboBox();
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "快速", Tag = ScreenshotTranslationMode.Fast });
        _cmbScreenshotMode.Items.Add(new ComboBoxItem { Content = "智能", Tag = ScreenshotTranslationMode.Smart });
        _modeHint = SettingsLayout.CreateHint(string.Empty);
        screenshotSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("模式", _cmbScreenshotMode, _modeHint, isLast: true));
        screenshotSection.Children.Add(_smartAccountNote);
        Children.Add(SettingsLayout.CreateSection(screenshotSection));
        foreach (var note in new[] { _providerNote, _screenshotOcrNote, _smartAccountNote })
            note.Margin = new Thickness(24, 0, 24, 12);
        _cmbProvider.SelectionChanged += (_, _) => UpdateNotes();
        _cmbScreenshotOcr.SelectionChanged += (_, _) => UpdateNotes();
        _cmbScreenshotMode.SelectionChanged += (_, _) => UpdateNotes();
    }

    private ComboBoxItem CreateLanguageModeItem(string source, string target, string tag, bool bidirectional = false)
    {
        var textBrush = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        var iconBrush = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        var transparentBrush = (System.Windows.Media.Brush)FindResource("TransparentBrush");

        var content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = source,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new Viewbox
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(7, 0, 7, 0),
            Opacity = 0.78,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data = (Geometry)FindResource(bidirectional ? "IconArrowLeftRight" : "IconArrowRight"),
                        Stroke = iconBrush,
                        StrokeThickness = 1.5,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Fill = transparentBrush
                    }
                }
            }
        });
        content.Children.Add(new TextBlock
        {
            Text = target,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var accessibleName = bidirectional
            ? $"{source}和{target}互译"
            : $"自动识别，译为{target}";
        var item = new ComboBoxItem
        {
            Content = content,
            Tag = tag
        };
        System.Windows.Automation.AutomationProperties.SetName(item, accessibleName);
        TextSearch.SetText(item, accessibleName);
        return item;
    }

    private void UpdateNotes()
    {
        var accounts = _configManager.Get().Accounts;
        var provider = (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is TranslationProvider p ? p : TranslationProvider.Google;
        var providerConfigured = provider switch
        {
            TranslationProvider.Tencent => accounts.Tencent.IsConfigured,
            TranslationProvider.OpenAI => accounts.AiText.IsConfigured,
            _ => true
        };
        _providerNote.Visibility = providerConfigured ? Visibility.Collapsed : Visibility.Visible;

        if (_providerNote.Inlines.FirstInline is System.Windows.Documents.Run run)
            run.Text = provider == TranslationProvider.Tencent ? "腾讯云账号尚未配置。" : "AI 文本模型尚未配置。";
        var cloudOcr = _cmbScreenshotOcr.SelectedItem is ComboBoxItem { Tag: ScreenshotOcrEngine.Tencent };
        var smart = _cmbScreenshotMode.SelectedItem is ComboBoxItem { Tag: ScreenshotTranslationMode.Smart };
        _screenshotOcrNote.Visibility = cloudOcr && !accounts.Tencent.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        _smartAccountNote.Visibility = smart && !accounts.AiText.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        _ocrHint.Text = cloudOcr ? "识别失败时自动改用 Windows 本地 OCR。" : string.Empty;
        _ocrHint.Visibility = cloudOcr ? Visibility.Visible : Visibility.Collapsed;
        var providerName = (_cmbProvider.SelectedItem as ComboBoxItem)?.Content as string ?? "所选翻译引擎";
        _modeHint.Text = smart
            ? "使用 AI 文本模型选段并翻译；失败时改用快速模式。"
            : $"自动选段，使用{providerName}翻译。";
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Translation;
        SelectByTag(_cmbProvider, config.Provider);
        SelectByTag(_cmbTranslationMode, config.TranslationMode);
        SelectByTag(_cmbScreenshotOcr, config.ScreenshotOcr);
        SelectByTag(_cmbScreenshotMode, config.ScreenshotMode);
        UpdateNotes();
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox comboBox, object value)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (Equals(item.Tag, value))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        autoSave.TrackImmediate(_cmbProvider);
        autoSave.TrackImmediate(_cmbTranslationMode);
        autoSave.TrackImmediate(_cmbScreenshotOcr);
        autoSave.TrackImmediate(_cmbScreenshotMode);
        Children.Add(new Border { Height = 12 });
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get().Translation;
        var provider = (TranslationProvider)((ComboBoxItem)_cmbProvider.SelectedItem).Tag;
        var translationMode = (string)((ComboBoxItem)_cmbTranslationMode.SelectedItem).Tag;
        var screenshotOcr = (ScreenshotOcrEngine)((ComboBoxItem)_cmbScreenshotOcr.SelectedItem).Tag;
        var screenshotMode = (ScreenshotTranslationMode)((ComboBoxItem)_cmbScreenshotMode.SelectedItem).Tag;
        var targetLanguage = Modules.Translation.TranslationManager.ResolveTargetLanguage(string.Empty, translationMode);

        if (config.Provider == provider &&
            config.TranslationMode == translationMode &&
            config.ScreenshotOcr == screenshotOcr &&
            config.ScreenshotMode == screenshotMode)
        {
            return false;
        }

        _configManager.Update(current =>
        {
            current.Translation.Provider = provider;
            current.Translation.TranslationMode = translationMode;
            current.Translation.ScreenshotOcr = screenshotOcr;
            current.Translation.ScreenshotMode = screenshotMode;
            current.Translation.SourceLanguage = "auto";
            current.Translation.TargetLanguage = targetLanguage;
        });
        return true;
    }
}
