using System.Windows;

namespace STool.Views;

/// <summary>导航按钮的选中状态。用附加属性表达，不再借用 Tag 存放 "Selected" 字符串。</summary>
public static class NavigationSelection
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected",
        typeof(bool),
        typeof(NavigationSelection),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
}
