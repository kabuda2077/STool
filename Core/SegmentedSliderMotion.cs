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
        bool animate,
        Duration? duration = null,
        KeySpline? keySpline = null,
        bool animateLayoutWidth = false)
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
        slider.BeginAnimation(FrameworkElement.WidthProperty, null);

        slider.Width = targetWidth;
        translate.X = currentX;
        scale.ScaleX = animateLayoutWidth ? 1 : currentVisualWidth / targetWidth;

        if (!animate || MotionSettings.ShouldReduceMotion)
        {
            translate.X = targetX;
            scale.ScaleX = 1;
            return;
        }

        translate.BeginAnimation(
            TranslateTransform.XProperty,
            CreateAnimation(currentX, targetX, duration, keySpline),
            HandoffBehavior.SnapshotAndReplace);
        if (animateLayoutWidth)
        {
            slider.BeginAnimation(FrameworkElement.WidthProperty,
                CreateAnimation(currentVisualWidth, targetWidth, duration, keySpline),
                HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                CreateAnimation(scale.ScaleX, 1, duration, keySpline),
                HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static DoubleAnimationBase CreateAnimation(double from, double to, Duration? duration, KeySpline? keySpline)
    {
        if (keySpline == null)
            return new DoubleAnimation(from, to, duration ?? MotionSettings.StateDuration)
            {
                EasingFunction = MotionSettings.SoftEaseOut,
                FillBehavior = FillBehavior.HoldEnd
            };

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration ?? MotionSettings.StateDuration,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), keySpline));
        return animation;
    }

    private static bool IsUsable(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
