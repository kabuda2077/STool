using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using STool.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace STool.Views.Settings;

/// <summary>设置页原生控件布局，对应 stool-magpie.html 的行、分组及字段。</summary>
internal static class SettingsLayout
{
    private static readonly DependencyProperty FieldHelpInitializedProperty = DependencyProperty.RegisterAttached(
        "FieldHelpInitialized", typeof(bool), typeof(SettingsLayout), new PropertyMetadata(false));
    private static readonly DependencyProperty TextHelpTargetProperty = DependencyProperty.RegisterAttached(
        "TextHelpTarget", typeof(FrameworkElement), typeof(SettingsLayout));
    private static readonly DependencyProperty TextHelpContentProperty = DependencyProperty.RegisterAttached(
        "TextHelpContent", typeof(string), typeof(SettingsLayout));
    public const double InputHeight = 27;
    public const double SectionSpacing = 8;
    public const double ContentHorizontalMargin = 16;
    public const double ContentTopMargin = 2;

    public static double BodyFontSize => (double)Application.Current.FindResource("SettingsBodyFontSize");
    public static double HintFontSize => (double)Application.Current.FindResource("SettingsHintFontSize");

    public static void WarnUnreadableSecrets() => ToastNotification.Show(
        "已保存的密钥无法解密",
        "Data\\secure.key 缺失或已更换，请重新填写 API Key 等密钥。未修改的字段会保留原值。",
        ToastNotification.ToastType.Warning, duration: 6000);

    /// <summary>普通设置行：文字在左、字段在右；分隔线跨整个列表宽度。</summary>
    public static Grid CreateInlineField(string label, FrameworkElement input,
        double? labelWidth = null, bool hasTopDivider = false, double? minHeight = null)
    {
        var row = CreateField(label, input, account: false, labelWidth, minHeight ?? 52);
        if (hasTopDivider) AddRowDivider(row);
        return row;
    }

    public static Grid CreateInlineFieldWithHint(string label, FrameworkElement input, string hint,
        double? labelWidth = null, bool hasTopDivider = false)
    {
        var row = CreateInlineField(label, input, labelWidth, hasTopDivider);
        SetFieldHelp(input, hint);
        return row;
    }

    public static Grid CreateInlineFieldWithHint(string label, FrameworkElement input, TextBlock hintBlock,
        double? labelWidth = null, bool hasTopDivider = false)
    {
        var row = CreateField(label, input, account: false, labelWidth, 52);
        AddHint(row, hintBlock, account: false);
        if (hasTopDivider) AddRowDivider(row);
        return row;
    }

    /// <summary>账号详情保留14 DIP内距，标签100 DIP、列距16 DIP，内部分隔线跟随表单。</summary>
    public static Grid CreateAccountField(string label, FrameworkElement input) =>
        CreateField(label, input, account: true, null, 52);

    public static Grid CreateAccountFieldWithHint(string label, FrameworkElement input, TextBlock hintBlock)
    {
        var row = CreateField(label, input, account: true, null, 52);
        AddHint(row, hintBlock, account: true);
        return row;
    }

    private static Grid CreateField(string label, FrameworkElement input, bool account,
        double? labelWidth, double height)
    {
        var row = new Grid
        {
            Margin = account ? new Thickness(14, 0, 14, 0) : new Thickness(0),
            MinHeight = height,
            Background = Brushes.Transparent
        };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = height });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = account ? new GridLength(100) : labelWidth is { } width ? new GridLength(width) : new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(account ? 16 : 12) });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = account ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
        });
        var text = new TextBlock
        {
            Text = label,
            Style = Resource<Style>(account ? "SettingsEditorLabel" : "SettingsFieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = account ? new Thickness(0) : new Thickness(14, 0, 0, 0)
        };
        if (!account && label.StartsWith("Windows ", StringComparison.Ordinal))
        {
            text.Inlines.Clear();
            text.Inlines.Add(new Run("Windows ") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(label[8..]));
        }
        row.Children.Add(text);
        input.VerticalAlignment = VerticalAlignment.Center;
        input.Margin = account ? new Thickness(0) : new Thickness(input.Margin.Left, input.Margin.Top, 12, input.Margin.Bottom);
        if (!account)
        {
            row.SizeChanged += (_, _) => input.MaxWidth = Math.Max(0, (row.ActualWidth - 26) * .6);
        }
        Grid.SetColumn(input, 2);
        row.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(input, text);
        if (input is SecurePasswordField password) password.SetAccessibleLabel(text);
        return row;
    }

    private static void AddHint(Grid row, TextBlock hint, bool account)
    {
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var firstRowStyle = new Style(typeof(RowDefinition));
        firstRowStyle.Setters.Add(new Setter(RowDefinition.MinHeightProperty, 52d));
        var visibleHint = new System.Windows.Data.Binding(nameof(TextBlock.Visibility)) { Source = hint };
        var compactRow = new DataTrigger { Binding = visibleHint, Value = Visibility.Visible };
        compactRow.Setters.Add(new Setter(RowDefinition.MinHeightProperty, account ? 35d : 33d));
        firstRowStyle.Triggers.Add(compactRow);
        row.RowDefinitions[0].ClearValue(RowDefinition.MinHeightProperty);
        row.RowDefinitions[0].Style = firstRowStyle;
        hint.LineHeight = account ? 16 : 16.5;
        hint.Margin = new Thickness(account ? 0 : 14, account ? 10 : 8, account ? 0 : 12, 4);
        Grid.SetRow(hint, 1);
        Grid.SetColumnSpan(hint, 3);
        row.Children.Add(hint);
    }

    private static void AddRowDivider(Grid row)
    {
        // CSS .row + .row includes its top border in the 52 DIP row. Reserve that
        // pixel before centering the contents without changing the overall height.
        foreach (var child in row.Children.OfType<FrameworkElement>())
            if (Grid.GetRow(child) == 0)
                child.Margin = new Thickness(child.Margin.Left, child.Margin.Top + 1, child.Margin.Right, child.Margin.Bottom);
        var divider = new Border
        {
            Name = "SettingsRowDivider",
            Height = 1,
            Background = Resource<Brush>("SettingsRowLineBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(divider, row.ColumnDefinitions.Count);
        Grid.SetRowSpan(divider, row.RowDefinitions.Count);
        row.Children.Add(divider);
    }

    public static TextBox CreateTextBox() => new()
    {
        Style = Resource<Style>("SettingsTextBox"),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    public static ComboBox CreateComboBox() => new()
    {
        Style = Resource<Style>("SettingsComboBox"),
        MinWidth = 100,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        HorizontalAlignment = HorizontalAlignment.Right
    };

    public static ComboBox CreateEditableComboBox()
    {
        var combo = CreateComboBox();
        combo.IsEditable = true;
        combo.IsTextSearchEnabled = false;
        combo.FontFamily = Resource<FontFamily>("SettingsBodyFontFamily");
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        return combo;
    }

    public static SecurePasswordField CreatePasswordField() => new();

    public static CheckBox CreateSwitch() => new()
    {
        Style = Resource<Style>("SettingsSwitch"),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static Border CreateSwitchRow(string title, string description, CheckBox switchBox,
        Action<bool>? onChanged = null, bool hasTopDivider = false)
    {
        var row = new Border
        {
            Background = Brushes.Transparent,
            MinHeight = 52,
            Padding = new Thickness(14, 4, 12, 4),
            BorderBrush = Resource<Brush>("SettingsRowLineBrush"),
            BorderThickness = new Thickness(0, hasTopDivider ? 1 : 0, 0, 0),
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = title,
            Style = Resource<Style>("SettingsFieldLabel"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(switchBox, 1);
        grid.Children.Add(switchBox);
        row.Child = grid;
        System.Windows.Automation.AutomationProperties.SetName(switchBox, title);
        SetFieldHelp(switchBox, description);
        System.Windows.Automation.AutomationProperties.SetHelpText(row, description);
        var notifying = false;
        void Changed(object sender, RoutedEventArgs args)
        {
            if (!switchBox.IsLoaded || notifying) return;
            notifying = true;
            try { onChanged?.Invoke(switchBox.IsChecked == true); }
            finally { notifying = false; }
        }
        switchBox.Checked += Changed;
        switchBox.Unchecked += Changed;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (switchBox.IsMouseOver) return;
            switchBox.Focus();
            switchBox.IsChecked = switchBox.IsChecked != true;
            e.Handled = true;
        };
        row.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            switchBox.IsChecked = switchBox.IsChecked != true;
            e.Handled = true;
        };
        return row;
    }

    public static TextBlock CreateHint(string text, bool inline = false) => new()
    {
        Text = text,
        Style = Resource<Style>("SettingsHintText"),
        Margin = new Thickness(0)
    };

    public static WrapPanel CreateSectionNote(string text, string? linkText = null, Action? onLink = null)
    {
        var note = new WrapPanel { MinHeight = 23, Margin = new Thickness(14, 0, 14, 10) };
        var message = CreateHint(text);
        message.VerticalAlignment = VerticalAlignment.Center;
        note.Children.Add(message);
        if (linkText != null && onLink != null)
        {
            var action = CreateSecondaryActionButton(linkText);
            action.FontSize = HintFontSize;
            action.Height = 23;
            action.VerticalAlignment = VerticalAlignment.Center;
            action.Click += (_, _) => onLink();
            note.Children.Add(action);
        }
        return note;
    }

    public static void SetSectionNoteText(WrapPanel note, string text) =>
        ((TextBlock)note.Children[0]).Text = text;

    public static StackPanel CreateSectionContent(string title) => new() { Tag = title };

    public static Border CreateSection(UIElement content) => CreateSection(content, null);

    public static Border CreateSection(UIElement content, Button? titleAction)
    {
        if (content is not StackPanel { Tag: string title } panel) return SectionPanel(content);
        var header = new Grid { MinHeight = 26, Margin = new Thickness(0, 2, 0, 6) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Style = Resource<Style>("SettingsGroupTitle"),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (titleAction != null)
        {
            titleAction.HorizontalAlignment = HorizontalAlignment.Right;
            titleAction.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(titleAction);
        }
        var wrapper = new StackPanel();
        wrapper.Children.Add(header);
        wrapper.Children.Add(SectionPanel(panel));
        return new Border { Background = Brushes.Transparent, Child = wrapper };
    }

    private static Border SectionPanel(UIElement content) => new()
    {
        Style = Resource<Style>("SettingsFlatSection"),
        Margin = new Thickness(0, 0, 0, content is StackPanel { Children: var children }
            && children.OfType<Expander>().Any() ? 12 : SectionSpacing),
        Child = content
    };

    public static Button CreateSecondaryActionButton(string text) => new()
    {
        Content = text,
        Style = Resource<Style>("SettingsActionButton")
    };

    public static Viewbox CreateChevron(bool pointsRight = false)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path { Style = Resource<Style>("SettingsChevronPath") });
        return new Viewbox
        {
            Width = 12, Height = 12, Child = canvas,
            RenderTransformOrigin = new System.Windows.Point(.5, .5),
            RenderTransform = new RotateTransform(pointsRight ? -90 : 0)
        };
    }

    public static Button CreateFieldButton(string label, Action onClick)
    {
        var text = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(text);
        var chevron = CreateChevron(pointsRight: true);
        chevron.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(chevron, 1);
        content.Children.Add(chevron);
        var button = new Button
        {
            Content = content, Tag = text,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = Resource<Style>("SettingsActionButton"),
            FontFamily = Resource<FontFamily>("SettingsPickerFontFamily"),
            FontSize = Resource<double>("SettingsFieldFontSize"),
            Height = 25,
            Padding = new Thickness(8, 0, 5, 0)
        };
        SetFieldButtonLabel(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static void SetFieldButtonLabel(Button button, string text)
    {
        var label = (TextBlock)button.Tag;
        var accessibleName = System.Windows.Automation.AutomationProperties.GetName(button);
        if (string.IsNullOrEmpty(accessibleName) || accessibleName == label.Text)
            System.Windows.Automation.AutomationProperties.SetName(button, text);
        label.Text = text;
    }

    /// <summary>只补充被省略的文字，或显示名称背后的原始模型 ID。</summary>
    public static void SetTextHelp(TextBlock label, string? fullText = null, FrameworkElement? target = null)
    {
        var initialized = label.ReadLocalValue(TextHelpTargetProperty) != DependencyProperty.UnsetValue;
        label.SetValue(TextHelpTargetProperty, target ?? label);
        label.SetValue(TextHelpContentProperty, fullText ?? label.Text);
        if (!initialized)
        {
            label.Loaded += (_, _) => UpdateTextHelp(label);
            label.SizeChanged += (_, _) => UpdateTextHelp(label);
        }
        UpdateTextHelp(label);
    }

    private static void UpdateTextHelp(TextBlock label)
    {
        var fullText = (string)label.GetValue(TextHelpContentProperty);
        var target = (FrameworkElement)label.GetValue(TextHelpTargetProperty);
        var truncated = false;
        if (label.ActualWidth > 0 && label.TextTrimming != TextTrimming.None)
        {
            var text = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentUICulture,
                label.FlowDirection, new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
            truncated = text.WidthIncludingTrailingWhitespace > label.ActualWidth + .5;
        }
        SetFieldHelp(target, fullText.Length > 0 && (fullText != label.Text || truncated) ? fullText : string.Empty);
    }

    public static void SetFieldHelp(FrameworkElement element, string description)
    {
        System.Windows.Automation.AutomationProperties.SetHelpText(element, description);
        if (string.IsNullOrWhiteSpace(description))
        {
            if (element.ToolTip is ToolTip oldTip) oldTip.IsOpen = false;
            element.ToolTip = null;
            return;
        }
        if (element.ToolTip is ToolTip existing) { existing.Content = description; return; }
        var tip = new ToolTip
        {
            Content = description,
            PlacementTarget = element,
            Style = Resource<Style>("SettingsToolTip")
        };
        element.ToolTip = tip;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(element, 450);
        System.Windows.Controls.ToolTipService.SetBetweenShowDelay(element, 0);
        System.Windows.Controls.ToolTipService.SetShowDuration(element, int.MaxValue);
        if ((bool)element.GetValue(FieldHelpInitializedProperty)) return;
        element.SetValue(FieldHelpInitializedProperty, true);
        element.GotKeyboardFocus += (_, _) =>
        {
            if (!element.IsMouseOver && element.ToolTip is ToolTip currentTip) currentTip.IsOpen = true;
        };
        element.LostKeyboardFocus += (_, _) =>
        {
            if (element.ToolTip is ToolTip currentTip) currentTip.IsOpen = false;
        };
        element.Unloaded += (_, _) =>
        {
            if (element.ToolTip is ToolTip currentTip) currentTip.IsOpen = false;
        };
        element.PreviewMouseDown += (_, _) =>
        {
            if (element.ToolTip is ToolTip currentTip) currentTip.IsOpen = false;
        };
        element.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
            if (element.ToolTip is ToolTip currentTip) currentTip.IsOpen = false;
        };
    }

    public static void SetPlaceholder(TextBox input, string placeholder) => SettingsInput.SetPlaceholder(input, placeholder);

    public static StackPanel CreateActionRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        for (var i = 0; i < buttons.Length; i++)
        {
            if (i > 0) buttons[i].Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(buttons[i]);
        }
        return row;
    }

    public static (StackPanel content, Border section, Expander expander) CreateCompactCollapsibleSection(string title, bool isExpanded = false)
    {
        var content = new StackPanel();
        var expander = new Expander
        {
            Header = title, Content = content, IsExpanded = isExpanded,
            Style = Resource<Style>("SettingsCompactExpander")
        };
        System.Windows.Automation.AutomationProperties.SetName(expander, title);
        return (content, new Border { Margin = new Thickness(0, 2, 0, 12), Child = expander }, expander);
    }

    private static T Resource<T>(string key) => (T)Application.Current.FindResource(key);
}
