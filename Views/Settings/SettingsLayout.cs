using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace STool.Views.Settings;

/// <summary>设置面板共享的布局常量和 UI 工厂方法。</summary>
internal static class SettingsLayout
{
    // ── 间距 Token ──
    public const double SpacingXS = 4;
    public const double SpacingSM = 8;
    public const double SpacingMD = 12;

    /// <summary>行内字段 Label 列宽。</summary>
    public const double InlineLabelWidth = 90;

    /// <summary>快捷键行内字段 Label 列宽(标签较短)。</summary>
    public const double HotkeyLabelWidth = 70;

    /// <summary>输入控件统一高度。</summary>
    public const double InputHeight = 32;

    // ── 常用 Margin ──
    public static readonly Thickness FieldSpacing = new(0, 0, 0, SpacingSM);
    public static readonly Thickness HintMargin = new(0, 3, 0, 0);
    public static readonly Thickness InlineHintMargin = new(InlineLabelWidth, 3, 0, 0);
    public static readonly Thickness SaveButtonMargin = new(0, SpacingMD, 0, 0);

    // ── UI 工厂方法 ──

    /// <summary>创建 Label(左) + Input(右) 同行布局。</summary>
    public static Grid CreateInlineField(string label, FrameworkElement input,
        double labelWidth = InlineLabelWidth)
    {
        var grid = new Grid { Margin = FieldSpacing };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
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

    /// <summary>创建标准 TextBox。</summary>
    public static System.Windows.Controls.TextBox CreateTextBox()
    {
        return new System.Windows.Controls.TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = InputHeight,
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

    /// <summary>创建密码框 + 明文框 + 眼睛切换按钮的组合控件。</summary>
    public static (Grid host, PasswordBox pwd) CreatePasswordField()
    {
        var host = new Grid
        {
            Height = InputHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var pwd = new PasswordBox
        {
            Style = (Style)Application.Current.FindResource("SunkenPasswordBox"),
            Height = InputHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(5, 3, 38, 3)
        };

        var txt = new System.Windows.Controls.TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = InputHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(5, 3, 38, 3),
            Visibility = Visibility.Collapsed
        };

        var btn = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Width = 32, Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "显示密钥",
            Content = new TextBlock
            {
                Text = "\uE890",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush")
            }
        };

        var isRevealed = false;
        var isSyncing = false;

        pwd.PasswordChanged += (_, _) =>
        {
            if (isSyncing || isRevealed) return;
            isSyncing = true;
            txt.Text = pwd.Password;
            isSyncing = false;
        };

        txt.TextChanged += (_, _) =>
        {
            if (isSyncing || !isRevealed) return;
            isSyncing = true;
            pwd.Password = txt.Text;
            isSyncing = false;
        };

        btn.Click += (_, _) =>
        {
            isRevealed = !isRevealed;
            if (isRevealed)
            {
                txt.Text = pwd.Password;
                pwd.Visibility = Visibility.Collapsed;
                txt.Visibility = Visibility.Visible;
                btn.ToolTip = "隐藏密钥";
                txt.Focus();
                txt.CaretIndex = txt.Text.Length;
            }
            else
            {
                pwd.Password = txt.Text;
                txt.Visibility = Visibility.Collapsed;
                pwd.Visibility = Visibility.Visible;
                btn.ToolTip = "显示密钥";
                pwd.Focus();
            }
        };

        host.Children.Add(pwd);
        host.Children.Add(txt);
        host.Children.Add(btn);
        return (host, pwd);
    }

    /// <summary>创建提示文本。</summary>
    public static TextBlock CreateHint(string text, bool inline = false)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("HintText"),
            Margin = inline ? InlineHintMargin : HintMargin,
            FontSize = (double)Application.Current.FindResource("FontSizeMicro"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush"),
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap
        };
    }
}
