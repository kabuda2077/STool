using System;
using System.Windows;
using System.Windows.Markup;

namespace STool.Core;

public enum MotionDurationKind
{
    Enter,
    Exit,
    State
}

/// <summary>XAML-friendly motion token that respects the system animation preference.</summary>
[MarkupExtensionReturnType(typeof(Duration))]
public sealed class MotionDurationExtension : MarkupExtension
{
    public MotionDurationExtension()
    {
    }

    public MotionDurationExtension(MotionDurationKind kind)
    {
        Kind = kind;
    }

    public MotionDurationKind Kind { get; set; } = MotionDurationKind.State;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (MotionSettings.ShouldReduceMotion)
        {
            return new Duration(TimeSpan.Zero);
        }

        return Kind switch
        {
            MotionDurationKind.Enter => MotionSettings.EnterDuration,
            MotionDurationKind.Exit => MotionSettings.ExitDuration,
            _ => MotionSettings.StateDuration
        };
    }
}
