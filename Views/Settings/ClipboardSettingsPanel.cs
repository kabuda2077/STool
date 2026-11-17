using System;
using System.Collections.Generic;
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
    private StackPanel _excludedAppsList = null!;
    private readonly List<string> _excludedApps = new();
    private SettingsAutoSaveController _autoSave = null!;

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
        retentionSection.Children.Add(SettingsLayout.CreateSectionNote("超出保留时间或数量时，优先清理最早的非收藏记录；收藏记录不受自动清理影响。"));

        _cmbRetention = SettingsLayout.CreateComboBox();
        AddOption(_cmbRetention, "7 天", 7);
        AddOption(_cmbRetention, "30 天", 30);
        AddOption(_cmbRetention, "90 天", 90);
        AddOption(_cmbRetention, "不限时间", 0);
        retentionSection.Children.Add(SettingsLayout.CreateInlineField("保留时间", _cmbRetention));

        _cmbMaxEntries = SettingsLayout.CreateComboBox();
        foreach (var count in new[] { 500, 1000, 3000, 5000 })
            AddOption(_cmbMaxEntries, $"{count} 条", count);
        retentionSection.Children.Add(SettingsLayout.CreateInlineField("最多保留", _cmbMaxEntries));

        _cmbMaxImageSize = SettingsLayout.CreateComboBox();
        foreach (var megabytes in new[] { 2, 5, 10, 20 })
            AddOption(_cmbMaxImageSize, $"{megabytes} MB", megabytes * 1024);
        retentionSection.Children.Add(SettingsLayout.CreateInlineFieldWithHint("单张图片大小", _cmbMaxImageSize, "超过大小的图片不会记录。", isLast: true));
        Children.Add(SettingsLayout.CreateSection(retentionSection));

        // ── 隐私 ──
        var privacySection = SettingsLayout.CreateSectionContent("隐私");
        _txtExcludedApps = SettingsLayout.CreateTextBox();
        _txtExcludedApps.AcceptsReturn = true; // Preserve line-separated app lists when pasted.
        _txtExcludedApps.ToolTip = "进程名，如 KeePass.exe；可粘贴用逗号、分号或换行分隔的多个应用";
        System.Windows.Automation.AutomationProperties.SetName(_txtExcludedApps, "添加不记录的应用");
        System.Windows.Automation.AutomationProperties.SetHelpText(_txtExcludedApps, (string)_txtExcludedApps.ToolTip);
        _txtExcludedApps.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                CommitExcludedAppsDraft();
            }
        };
        _txtExcludedApps.LostKeyboardFocus += (_, _) => CommitExcludedAppsDraft();

        var addButton = SettingsLayout.CreateSecondaryActionButton("添加");
        addButton.Margin = new Thickness(8, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(addButton, "添加不记录的应用");
        addButton.Click += (_, _) => CommitExcludedAppsDraft();
        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(_txtExcludedApps);
        Grid.SetColumn(addButton, 1);
        inputRow.Children.Add(addButton);

        var editor = new StackPanel();
        editor.Children.Add(inputRow);
        _excludedAppsList = new StackPanel();
        System.Windows.Automation.AutomationProperties.SetName(_excludedAppsList, "不记录的应用列表");
        editor.Children.Add(_excludedAppsList);
        privacySection.Children.Add(SettingsLayout.CreateInlineFieldWithHint(
            "不记录的应用",
            editor,
            "密码管理器标记为隐私的内容始终不会记录。",
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
        SelectOrAddValue(_cmbRetention, config.RetentionDays, days => days <= 0 ? "不限时间" : $"{days} 天");
        SelectOrAddValue(_cmbMaxEntries, config.MaxEntries, count => $"{count} 条");
        SelectOrAddValue(_cmbMaxImageSize, config.MaxImageSizeKB, kilobytes => $"{kilobytes / 1024d:0.#} MB");
        _excludedApps.AddRange(ClipboardPrivacy.ParseAppList(string.Join("\n", config.ExcludedApps)));
        foreach (var app in _excludedApps)
            AddExcludedAppRow(app);
    }

    private void EnableAutoSave()
    {
        _autoSave = new SettingsAutoSaveController(this, SaveSettings);
        _autoSave.TrackImmediate(_cmbRetention);
        _autoSave.TrackImmediate(_cmbMaxEntries);
        _autoSave.TrackImmediate(_cmbMaxImageSize);
        // Track the draft so navigation/closing flushes it before removing this page.
        _autoSave.TrackDraft(_txtExcludedApps);
        Children.Add(new Border { Height = 12 });
    }

    private void CommitExcludedAppsDraft()
    {
        if (MergeExcludedAppsDraft())
            _autoSave.SaveChange();
    }

    private bool MergeExcludedAppsDraft()
    {
        var apps = ClipboardPrivacy.ParseAppList(_txtExcludedApps.Text);
        if (apps.Count == 0)
            return false;

        var changed = false;
        foreach (var app in apps)
        {
            if (_excludedApps.Contains(app, StringComparer.OrdinalIgnoreCase))
                continue;

            _excludedApps.Add(app);
            AddExcludedAppRow(app);
            changed = true;
        }

        // A focus change to Add may commit before Click; clearing makes Click idempotent.
        _txtExcludedApps.Clear();
        return changed;
    }

    private void AddExcludedAppRow(string app)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = app,
            FontSize = SettingsLayout.BodyFontSize,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = app,
            Margin = new Thickness(SettingsLayout.FormTextInset, 0, 8, 0)
        });
        var removeButton = SettingsLayout.CreateSecondaryActionButton("移除");
        System.Windows.Automation.AutomationProperties.SetName(removeButton, $"移除 {app}");
        removeButton.Click += (_, _) =>
        {
            _excludedApps.Remove(app);
            _excludedAppsList.Children.Remove(row);
            _autoSave.SaveChange();
        };
        Grid.SetColumn(removeButton, 1);
        row.Children.Add(removeButton);
        // Append rows so committing a draft on focus loss keeps existing Remove buttons alive.
        _excludedAppsList.Children.Add(row);
    }

    private void SaveEnabled(bool enabled)
    {
        try
        {
            _configManager.Update(config => config.Clipboard.Enabled = enabled);
            _shell.ApplyClipboardSettings();
            SettingsFeedback.Report(this);
        }
        catch (Exception ex)
        {
            _chkEnabled.IsChecked = !enabled;
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private bool SaveSettings()
    {
        MergeExcludedAppsDraft();
        var config = _configManager.Get().Clipboard;
        var retentionDays = SelectedValue(_cmbRetention, config.RetentionDays);
        var maxEntries = SelectedValue(_cmbMaxEntries, config.MaxEntries);
        var maxImageSize = SelectedValue(_cmbMaxImageSize, config.MaxImageSizeKB);
        var excludedApps = _excludedApps.ToList();

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
