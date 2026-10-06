using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Modules.Clipboard;

namespace STool.Views.Settings;

/// <summary>剪贴板历史设置：记录开关、保留策略与隐私排除。</summary>
public class ClipboardSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly IAppShell _shell;
    private System.Windows.Controls.CheckBox _chkEnabled = null!;
    private System.Windows.Controls.ComboBox _cmbRetention = null!;
    private System.Windows.Controls.ComboBox _cmbMaxEntries = null!;
    private System.Windows.Controls.ComboBox _cmbMaxImageSize = null!;
    private System.Windows.Controls.TextBox _txtExcludedApps = null!;

    public ClipboardSettingsPanel(ConfigManager configManager, IAppShell shell)
    {
        _configManager = configManager;
        _shell = shell;
        InitializeUI();
        LoadSettings();
        EnableAutoSave();
    }

    private void InitializeUI()
    {
        Margin = new Thickness(0);

        // ── 记录 ──
        var recordSection = SettingsLayout.CreateSectionContent("记录");
        _chkEnabled = SettingsLayout.CreateSwitch();
        recordSection.Children.Add(SettingsLayout.CreateSwitchRow(
            "记录剪贴板历史",
            "关闭后不再记录新内容，已有记录保留",
            _chkEnabled,
            SaveEnabled,
            isLast: true));
        Children.Add(SettingsLayout.CreateSection(recordSection));

        // ── 保留策略 ──
        var retentionSection = SettingsLayout.CreateSectionContent("保留策略");

        _cmbRetention = SettingsLayout.CreateComboBox();
        AddOption(_cmbRetention, "7 天", 7);
        AddOption(_cmbRetention, "30 天", 30);
        AddOption(_cmbRetention, "90 天", 90);
        AddOption(_cmbRetention, "永久保留", 0);
        retentionSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("保留时间", _cmbRetention, "收藏的记录不会被自动清理。"));

        _cmbMaxEntries = SettingsLayout.CreateComboBox();
        foreach (var count in new[] { 500, 1000, 3000, 5000 })
            AddOption(_cmbMaxEntries, $"{count} 条", count);
        retentionSection.Children.Add(SettingsLayout.CreateInlineField("最多保留", _cmbMaxEntries));

        _cmbMaxImageSize = SettingsLayout.CreateComboBox();
        foreach (var megabytes in new[] { 2, 5, 10, 20 })
            AddOption(_cmbMaxImageSize, $"{megabytes} MB", megabytes * 1024);
        retentionSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("图片上限", _cmbMaxImageSize, "超过大小的图片不会记录。", isLast: true));
        Children.Add(SettingsLayout.CreateSection(retentionSection));

        // ── 隐私 ──
        var privacySection = SettingsLayout.CreateSectionContent("隐私");
        _txtExcludedApps = SettingsLayout.CreateTextBox();
        privacySection.Children.Add(SettingsLayout.CreateInlineFieldWithHint(
            "不记录的应用",
            _txtExcludedApps,
            "进程名，用逗号分隔，如 KeePass.exe。密码管理器标记为隐私的内容始终不会记录。",
            isLast: true));
        Children.Add(SettingsLayout.CreateSection(privacySection));
    }

    private static void AddOption(System.Windows.Controls.ComboBox comboBox, string label, int value)
    {
        comboBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    }

    /// <summary>选中与配置值相同的选项；配置文件里手工写入的其他值作为"自定义"选项保留，不会被自动保存覆盖。</summary>
    private static void SelectOrAddValue(System.Windows.Controls.ComboBox comboBox, int value, Func<int, string> describe)
    {
        var item = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(option => option.Tag is int tag && tag == value);
        if (item == null)
        {
            item = new ComboBoxItem { Content = $"{describe(value)}（自定义）", Tag = value };
            comboBox.Items.Add(item);
        }

        comboBox.SelectedItem = item;
    }

    private void LoadSettings()
    {
        var config = _configManager.Get().Clipboard;
        _chkEnabled.IsChecked = config.Enabled;
        SelectOrAddValue(_cmbRetention, config.RetentionDays, days => days <= 0 ? "永久保留" : $"{days} 天");
        SelectOrAddValue(_cmbMaxEntries, config.MaxEntries, count => $"{count} 条");
        SelectOrAddValue(_cmbMaxImageSize, config.MaxImageSizeKB, kilobytes => $"{kilobytes / 1024d:0.#} MB");
        _txtExcludedApps.Text = string.Join(", ", config.ExcludedApps);
    }

    private void EnableAutoSave()
    {
        var autoSave = new SettingsAutoSaveController(this, SaveSettings);
        autoSave.TrackImmediate(_cmbRetention);
        autoSave.TrackImmediate(_cmbMaxEntries);
        autoSave.TrackImmediate(_cmbMaxImageSize);
        autoSave.TrackDebounced(_txtExcludedApps);
        Children.Add(new Border { Height = 12 });
    }

    private void SaveEnabled(bool enabled)
    {
        try
        {
            _configManager.Update(config => config.Clipboard.Enabled = enabled);
            _shell.ApplyClipboardSettings();
            ToastNotification.ShowSettingsSaved();
        }
        catch (Exception ex)
        {
            _chkEnabled.IsChecked = !enabled;
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private bool SaveSettings()
    {
        var config = _configManager.Get().Clipboard;
        var retentionDays = SelectedValue(_cmbRetention, config.RetentionDays);
        var maxEntries = SelectedValue(_cmbMaxEntries, config.MaxEntries);
        var maxImageSize = SelectedValue(_cmbMaxImageSize, config.MaxImageSizeKB);
        var excludedApps = ClipboardPrivacy.ParseAppList(_txtExcludedApps.Text);

        if (config.RetentionDays == retentionDays &&
            config.MaxEntries == maxEntries &&
            config.MaxImageSizeKB == maxImageSize &&
            config.ExcludedApps.SequenceEqual(excludedApps, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _configManager.Update(current =>
        {
            current.Clipboard.RetentionDays = retentionDays;
            current.Clipboard.MaxEntries = maxEntries;
            current.Clipboard.MaxImageSizeKB = maxImageSize;
            current.Clipboard.ExcludedApps = excludedApps;
        });
        _shell.ApplyClipboardSettings();
        return true;
    }

    private static int SelectedValue(System.Windows.Controls.ComboBox comboBox, int fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag is int value ? value : fallback;
}
