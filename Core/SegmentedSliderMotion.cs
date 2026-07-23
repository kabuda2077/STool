using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace STool.Core;

internal static class SegmentedSliderMotion
{
    public static void MoveTo(
        FrameworkElement slider,
        TranslateTransform translate,
        ScaleTransform scale,
        double targetX,
        double targetWidth,
        bool animate)
    {
        if (!IsUsable(targetX) || !IsUsable(targetWidth) || targetWidth <= 0)
        {
            return;
        }

        var currentX = translate.X;
        var currentScale = IsUsable(scale.ScaleX) && scale.ScaleX > 0 ? scale.ScaleX : 1;
        var currentLayoutWidth = slider.ActualWidth > 0 ? slider.ActualWidth : slider.Width;
        if (!IsUsable(currentLayoutWidth) || currentLayoutWidth <= 0)
        {
            currentLayoutWidth = targetWidth;
        }

        var currentVisualWidth = currentLayoutWidth * currentScale;
        if (!IsUsable(currentVisualWidth) || currentVisualWidth <= 0)
        {
            currentVisualWidth = targetWidth;
        }

        translate.BeginAnimation(TranslateTransform.XProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);

        slider.Width = targetWidth;
        translate.X = currentX;
        scale.ScaleX = currentVisualWidth / targetWidth;

        if (!animate || MotionSettings.ShouldReduceMotion)
        {
            translate.X = targetX;
            scale.ScaleX = 1;
            return;
        }

        var xAnimation = new DoubleAnimation(currentX, targetX, MotionSettings.StateDuration)
        {
            EasingFunction = MotionSettings.SoftEaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };

        var scaleAnimation = new DoubleAnimation(scale.ScaleX, 1, MotionSettings.StateDuration)
        {
            EasingFunction = MotionSettings.SoftEaseOut,
            FillBehavior = FillBehavior.HoldEnd
        };

        translate.BeginAnimation(
            TranslateTransform.XProperty,
            xAnimation,
            HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            scaleAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static bool IsUsable(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
