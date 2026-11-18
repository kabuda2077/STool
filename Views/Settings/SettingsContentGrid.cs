using System.Windows;
using System.Windows.Controls;

namespace STool.Views.Settings;

/// <summary>浏览器 scrollHeight 按整 DIP 取整，半像素卡片仍保留自身的实际高度。</summary>
public sealed class SettingsContentGrid : Grid
{
    protected override Size MeasureOverride(Size constraint)
    {
        var content = base.MeasureOverride(constraint);
        return new Size(content.Width, Math.Ceiling(content.Height));
    }
}
