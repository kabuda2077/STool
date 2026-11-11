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
            var edit = ResolveMaskedEdit(_realPassword, _pwd.Password, MaskPlaceholder);
            if (edit.RestoreMask || edit.ReplaceDisplay)
            {
                _isSyncing = true;
                _pwd.Password = edit.RestoreMask ? MaskPlaceholder : edit.Password;
                if (!edit.RestoreMask)
                    SetPasswordCaretToEnd(_pwd);
                _isSyncing = false;
            }

            if (edit.RestoreMask)
                return;

            _realPassword = edit.Password;
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

    internal readonly record struct MaskedEditResult(string Password, bool RestoreMask, bool ReplaceDisplay);

    /// <summary>
    /// 处理密码框里的一次编辑。已保存的密钥只显示为定长圆点，圆点不代表真实字符：
    /// 只删掉部分圆点视为无意义的编辑并恢复遮罩；全部删除才是清空；圆点旁输入的新字符视为覆盖为新密钥。
    /// </summary>
    internal static MaskedEditResult ResolveMaskedEdit(string realPassword, string displayed, string mask)
    {
        var masked = realPassword.Length > 0;
        if (!masked || displayed.IndexOf(mask[0]) < 0)
            return new MaskedEditResult(displayed, RestoreMask: false, ReplaceDisplay: false);

        var typed = displayed.Replace(mask[0].ToString(), string.Empty);
        if (typed.Length == 0)
        {
            // 仍是完整遮罩或只删了几个圆点：保留原密钥。
            return new MaskedEditResult(realPassword, RestoreMask: true, ReplaceDisplay: false);
        }

        return new MaskedEditResult(typed, RestoreMask: false, ReplaceDisplay: true);
    }

    private static void SetPasswordCaretToEnd(PasswordBox box)
    {
        // PasswordBox 没有公开 CaretIndex；用反射调用内部 Select 把光标放到末尾。
        typeof(PasswordBox)
            .GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(box, new object[] { box.Password.Length, 0 });
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
