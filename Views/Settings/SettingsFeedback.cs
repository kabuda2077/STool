using System.Windows;

namespace STool.Views.Settings;

/// <summary>设置页内的保存状态，向窗口冒泡，避免高频成功提示打断操作。</summary>
internal static class SettingsFeedback
{
    public static readonly RoutedEvent ChangedEvent = EventManager.RegisterRoutedEvent(
        "Changed", RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<string>), typeof(SettingsFeedback));

    public static void Report(FrameworkElement owner, string message = "已保存") =>
        owner.RaiseEvent(new RoutedPropertyChangedEventArgs<string>(string.Empty, message, ChangedEvent));
}
