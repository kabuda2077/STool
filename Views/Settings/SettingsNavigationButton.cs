using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using STool.Core;

namespace STool.Views.Settings;

/// <summary>导航文字只保留当前一次颜色过渡，快速切换时从正在显示的颜色继续。</summary>
public sealed class SettingsNavigationButton : Button
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        UpdateForeground(animate: false);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (IsInitialized && (e.Property == NavigationSelection.IsSelectedProperty || e.Property == IsMouseOverProperty))
            UpdateForeground(animate: IsLoaded);
    }

    private void UpdateForeground(bool animate)
    {
        var key = NavigationSelection.GetIsSelected(this) ? "SettingsForegroundBrush"
            : IsMouseOver ? "SettingsSecondaryBrush" : "SettingsControlBrush";
        var target = ((SolidColorBrush)FindResource(key)).Color;
        var brush = new SolidColorBrush((Foreground as SolidColorBrush)?.Color ?? target);
        SetCurrentValue(ForegroundProperty, brush);
        if (!animate || MotionSettings.ShouldReduceMotion) { brush.Color = target; return; }
        var transition = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180) };
        transition.KeyFrames.Add(new SplineColorKeyFrame(target, KeyTime.FromPercent(1), new KeySpline(.2, .8, .2, 1)));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, transition);
    }
}
