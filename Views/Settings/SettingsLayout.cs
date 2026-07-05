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
    public double SpacingMD { get; init; } = 6;
    public double SpacingLG { get; init; } = 14;
    public double InlineLabelWidth { get; init; } = 84;
    public double HotkeyLabelWidth { get; init; } = 84;
    public double InputHeight { get; init; } = 32;
    public double SectionPadding { get; init; } = 14;
    public double SectionSpacing { get; init; } = 8;
    public double SectionBorderThickness { get; init; } = 0;
    public bool SectionShadow { get; init; } = true;
    public double ContentHorizontalMargin { get; init; } = 10;
    public double ContentTopMargin { get; init; } = 2;
    public double NavigationButtonVerticalPadding { get; init; } = 10;

    public static SettingsLayoutMetrics Default { get; } = new();

    public SettingsLayoutMetrics With(
        double? spacingMD = null,
        double? inlineLabelWidth = null,
        double? hotkeyLabelWidth = null,
        double? inputHeight = null,
        double? sectionPadding = null,
        double? sectionSpacing = null,
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
            SectionBorderThickness = SectionBorderThickness,
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

    // ── 常用 Margin ──
    public static Thickness FieldSpacing => new(0, 0, 0, SpacingMD);
    public static Thickness HintMargin => new(0, 3, 0, 0);
    public static Thickness InlineHintMargin => new(InlineLabelWidth, 3, 0, 0);
    public static Thickness SaveButtonMargin => new(0, SpacingXS, 0, 0);
    public static Thickness ActionRowMargin => new(InlineLabelWidth, SpacingXS, 0, 0);

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
        double? labelWidth = null)
    {
        var resolvedLabelWidth = labelWidth ?? InlineLabelWidth;
        var grid = new Grid { Margin = FieldSpacing };
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

        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        return grid;
    }

    /// <summary>创建 Label(左) + [Input + Hint](右) 同行布局，使提示紧贴输入框并对其对齐。</summary>
    public static Grid CreateInlineFieldWithHint(string label, FrameworkElement input, string hint,
        double? labelWidth = null)
    {
        var resolvedLabelWidth = labelWidth ?? InlineLabelWidth;
        var grid = new Grid { Margin = FieldSpacing };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(resolvedLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        Grid.SetRow(lbl, 0);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        Grid.SetRow(input, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        var hintBlock = CreateHint(hint, inline: false);
        hintBlock.Margin = new Thickness(0, InputHintSpacing, 0, 0);
        Grid.SetRow(hintBlock, 1);
        Grid.SetColumn(hintBlock, 1);
        grid.Children.Add(hintBlock);

        return grid;
    }

    /// <summary>创建标准 TextBox。</summary>
    public static System.Windows.Controls.TextBox CreateTextBox()
    {
        return new System.Windows.Controls.TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = InputHeight,
            FontSize = BodyFontSize,
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
            FontSize = BodyFontSize,
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
            IsHitTestVisible = false
        };
    }

    /// <summary>创建设置页布尔偏好行。</summary>
    public static Border CreateSwitchRow(string title, string description, CheckBox switchBox, Action<bool>? onChanged = null, bool isLast = false)
    {
        var row = new Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceAltBrush"),
            CornerRadius = (CornerRadius)Application.Current.FindResource("CornerRadiusMedium"),
            MinHeight = 48,
            Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, isLast ? SpacingXS : SpacingMD),
            Cursor = System.Windows.Input.Cursors.Hand
        };

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

        var normalBrush = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceAltBrush");
        var hoverBrush = (System.Windows.Media.Brush)Application.Current.FindResource("PrimarySoftBrush");
        row.MouseEnter += (_, _) => row.Background = hoverBrush;
        row.MouseLeave += (_, _) => row.Background = normalBrush;
        row.MouseLeftButtonUp += (_, _) =>
        {
            var enabled = switchBox.IsChecked != true;
            switchBox.IsChecked = enabled;
            onChanged?.Invoke(enabled);
        };

        return row;
    }

    /// <summary>创建行内布尔偏好字段,用于表单区域里的开关项。</summary>
    public static Grid CreateInlineSwitchField(string label, string title, string description, CheckBox switchBox, Action<bool>? onChanged = null, bool isLast = false)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, isLast ? 0 : SpacingMD),
            Cursor = System.Windows.Input.Cursors.Hand
        };
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
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceAltBrush"),
            CornerRadius = (CornerRadius)Application.Current.FindResource("CornerRadiusMedium"),
            Height = InputHeight,
            Padding = new Thickness(10, 0, 10, 0)
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
            hintBlock.Margin = new Thickness(0, InputHintSpacing, 0, 0);
            Grid.SetRow(hintBlock, 1);
            Grid.SetColumn(hintBlock, 1);
            grid.Children.Add(hintBlock);
        }

        grid.MouseLeftButtonUp += (_, _) =>
        {
            var enabled = switchBox.IsChecked != true;
            switchBox.IsChecked = enabled;
            onChanged?.Invoke(enabled);
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
    public static Border CreateSection(UIElement content)
    {
        return new Border
        {
            Style = (Style)Application.Current.FindResource("SettingsFlatSection"),
            Padding = new Thickness(_metrics.SectionPadding),
            Margin = new Thickness(0, 0, 0, _metrics.SectionSpacing),
            BorderThickness = new Thickness(_metrics.SectionBorderThickness),
            Effect = _metrics.SectionShadow
                ? (System.Windows.Media.Effects.Effect)Application.Current.FindResource("PaneShadow")
                : null,
            Child = content
        };
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

    /// <summary>设置页主保存按钮。</summary>
    public static Button CreateSaveButton(string content = "保存设置")
    {
        return new Button
        {
            Content = content,
            Style = (Style)Application.Current.FindResource("ModernButton"),
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
            Padding = new Thickness(5, 3, 38, 3)
        };

        _txt = new TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = SettingsLayout.InputHeight,
            FontSize = SettingsLayout.BodyFontSize,
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
            _txt.Text = _realPassword;
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
