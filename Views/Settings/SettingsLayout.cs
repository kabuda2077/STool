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

    /// <summary>创建 Label(左) + [Input + Hint](右) 同行布局，使提示紧贴输入框并对其对齐。</summary>
    public static Grid CreateInlineFieldWithHint(string label, FrameworkElement input, string hint,
        double labelWidth = InlineLabelWidth)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        panel.Children.Add(input);

        var hintBlock = CreateHint(hint, inline: false);
        hintBlock.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(hintBlock);

        return CreateInlineField(label, panel, labelWidth);
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

    /// <summary>创建安全密码框组合控件。</summary>
    public static SecurePasswordField CreatePasswordField()
    {
        return new SecurePasswordField();
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

/// <summary>安全密码框组件，支持密文定长遮罩与显示/隐藏状态切换。</summary>
public class SecurePasswordField : Grid
{
    private readonly PasswordBox _pwd;
    private readonly TextBox _txt;
    private readonly Button _btn;
    private readonly TextBlock _iconBlock;

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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(5, 3, 38, 3)
        };

        _txt = new TextBox
        {
            Style = (Style)Application.Current.FindResource("SunkenTextBox"),
            Height = SettingsLayout.InputHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(5, 3, 38, 3),
            Visibility = Visibility.Collapsed
        };

        _iconBlock = new TextBlock
        {
            Text = "\uE72E", // 锁闭
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush")
        };

        _btn = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "显示密钥",
            Content = _iconBlock
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
            _iconBlock.Text = "\uE785"; // 锁开
            _txt.Focus();
            _txt.CaretIndex = _txt.Text.Length;
        }
        else
        {
            _pwd.Password = string.IsNullOrEmpty(_realPassword) ? "" : MaskPlaceholder;
            _txt.Visibility = Visibility.Collapsed;
            _pwd.Visibility = Visibility.Visible;
            _btn.ToolTip = "显示密钥";
            _iconBlock.Text = "\uE72E"; // 锁闭
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
