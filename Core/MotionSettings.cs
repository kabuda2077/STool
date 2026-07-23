using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace STool.Core;

internal static class MotionSettings
{
    public static readonly Duration EnterDuration = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration StateDuration = new(TimeSpan.FromMilliseconds(200));

    public static bool ShouldReduceMotion => !SystemParameters.ClientAreaAnimation;

    public static IEasingFunction EaseOut { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction EaseIn { get; } = new CubicEase { EasingMode = EasingMode.EaseIn };
    public static IEasingFunction SoftEaseOut { get; } = new SineEase { EasingMode = EasingMode.EaseOut };
}
