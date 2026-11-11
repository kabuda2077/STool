using System;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;

namespace STool.Views.Settings;

/// <summary>
/// OCR 设置：「OCR 识别」使用的引擎、失败策略和本地 OCR 语言。账号在「服务账号」页配置。
/// </summary>
public class OcrSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly Action _openAccounts;
    private System.Windows.Controls.ComboBox _cmbProvider = null!;
    private System.Windows.Controls.ComboBox _cmbFallbackPolicy = null!;
    private System.Windows.Controls.ComboBox _cmbLocalLanguage = null!;
    private TextBlock _accountNote = null!;

    public OcrSettingsPanel(ConfigManager configManager, Action openAccounts)
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

        var baseSection = SettingsLayout.CreateSectionContent("OCR 识别");
        _accountNote = SettingsLayout.CreateSectionNote("所选引擎的账号尚未配置。", "前往服务账号", _openAccounts);
        _accountNote.Visibility = Visibility.Collapsed;
        baseSection.Children.Add(_accountNote);

        _cmbProvider = SettingsLayout.CreateComboBox();
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "Windows 本地 OCR", Tag = OcrProvider.WindowsLocal });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "腾讯云 OCR", Tag = OcrProvider.Tencent });
        _cmbProvider.Items.Add(new ComboBoxItem { Content = "AI 视觉模型", Tag = OcrProvider.AI });
        _cmbProvider.SelectionChanged += (_, _) => UpdateAccountNote();
        baseSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint(
            "当前引擎",
            _cmbProvider,
            "用于截图工具栏的「OCR 识别」。截图翻译的识别引擎在翻译页设置。"));

        _cmbFallbackPolicy = SettingsLayout.CreateComboBox();
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "使用 Windows 本地 OCR", Tag = true });
        _cmbFallbackPolicy.Items.Add(new ComboBoxItem { Content = "不自动处理", Tag = false });
        baseSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint(
            "失败时",
            _cmbFallbackPolicy,
            "云服务异常时执行的策略",
            isLast: true));
        Children.Add(SettingsLayout.CreateSection(baseSection));

        var localSection = SettingsLayout.CreateSectionContent("Windows 本地 OCR");
        _cmbLocalLanguage = SettingsLayout.CreateComboBox();
        _cmbLocalLanguage.Items.Add(new ComboBoxItem { Content = "自动", Tag = "auto" });
        foreach (var (tag, displayName) in WindowsOcrService.GetAvailableLanguages())
            _cmbLocalLanguage.Items.Add(new ComboBoxItem { Content = displayName, Tag = tag });
        localSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint(
            "识别语言",
            _cmbLocalLanguage,
            "只列出系统已安装的语言。需要其他语言时，在 Windows 设置 → 语言中添加对应语言包。",
            isLast: true));
        Children.Add(SettingsLayout.CreateSection(localSection));
    }

    private void UpdateAccountNote()
    {
        var accounts = _configManager.Get().Accounts;
        var provider = (_cmbProvider.SelectedItem as ComboBoxItem)?.Tag is OcrProvider selected ? selected : OcrProvider.WindowsLocal;
        var configured = provider switch
        {
            OcrProvider.Tencent => accounts.Tencent.IsConfigured,
            OcrProvider.AI => accounts.ResolveVision().IsConfigured,
            _ => true
        };
        _accountNote.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Ocr;
        SelectByTag(_cmbProvider, config.Provider);
        SelectByTag(_cmbFallbackPolicy, config.FallbackToLocal);
        SelectByTag(_cmbLocalLanguage, config.LocalLanguage);
        UpdateAccountNote();
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
        autoSave.TrackImmediate(_cmbFallbackPolicy);
        autoSave.TrackImmediate(_cmbLocalLanguage);
        Children.Add(new Border { Height = 12 });
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get().Ocr;
        var provider = (OcrProvider)((ComboBoxItem)_cmbProvider.SelectedItem).Tag;
        var fallbackToLocal = (_cmbFallbackPolicy.SelectedItem as ComboBoxItem)?.Tag is true;
        var localLanguage = (_cmbLocalLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

        if (config.Provider == provider &&
            config.FallbackToLocal == fallbackToLocal &&
            config.LocalLanguage == localLanguage)
        {
            return false;
        }

        _configManager.Update(current =>
        {
            current.Ocr.Provider = provider;
            current.Ocr.FallbackToLocal = fallbackToLocal;
            current.Ocr.LocalLanguage = localLanguage;
        });
        return true;
    }
}
