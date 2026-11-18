using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Modules.Clipboard;

namespace STool.Views.Settings;

/// <summary>剪贴板历史设置：记录开关、条数上限与隐私排除。</summary>
public class ClipboardSettingsPanel : StackPanel
{
    private readonly ConfigManager _configManager;
    private readonly IAppShell _shell;
    private System.Windows.Controls.CheckBox _chkEnabled = null!;
    private System.Windows.Controls.ComboBox _cmbMaxEntries = null!;
    private System.Windows.Controls.TextBox _txtExcludedApps = null!;
    private WrapPanel _excludedAppsList = null!;
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
            string.Empty,
            _chkEnabled,
            SaveEnabled));

        _cmbMaxEntries = SettingsLayout.CreateComboBox();
        _cmbMaxEntries.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        foreach (var count in new[] { 500, 1000, 3000, 5000 })
            AddOption(_cmbMaxEntries, $"{count} 条", count);
        SettingsLayout.SetFieldHelp(_cmbMaxEntries, "保留越多，越占硬盘空间，搜索也可能变慢。");
        recordSection.Children.Add(SettingsLayout.CreateInlineField("历史记录上限", _cmbMaxEntries, hasTopDivider: true));
        Children.Add(SettingsLayout.CreateSection(recordSection));

        // ── 隐私 ──
        var privacySection = SettingsLayout.CreateSectionContent("隐私");
        _txtExcludedApps = SettingsLayout.CreateTextBox();
        _txtExcludedApps.Width = 240;
        SettingsLayout.SetPlaceholder(_txtExcludedApps, "进程名，如 KeePass.exe");
        _txtExcludedApps.AcceptsReturn = true; // Preserve line-separated app lists when pasted.
        System.Windows.Automation.AutomationProperties.SetName(_txtExcludedApps, "添加不记录的应用");
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
        addButton.Margin = new Thickness(6, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(addButton, "添加不记录的应用");
        addButton.Click += (_, _) => CommitExcludedAppsDraft();
        var inputRow = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(_txtExcludedApps);
        Grid.SetColumn(addButton, 1);
        inputRow.Children.Add(addButton);

        privacySection.Children.Add(SettingsLayout.CreateInlineField("不记录的应用", inputRow));
        _excludedAppsList = new WrapPanel
        {
            // Compensate the trailing chip margins so only gaps add space.
            Margin = new Thickness(0, 0, -6, -6)
        };
        System.Windows.Automation.AutomationProperties.SetName(_excludedAppsList, "不记录的应用列表");
        privacySection.Children.Add(new Border
        {
            Padding = new Thickness(14, 0, 14, 12),
            Child = _excludedAppsList
        });
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
        SelectOrAddValue(_cmbMaxEntries, config.MaxEntries, count => $"{count} 条");
        _excludedApps.AddRange(ClipboardPrivacy.ParseAppList(string.Join("\n", config.ExcludedApps)));
        foreach (var app in _excludedApps)
            AddExcludedAppChip(app);
    }

    private void EnableAutoSave()
    {
        _autoSave = new SettingsAutoSaveController(this, SaveSettings);
        _autoSave.TrackImmediate(_cmbMaxEntries);
        // Track the draft so navigation/closing flushes it before removing this page.
        _autoSave.TrackDraft(_txtExcludedApps);
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
            AddExcludedAppChip(app);
            changed = true;
        }

        // A focus change to Add may commit before Click; clearing makes Click idempotent.
        _txtExcludedApps.Clear();
        return changed;
    }

    private void AddExcludedAppChip(string app)
    {
        var chip = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SettingsPillBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 6)
        };
        chip.SetBinding(MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = _excludedAppsList });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var appName = SettingsLayout.CreateHint(app);
        appName.Foreground = (System.Windows.Media.Brush)FindResource("SettingsSecondaryBrush");
        appName.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        appName.TextWrapping = TextWrapping.NoWrap;
        appName.TextTrimming = TextTrimming.CharacterEllipsis;
        SettingsLayout.SetTextHelp(appName);
        appName.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(appName);
        var removeButton = new Button
        {
            Style = (Style)FindResource("SettingsChipRemoveButton"),
            ToolTip = $"移除 {app}",
            Content = new Viewbox
            {
                Width = 10,
                Height = 10,
                Child = new System.Windows.Shapes.Path
                {
                    Data = (System.Windows.Media.Geometry)FindResource("SettingsChipRemoveGeometry"),
                    Width = 24,
                    Height = 24,
                    Stroke = (System.Windows.Media.Brush)FindResource("SettingsMutedBrush"),
                    StrokeThickness = 1.5
                }
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(removeButton, $"移除 {app}");
        removeButton.Click += (_, _) =>
        {
            _excludedApps.Remove(app);
            _excludedAppsList.Children.Remove(chip);
            _autoSave.SaveChange();
        };
        Grid.SetColumn(removeButton, 1);
        row.Children.Add(removeButton);
        chip.Child = row;
        // Append chips so committing a draft on focus loss keeps existing Remove buttons alive.
        _excludedAppsList.Children.Add(chip);
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
        var maxEntries = SelectedValue(_cmbMaxEntries, config.MaxEntries);
        var excludedApps = _excludedApps.ToList();

        if (config.MaxEntries == maxEntries &&
            config.ExcludedApps.SequenceEqual(excludedApps, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _configManager.Update(current =>
        {
            current.Clipboard.MaxEntries = maxEntries;
            current.Clipboard.ExcludedApps = excludedApps;
        });
        _shell.ApplyClipboardSettings();
        return true;
    }

    private static int SelectedValue(System.Windows.Controls.ComboBox comboBox, int fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag is int value ? value : fallback;
}
