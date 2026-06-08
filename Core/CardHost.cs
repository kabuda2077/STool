using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;

namespace STool.Core;

/// <summary>
/// 把内容包成"阴影层 + 内容层"两层卡片。
/// WPF 的 Effect(DropShadowEffect)会把元素连同内部文字渲染进离屏位图,
/// 高 DPI 下被缩放导致文字锯齿/笔画不均。把阴影投到空白投影层、文字留在无特效的内容层,
/// 文字即按原生 DPI 渲染、保持清晰。
/// </summary>
public static class CardHost
{
    public static Grid Wrap(UIElement content, double cornerRadius = 10, Thickness? padding = null, Thickness? margin = null)
    {
        var radius = new CornerRadius(cornerRadius);
        var surface = (Brush)Application.Current.FindResource("SurfaceBrush");
        var shadow = (Effect)Application.Current.FindResource("Elevation1");

        // 投影层:仅纯色 + 阴影,无文字,缩放不影响清晰度
        var caster = new Border
        {
            Background = surface,
            CornerRadius = radius,
            Effect = shadow
        };

        // 内容层:无特效,文字按原生 DPI 渲染
        var body = new Border
        {
            Background = surface,
            CornerRadius = radius,
            Padding = padding ?? new Thickness(16),
            Child = content
        };

        var grid = new Grid { Margin = margin ?? new Thickness(0, 0, 0, 14) };
        grid.Children.Add(caster);
        grid.Children.Add(body);
        return grid;
    }
}
