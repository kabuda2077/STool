using System.Windows;
using System.Windows.Markup;
using STool.Core;

namespace STool.Views.Settings;

/// <summary>设置页输入框的占位文字，由共用模板显示。</summary>
public static class SettingsInput
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(SettingsInput), new PropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);
}

/// <summary>原型各控件的动画时长，遵循 Windows 的减少动画偏好。</summary>
[MarkupExtensionReturnType(typeof(Duration))]
public sealed class SettingsMotionExtension(double milliseconds = 220) : MarkupExtension
{
    public double Milliseconds { get; set; } = milliseconds;

    public override object ProvideValue(IServiceProvider serviceProvider) => new Duration(
        MotionSettings.ShouldReduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Milliseconds));
}

/// <summary>白色列表裁切展开编辑区和悬停背景，同时保留外边框的圆角。</summary>
public static class SettingsRoundedClip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SettingsRoundedClip), new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var border = (System.Windows.Controls.Border)sender;
        if ((bool)args.NewValue)
        {
            border.SizeChanged += UpdateClip;
            border.Loaded += UpdateClip;
        }
        else
        {
            border.SizeChanged -= UpdateClip;
            border.Loaded -= UpdateClip;
            border.Clip = null;
            if (border.Child is { } child) child.Clip = null;
        }
    }

    private static void UpdateClip(object sender, RoutedEventArgs args)
    {
        var border = (System.Windows.Controls.Border)sender;
        border.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight),
            border.CornerRadius.TopLeft, border.CornerRadius.TopLeft);
        if (border.Child is { } child)
        {
            var radius = Math.Max(0, border.CornerRadius.TopLeft -
                Math.Max(border.BorderThickness.Left, border.BorderThickness.Top));
            child.Clip = new System.Windows.Media.RectangleGeometry(new Rect(child.RenderSize), radius, radius);
        }
    }
}
