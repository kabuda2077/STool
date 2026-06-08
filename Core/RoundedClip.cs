using System.Windows;
using System.Windows.Media;

namespace STool.Core;

public static class RoundedClip
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(RoundedClip),
            new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(RoundedClip),
            new PropertyMetadata(0.0, OnRadiusChanged));

    public static void SetEnabled(DependencyObject element, bool value)
    {
        element.SetValue(EnabledProperty, value);
    }

    public static bool GetEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(EnabledProperty);
    }

    public static void SetRadius(DependencyObject element, double value)
    {
        element.SetValue(RadiusProperty, value);
    }

    public static double GetRadius(DependencyObject element)
    {
        return (double)element.GetValue(RadiusProperty);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.SizeChanged -= Element_SizeChanged;

        if (e.NewValue is true)
        {
            element.SizeChanged += Element_SizeChanged;
            UpdateClip(element);
        }
        else
        {
            element.Clip = null;
        }
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && GetEnabled(element))
        {
            UpdateClip(element);
        }
    }

    private static void Element_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            UpdateClip(element);
        }
    }

    private static void UpdateClip(FrameworkElement element)
    {
        var radius = GetRadius(element);
        element.Clip = new RectangleGeometry(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight),
            radius,
            radius);
    }
}
