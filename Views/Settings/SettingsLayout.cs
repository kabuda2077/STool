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

/// <summary>设置页布局参数。默认值用于正式页面,设计预览台可临时覆盖。</summary>
internal sealed class SettingsLayoutMetrics
{
    public double SpacingXS { get; init; } = 4;
    public double SpacingSM { get; init; } = 8;
    public double SpacingMD { get; init; } = 8;
    public double SpacingLG { get; init; } = 16;
    public double InlineLabelWidth { get; init; } = 112;
    public double HotkeyLabelWidth { get; init; } = 112;
    public double InputHeight { get; init; } = 34;
    public double SectionPadding { get; init; } = 0;
    public double SectionSpacing { get; init; } = 18;
    public double SectionBorderThickness { get; init; } = 0.5;
    public bool SectionShadow { get; init; } = false;
    public double ContentHorizontalMargin { get; init; } = 24;
    public double ContentTopMargin { get; init; } = 12;
    public double NavigationButtonVerticalPadding { get; init; } = 10;

    public static SettingsLayoutMetrics Default { get; } = new();

    public SettingsLayoutMetrics With(
        double? spacingMD = null,
        double? inlineLabelWidth = null,
        double? hotkeyLabelWidth = null,
        double? inputHeight = null,
        double? sectionPadding = null,
        double? sectionSpacing = null,
        double? sectionBorderThickness = null,
        double? contentHorizontalMargin = null,
        double? contentTopMargin = null,
        double? navigationButtonVerticalPadding = null,
        bool? sectionShadow = null)
    {
        return new SettingsLayoutMetrics
        {
            SpacingXS = SpacingXS,
            SpacingSM = SpacingSM,
            SpacingMD = spacingMD ?? SpacingMD,
            SpacingLG = SpacingLG,
            InlineLabelWidth = inlineLabelWidth ?? InlineLabelWidth,
            HotkeyLabelWidth = hotkeyLabelWidth ?? HotkeyLabelWidth,
            InputHeight = inputHeight ?? InputHeight,
            SectionPadding = sectionPadding ?? SectionPadding,
            SectionSpacing = sectionSpacing ?? SectionSpacing,
            SectionBorderThickness = sectionBorderThickness ?? SectionBorderThickness,
            SectionShadow = sectionShadow ?? SectionShadow,
            ContentHorizontalMargin = contentHorizontalMargin ?? ContentHorizontalMargin,
            ContentTopMargin = contentTopMargin ?? ContentTopMargin,
            NavigationButtonVerticalPadding = navigationButtonVerticalPadding ?? NavigationButtonVerticalPadding
        };
    }
}

/// <summary>设置面板共享的布局参数和 UI 工厂方法。</summary>
internal static class SettingsLayout
{
    private static SettingsLayoutMetrics _metrics = SettingsLayoutMetrics.Default;

    // ── 间距 Token ──
    public static double SpacingXS => _metrics.SpacingXS;
    public static double SpacingSM => _metrics.SpacingSM;
    public static double SpacingMD => _metrics.SpacingMD;
    public static double SpacingLG => _metrics.SpacingLG;

    /// <summary>行内字段 Label 列宽。</summary>
    public static double InlineLabelWidth => _metrics.InlineLabelWidth;

    /// <summary>快捷键行内字段 Label 列宽(标签较短)。</summary>
    public static double HotkeyLabelWidth => _metrics.HotkeyLabelWidth;

    /// <summary>输入控件统一高度。</summary>
    public static double InputHeight => _metrics.InputHeight;
    public static double BodyFontSize => (double)Application.Current.FindResource("FontSizeBody");
    public static double HintFontSize => (double)Application.Current.FindResource("FontSizeHint");

    public static double SectionPadding => _metrics.SectionPadding;
    public static double SectionSpacing => _metrics.SectionSpacing;
    public static double ContentHorizontalMargin => _metrics.ContentHorizontalMargin;
    public static double ContentTopMargin => _metrics.ContentTopMargin;
    public static double NavigationButtonVerticalPadding => _metrics.NavigationButtonVerticalPadding;
    public static double InputHintSpacing => 5;
    public static double FormTextInset => 12;

    // ── 常用 Margin ──
    public static Thickness FieldSpacing => new(24, 0, 24, 0);
    public static Thickness HintMargin => new(FormTextInset, 3, 0, 0);
    public static Thickness InlineHintMargin => new(InlineLabelWidth + FormTextInset, 3, 0, 0);
    public static Thickness SaveButtonMargin => new(0, SpacingSM, 0, 0);
    public static Thickness ActionRowMargin => new(24 + InlineLabelWidth, SpacingSM, 24, 16);

    public static SettingsLayoutMetrics CurrentMetrics => _metrics;

    public static void ApplyMetrics(SettingsLayoutMetrics metrics)
    {
        _metrics = metrics;
    }

    public static void ResetMetrics()
    {
        _metrics = SettingsLayoutMetrics.Default;
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
            Focusable = false,
            IsHitTestVisible = false
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
            Focusable = true,
            FocusVisualStyle = (Style)Application.Current.FindResource("ModernFocusVisual"),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsSectionBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, isLast ? 0 : _metrics.SectionBorderThickness)
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
            Opacity = 0.78,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        Grid.SetColumn(switchBox, 1);
        grid.Children.Add(switchBox);
        row.Child = grid;

        WireSwitchHost(row, switchBox, title, onChanged);

        return row;
    }

    private static void WireSwitchHost(Border host, CheckBox switchBox, string title, Action<bool>? onChanged)
    {
        var normalBrush = (System.Windows.Media.Brush)Application.Current.FindResource("TransparentBrush");
        var hoverBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsItemHoverBrush");
        var focusBrush = (System.Windows.Media.Brush)Application.Current.FindResource("PrimaryBrush");

        void SetChecked(bool enabled)
        {
            switchBox.IsChecked = enabled;
            System.Windows.Automation.AutomationProperties.SetName(switchBox, title);
            onChanged?.Invoke(enabled);
        }

        void Toggle() => SetChecked(switchBox.IsChecked != true);

        System.Windows.Automation.AutomationProperties.SetName(switchBox, title);

        host.MouseEnter += (_, _) => host.Background = hoverBrush;
        host.MouseLeave += (_, _) => host.Background = normalBrush;
        host.GotKeyboardFocus += (_, _) => host.BorderBrush = focusBrush;
        host.LostKeyboardFocus += (_, _) => host.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsSectionBorderBrush");
        host.MouseLeftButtonUp += (_, _) => Toggle();
        host.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
            {
                Toggle();
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
            Focusable = true,
            FocusVisualStyle = (Style)Application.Current.FindResource("ModernFocusVisual")
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
            BorderThickness = new Thickness(_metrics.SectionBorderThickness)
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

        var normalBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsInputBrush");
        var hoverBrush = (System.Windows.Media.Brush)Application.Current.FindResource("PrimarySoftBrush");
        var focusBrush = (System.Windows.Media.Brush)Application.Current.FindResource("PrimaryBrush");

        void Toggle()
        {
            var enabled = switchBox.IsChecked != true;
            switchBox.IsChecked = enabled;
            System.Windows.Automation.AutomationProperties.SetName(switchBox, title);
            onChanged?.Invoke(enabled);
        }

        System.Windows.Automation.AutomationProperties.SetName(switchBox, title);
        grid.MouseEnter += (_, _) => valueHost.Background = hoverBrush;
        grid.MouseLeave += (_, _) => valueHost.Background = normalBrush;
        grid.GotKeyboardFocus += (_, _) => valueHost.BorderBrush = focusBrush;
        grid.LostKeyboardFocus += (_, _) => valueHost.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SettingsSectionBorderBrush");
        grid.MouseLeftButtonUp += (_, _) => Toggle();
        grid.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
            {
                Toggle();
                e.Handled = true;
            }
        };

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
            Opacity = 0.78,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap
        };
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
                Margin = new Thickness(0, 0, 0, _metrics.SectionSpacing),
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
            Padding = new Thickness(_metrics.SectionPadding),
            Margin = new Thickness(0, 0, 0, includeMargin ? _metrics.SectionSpacing : 0),
            BorderThickness = new Thickness(_metrics.SectionBorderThickness),
            Effect = _metrics.SectionShadow
                ? (System.Windows.Media.Effects.Effect)Application.Current.FindResource("PaneShadow")
                : null,
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

    /// <summary>设置页主保存按钮。</summary>
    public static Button CreateSaveButton(string content = "保存设置")
    {
        return new Button
        {
            Content = content,
            Style = (Style)Application.Current.FindResource("ModernButton"),
            MinHeight = 38,
            MinWidth = 96,
            Padding = new Thickness(18, 8, 18, 8),
            Margin = SaveButtonMargin,
            HorizontalAlignment = HorizontalAlignment.Right
        };
    }
}

/// <summary>安全密码框组件，支持密文定长遮罩与显示/隐藏状态切换。</summary>
public class SecurePasswordField : Grid
{
    private readonly PasswordBox _pwd;
    private readonly TextBox _txt;
    private readonly Button _btn;
    private readonly Path _iconPath;

    private string _realPassword = "";
    private bool _isRevealed = false;
    private bool _isSyncing = false;

    // 使用定长 16 个圆点作为象征性遮罩，防止溢出裁切，同时更美观和安全
    private const string MaskPlaceholder = "••••••••••••••••";

    public event EventHandler? PasswordChanged;

    public string Password
    {
        get => _realPassword;
        set
        {
            _realPassword = value ?? "";
            UpdateUI();
        }
    }

    public SecurePasswordField()
    {
        Height = SettingsLayout.InputHeight;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _pwd = new PasswordBox
        {
            Style = (Style)Application.Current.FindResource("SunkenPasswordBox"),
            Height = SettingsLayout.InputHeight,
            FontSize = SettingsLayout.BodyFontSize,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(5, 3, 38, 3)
        };

        _txt = new TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = SettingsLayout.InputHeight,
            FontSize = SettingsLayout.BodyFontSize,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            Padding = new Thickness(5, 3, 38, 3),
            Visibility = Visibility.Collapsed
        };

        _iconPath = new Path
        {
            Data = (System.Windows.Media.Geometry)Application.Current.FindResource("IconEye"),
            Stroke = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush"),
            StrokeThickness = 2,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            Fill = (System.Windows.Media.Brush)Application.Current.FindResource("TransparentBrush"),
            Stretch = System.Windows.Media.Stretch.Uniform
        };

        _btn = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Width = SettingsLayout.InputHeight,
            Height = SettingsLayout.InputHeight,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "显示密钥",
            Content = new Viewbox
            {
                Width = 15,
                Height = 15,
                Child = _iconPath
            }
        };

        _pwd.PasswordChanged += (s, e) =>
        {
            if (_isSyncing) return;
            var newText = _pwd.Password;
            if (newText.Contains('•'))
            {
                var cleanText = newText.Replace("•", "");
                _realPassword = cleanText;
                _isSyncing = true;
                _pwd.Password = cleanText;
                _isSyncing = false;
            }
            else
            {
                _realPassword = newText;
            }
            _isSyncing = true;
            _txt.Text = _realPassword;
            _isSyncing = false;
            PasswordChanged?.Invoke(this, EventArgs.Empty);
        };

        _txt.TextChanged += (s, e) =>
        {
            if (_isSyncing) return;
            _realPassword = _txt.Text;
            _isSyncing = true;
            if (_isRevealed)
            {
                _pwd.Password = _realPassword;
            }
            else
            {
                _pwd.Password = string.IsNullOrEmpty(_realPassword) ? "" : MaskPlaceholder;
            }
            _isSyncing = false;
            PasswordChanged?.Invoke(this, EventArgs.Empty);
        };

        _pwd.GotFocus += (s, e) => _pwd.SelectAll();
        _txt.GotFocus += (s, e) => _txt.SelectAll();

        _pwd.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (!_pwd.IsKeyboardFocusWithin)
            {
                _pwd.Focus();
                e.Handled = true;
            }
        };
        _txt.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (!_txt.IsKeyboardFocusWithin)
            {
                _txt.Focus();
                e.Handled = true;
            }
        };

        _btn.Click += (s, e) => ToggleReveal();

        Children.Add(_pwd);
        Children.Add(_txt);
        Children.Add(_btn);
    }

    private void ToggleReveal()
    {
        _isRevealed = !_isRevealed;
        _isSyncing = true;
        if (_isRevealed)
        {
            _txt.Text = _realPassword;
            _pwd.Visibility = Visibility.Collapsed;
            _txt.Visibility = Visibility.Visible;
            _btn.ToolTip = "隐藏密钥";
            _iconPath.Data = (System.Windows.Media.Geometry)Application.Current.FindResource("IconEyeOff");
            _txt.Focus();
            _txt.CaretIndex = _txt.Text.Length;
        }
        else
        {
            _pwd.Password = string.IsNullOrEmpty(_realPassword) ? "" : MaskPlaceholder;
            _txt.Visibility = Visibility.Collapsed;
            _pwd.Visibility = Visibility.Visible;
            _btn.ToolTip = "显示密钥";
            _iconPath.Data = (System.Windows.Media.Geometry)Application.Current.FindResource("IconEye");
            _pwd.Focus();
        }
        _isSyncing = false;
    }

    private void UpdateUI()
    {
        _isSyncing = true;
        if (_isRevealed)
        {
            _txt.Text = _realPassword;
            _pwd.Password = _realPassword;
        }
        else
        {
            _txt.Text = _realPassword;
            _pwd.Password = string.IsNullOrEmpty(_realPassword) ? "" : MaskPlaceholder;
        }
        _isSyncing = false;
    }
}
