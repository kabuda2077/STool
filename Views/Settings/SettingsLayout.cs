using System;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace STool.Views.Settings;

/// <summary>设置面板共享的布局参数和 UI 工厂方法。</summary>
internal static class SettingsLayout
{
    // ── 间距与尺寸常量 ──
    public const double SpacingXS = 4;
    public const double SpacingSM = 8;
    public const double SpacingMD = 8;

    /// <summary>行内字段 Label 列宽。</summary>
    public const double InlineLabelWidth = 112;

    /// <summary>快捷键行内字段 Label 列宽(标签较短)。</summary>
    public const double HotkeyLabelWidth = 112;

    /// <summary>输入控件统一高度。</summary>
    public const double InputHeight = 34;
    public const double SectionPadding = 0;
    public const double SectionSpacing = 18;
    public const double SectionBorderThickness = 0.5;
    public const double ContentHorizontalMargin = 24;
    public const double ContentTopMargin = 12;
    public const double InputHintSpacing = 5;
    public const double FormTextInset = 12;

    public static double BodyFontSize => (double)Application.Current.FindResource("FontSizeBody");
    public static double HintFontSize => (double)Application.Current.FindResource("FontSizeHint");

    // ── 常用 Margin ──
    public static Thickness FieldSpacing => new(24, 0, 24, 0);
    public static Thickness HintMargin => new(FormTextInset, 3, 0, 0);
    public static Thickness InlineHintMargin => new(InlineLabelWidth + FormTextInset, 3, 0, 0);
    public static Thickness ActionRowMargin => new(24 + InlineLabelWidth, SpacingSM, 24, 16);

    /// <summary>已保存的密钥无法解密时提示用户重新填写，而不是静默显示为空。</summary>
    public static void WarnUnreadableSecrets()
    {
        STool.Core.ToastNotification.Show(
            "已保存的密钥无法解密",
            "Data\\secure.key 缺失或已更换，请重新填写 API Key 等密钥。未修改的字段会保留原值。",
            STool.Core.ToastNotification.ToastType.Warning,
            duration: 6000);
    }

    // ── UI 工厂方法 ──

    /// <summary>创建 Label(左) + Input(右) 同行布局。</summary>
    public static Grid CreateInlineField(string label, FrameworkElement input,
        double? labelWidth = null, bool isLast = false)
    {
        var resolvedLabelWidth = labelWidth ?? InlineLabelWidth;
        var grid = new Grid
        {
            Margin = FieldSpacing,
            MinHeight = 58
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(resolvedLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        SetFieldLabel(input, lbl);
        input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        AddRowDivider(grid, isLast);
        return grid;
    }

    /// <summary>创建 Label(左) + [Input + Hint](右) 同行布局，使提示紧贴输入框并对其对齐。</summary>
    public static Grid CreateInlineFieldWithHint(string label, FrameworkElement input, string hint,
        double? labelWidth = null, bool isLast = false)
    {
        return CreateInlineFieldWithHint(label, input, CreateHint(hint, inline: false), labelWidth, isLast);
    }

    /// <summary>创建可动态更新提示文本的行内字段。</summary>
    public static Grid CreateInlineFieldWithHint(string label, FrameworkElement input, TextBlock hintBlock,
        double? labelWidth = null, bool isLast = false)
    {
        var resolvedLabelWidth = labelWidth ?? InlineLabelWidth;
        var grid = new Grid
        {
            Margin = FieldSpacing,
            MinHeight = 72
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(resolvedLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(lbl, 0);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        SetFieldLabel(input, lbl);
        input.VerticalAlignment = VerticalAlignment.Top;
        input.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(input, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        hintBlock.Margin = new Thickness(FormTextInset, InputHintSpacing, 0, 12);
        Grid.SetRow(hintBlock, 1);
        Grid.SetColumn(hintBlock, 1);
        grid.Children.Add(hintBlock);

        AddRowDivider(grid, isLast);
        return grid;
    }

    private static void SetFieldLabel(FrameworkElement input, TextBlock label)
    {
        System.Windows.Automation.AutomationProperties.SetLabeledBy(input, label);
        if (input is SecurePasswordField passwordField)
            passwordField.SetAccessibleLabel(label);
    }

    private static void AddRowDivider(Grid grid, bool isLast)
    {
        if (isLast)
        {
            return;
        }

        var divider = new Border
        {
            Height = 0.5,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsSectionBorderBrush"),
            VerticalAlignment = VerticalAlignment.Bottom,
            SnapsToDevicePixels = true
        };
        Grid.SetColumnSpan(divider, Math.Max(1, grid.ColumnDefinitions.Count));
        if (grid.RowDefinitions.Count > 0)
        {
            Grid.SetRowSpan(divider, grid.RowDefinitions.Count);
        }
        grid.Children.Add(divider);
    }

    /// <summary>创建标准 TextBox。</summary>
    public static System.Windows.Controls.TextBox CreateTextBox()
    {
        return new System.Windows.Controls.TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = InputHeight,
            MinHeight = InputHeight,
            FontSize = BodyFontSize,
            TextAlignment = TextAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    /// <summary>创建标准 ComboBox。</summary>
    public static System.Windows.Controls.ComboBox CreateComboBox()
    {
        return new System.Windows.Controls.ComboBox
        {
            Style = (Style)Application.Current.FindResource("SunkenComboBox"),
            Height = InputHeight,
            MinHeight = InputHeight,
            FontSize = BodyFontSize,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    /// <summary>创建可编辑 ComboBox。</summary>
    public static System.Windows.Controls.ComboBox CreateEditableComboBox()
    {
        var cb = CreateComboBox();
        cb.IsEditable = true;
        cb.IsTextSearchEnabled = false;
        return cb;
    }

    /// <summary>创建安全密码框组合控件。</summary>
    public static SecurePasswordField CreatePasswordField()
    {
        return new SecurePasswordField();
    }

    /// <summary>创建设置页开关控件。</summary>
    public static CheckBox CreateSwitch()
    {
        return new CheckBox
        {
            Style = (Style)Application.Current.FindResource("SwitchCheckBox"),
            FontSize = BodyFontSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Focusable = true,
            IsHitTestVisible = true
        };
    }

    /// <summary>创建设置页布尔偏好行。</summary>
    public static Border CreateSwitchRow(string title, string description, CheckBox switchBox, Action<bool>? onChanged = null, bool isLast = false)
    {
        var row = new Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("TransparentBrush"),
            CornerRadius = new CornerRadius(0),
            MinHeight = 60,
            Padding = new Thickness(24, 10, 20, 10),
            Margin = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsSectionBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, isLast ? 0 : SectionBorderThickness)
        };
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        System.Windows.Automation.AutomationProperties.SetHelpText(row, description);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimaryBrush"),
            FontWeight = FontWeights.Normal
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.FindResource("HintText"),
            FontSize = HintFontSize,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        Grid.SetColumn(switchBox, 1);
        grid.Children.Add(switchBox);
        row.Child = grid;

        WireSwitchHost(row, row, switchBox, title, description, onChanged,
            (System.Windows.Media.Brush)Application.Current.FindResource("TransparentBrush"),
            (System.Windows.Media.Brush)Application.Current.FindResource("SettingsItemHoverBrush"));

        return row;
    }

    private static void WireSwitchHost(FrameworkElement host, Border surface, CheckBox switchBox,
        string title, string description, Action<bool>? onChanged,
        System.Windows.Media.Brush normalBrush, System.Windows.Media.Brush hoverBrush)
    {
        var focusBrush = (System.Windows.Media.Brush)surface.FindResource("PrimaryBrush");
        var borderBrush = surface.BorderBrush;
        var notifying = false;

        void OnChanged(object sender, RoutedEventArgs e)
        {
            // Initial values are assigned before the panel is loaded. A failed callback may
            // restore the old value; that restoration must not invoke the callback again.
            if (!switchBox.IsLoaded || notifying)
                return;

            notifying = true;
            try { onChanged?.Invoke(switchBox.IsChecked == true); }
            finally { notifying = false; }
        }

        switchBox.Checked += OnChanged;
        switchBox.Unchecked += OnChanged;
        System.Windows.Automation.AutomationProperties.SetName(switchBox, title);
        System.Windows.Automation.AutomationProperties.SetHelpText(switchBox, description);

        host.MouseEnter += (_, _) => surface.Background = hoverBrush;
        host.MouseLeave += (_, _) => surface.Background = normalBrush;
        host.GotKeyboardFocus += (_, _) => surface.BorderBrush = focusBrush;
        host.LostKeyboardFocus += (_, _) => surface.BorderBrush = borderBrush;
        host.MouseLeftButtonUp += (_, e) =>
        {
            // Native checkbox clicks are handled by CheckBox itself. The rest of the row
            // focuses and changes the same control, including its automation toggle state.
            if (switchBox.IsMouseOver)
                return;
            switchBox.Focus();
            switchBox.IsChecked = switchBox.IsChecked != true;
            e.Handled = true;
        };
        host.PreviewKeyDown += (_, e) =>
        {
            // Space is handled by the focused CheckBox; Enter also activates the row.
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                switchBox.IsChecked = switchBox.IsChecked != true;
                e.Handled = true;
            }
        };
    }

    /// <summary>创建行内布尔偏好字段,用于表单区域里的开关项。</summary>
    public static Grid CreateInlineSwitchField(string label, string title, string description, CheckBox switchBox, Action<bool>? onChanged = null, bool isLast = false)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, isLast ? 0 : SpacingMD),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(grid, title);
        System.Windows.Automation.AutomationProperties.SetHelpText(grid, description);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InlineLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!string.IsNullOrWhiteSpace(description))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        var labelHost = new Border
        {
            Height = InputHeight,
            Child = lbl
        };
        Grid.SetRow(labelHost, 0);
        Grid.SetColumn(labelHost, 0);
        grid.Children.Add(labelHost);

        var valueGrid = new Grid();
        valueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueHost = new Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsInputBrush"),
            CornerRadius = (CornerRadius)Application.Current.FindResource("CornerRadiusMedium"),
            Height = InputHeight,
            Padding = new Thickness(10, 0, 10, 0),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsSectionBorderBrush"),
            BorderThickness = new Thickness(SectionBorderThickness)
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleBlock, 0);
        valueGrid.Children.Add(titleBlock);

        Grid.SetColumn(switchBox, 1);
        valueGrid.Children.Add(switchBox);
        valueHost.Child = valueGrid;
        Grid.SetRow(valueHost, 0);
        Grid.SetColumn(valueHost, 1);
        grid.Children.Add(valueHost);

        if (!string.IsNullOrWhiteSpace(description))
        {
            var hintBlock = CreateHint(description);
            hintBlock.Margin = new Thickness(FormTextInset, InputHintSpacing, 0, 0);
            Grid.SetRow(hintBlock, 1);
            Grid.SetColumn(hintBlock, 1);
            grid.Children.Add(hintBlock);
        }

        WireSwitchHost(grid, valueHost, switchBox, title, description, onChanged,
            (System.Windows.Media.Brush)Application.Current.FindResource("SettingsInputBrush"),
            (System.Windows.Media.Brush)Application.Current.FindResource("PrimarySoftBrush"));

        return grid;
    }

    /// <summary>创建提示文本。</summary>
    public static TextBlock CreateHint(string text, bool inline = false)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("HintText"),
            Margin = inline ? InlineHintMargin : HintMargin,
            FontSize = HintFontSize,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush"),
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap
        };
    }

    /// <summary>
    /// 分组顶部的说明文字；可附带一个链接（如「前往服务账号」）。
    /// </summary>
    public static TextBlock CreateSectionNote(string text, string? linkText = null, Action? onLink = null)
    {
        var block = CreateHint(text);
        block.Margin = new Thickness(ContentHorizontalMargin, 12, ContentHorizontalMargin, 0);
        if (linkText != null && onLink != null)
        {
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(linkText))
            {
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("PrimaryBrush"),
                TextDecorations = null,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            link.Click += (_, _) => onLink();
            block.Inlines.Add(new System.Windows.Documents.Run(" "));
            block.Inlines.Add(link);
        }

        return block;
    }

    /// <summary>创建设置页轻量分组容器。</summary>
    public static StackPanel CreateSectionContent(string title)
    {
        return new StackPanel
        {
            Tag = title
        };
    }

    /// <summary>创建设置页轻量分组容器。</summary>
    public static Border CreateSection(UIElement content)
    {
        if (content is StackPanel { Tag: string title } titledContent)
        {
            var wrapper = new StackPanel();
            wrapper.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)Application.Current.FindResource("SettingsGroupTitle")
            });
            wrapper.Children.Add(CreateSectionPanel(titledContent, includeMargin: false));

            return new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("TransparentBrush"),
                Margin = new Thickness(0, 0, 0, SectionSpacing),
                Child = wrapper
            };
        }

        return CreateSectionPanel(content);
    }

    private static Border CreateSectionPanel(UIElement content, bool includeMargin = true)
    {
        return new Border
        {
            Style = (Style)Application.Current.FindResource("SettingsFlatSection"),
            Padding = new Thickness(SectionPadding),
            Margin = new Thickness(0, 0, 0, includeMargin ? SectionSpacing : 0),
            BorderThickness = new Thickness(SectionBorderThickness),
            Child = content
        };
    }

    /// <summary>设置页次级操作按钮,用于获取模型、测试等行内动作。</summary>
    public static Button CreateSecondaryActionButton(string content)
    {
        return new Button
        {
            Content = content,
            Style = (Style)Application.Current.FindResource("SettingsActionButton")
        };
    }

    /// <summary>设置页字段后的行内操作区,左边缘与输入控件对齐。</summary>
    public static StackPanel CreateActionRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = ActionRowMargin
        };

        for (var i = 0; i < buttons.Length; i++)
        {
            if (i > 0)
            {
                buttons[i].Margin = new Thickness(SpacingSM, 0, 0, 0);
            }

            row.Children.Add(buttons[i]);
        }

        return row;
    }

    /// <summary>创建可折叠的设置页轻量分组。</summary>
    public static (StackPanel content, Border section, Expander expander) CreateCollapsibleSection(string title, bool isExpanded = false)
    {
        var content = new StackPanel();
        var expander = new Expander
        {
            Style = (Style)Application.Current.FindResource("SettingsExpander"),
            Header = title,
            Content = content,
            IsExpanded = isExpanded
        };

        return (content, CreateSection(expander), expander);
    }

    /// <summary>创建更紧凑的折叠分组,用于仅显示标题的高级配置区。</summary>
    public static (StackPanel content, Border section, Expander expander) CreateCompactCollapsibleSection(string title, bool isExpanded = false)
    {
        var result = CreateCollapsibleSection(title, isExpanded);
        result.section.Padding = new Thickness(0);
        return result;
    }
}
