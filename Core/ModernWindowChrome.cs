using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace STool.Core;

/// <summary>
/// 为使用 ModernWindow 样式的窗口接管自定义标题栏行为。
/// 标题栏拖拽与双击最大化由 WindowChrome.CaptionHeight 交给系统处理;
/// 这里只负责 caption 按钮点击、最大化字形切换,以及 Win11 DWM 圆角(SingleBorderWindow 兜底)。
/// </summary>
public static class ModernWindowChrome
{
    public static readonly DependencyProperty ShowTopmostButtonProperty =
        DependencyProperty.RegisterAttached(
            "ShowTopmostButton",
            typeof(bool),
            typeof(ModernWindowChrome),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HideTitleTextProperty =
        DependencyProperty.RegisterAttached(
            "HideTitleText",
            typeof(bool),
            typeof(ModernWindowChrome),
            new PropertyMetadata(false));

    public static void SetHideTitleText(DependencyObject element, bool value)
    {
        element.SetValue(HideTitleTextProperty, value);
    }

    public static bool GetHideTitleText(DependencyObject element)
    {
        return (bool)element.GetValue(HideTitleTextProperty);
    }

    public static readonly DependencyProperty TitleBarExtraTopPaddingProperty =
        DependencyProperty.RegisterAttached(
            "TitleBarExtraTopPadding",
            typeof(bool),
            typeof(ModernWindowChrome),
            new PropertyMetadata(false));

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ModernWindowChrome),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetShowTopmostButton(DependencyObject element, bool value)
    {
        element.SetValue(ShowTopmostButtonProperty, value);
    }

    public static bool GetShowTopmostButton(DependencyObject element)
    {
        return (bool)element.GetValue(ShowTopmostButtonProperty);
    }

    public static void SetTitleBarExtraTopPadding(DependencyObject element, bool value)
    {
        element.SetValue(TitleBarExtraTopPaddingProperty, value);
    }

    public static bool GetTitleBarExtraTopPadding(DependencyObject element)
    {
        return (bool)element.GetValue(TitleBarExtraTopPaddingProperty);
    }

    public static void SetEnabled(DependencyObject element, bool value)
    {
        element.SetValue(EnabledProperty, value);
    }

    public static bool GetEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(EnabledProperty);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
            return;

        window.SourceInitialized -= Window_SourceInitialized;
        window.SourceInitialized += Window_SourceInitialized;
        window.Loaded -= Window_Loaded;
        window.Loaded += Window_Loaded;
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            TryRoundCorners(hwnd);
        }
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        AttachButton(window, "PART_MinimizeButton", (_, _) => window.WindowState = WindowState.Minimized);
        AttachButton(window, "PART_MaximizeButton", (_, _) => ToggleMaximize(window));
        AttachButton(window, "PART_CloseButton", (_, _) => window.Close());
        AttachButton(window, "PART_TopmostButton", (_, _) => ToggleTopmost(window));
        window.StateChanged -= Window_StateChanged;
        window.StateChanged += Window_StateChanged;
        UpdateMaximizeGlyph(window);
        UpdateTopmostGlyph(window);
    }

    private static void AttachButton(Window window, string name, RoutedEventHandler handler)
    {
        if (window.Template?.FindName(name, window) is not System.Windows.Controls.Primitives.ButtonBase button)
            return;

        button.Click -= handler;
        button.Click += handler;
    }

    private static void ToggleMaximize(Window window)
    {
        if (window.ResizeMode == ResizeMode.NoResize)
            return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static void ToggleTopmost(Window window)
    {
        window.Topmost = !window.Topmost;
        UpdateTopmostGlyph(window);
    }

    private static void Window_StateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            UpdateMaximizeGlyph(window);
        }
    }

    private static void UpdateMaximizeGlyph(Window window)
    {
        var isMaximized = window.WindowState == WindowState.Maximized;

        if (window.Template?.FindName("PART_MaximizeIcon", window) is System.Windows.Shapes.Path icon)
        {
            icon.Data = (System.Windows.Media.Geometry)window.FindResource(
                isMaximized ? "IconRestore" : "IconMaximize");
        }

        if (window.Template?.FindName("PART_MaximizeButton", window) is System.Windows.Controls.Button button)
        {
            button.ToolTip = isMaximized ? "还原" : "最大化";
        }
    }

    private static void UpdateTopmostGlyph(Window window)
    {
        if (window.Template?.FindName("PART_TopmostIcon", window) is System.Windows.Shapes.Path icon)
        {
            icon.RenderTransform = new System.Windows.Media.RotateTransform(window.Topmost ? 0 : -45);
        }

        if (window.Template?.FindName("PART_TopmostButton", window) is System.Windows.Controls.Button button)
        {
            button.ToolTip = window.Topmost ? "取消置顶" : "置顶窗口";
            button.Foreground = window.Topmost
                ? (System.Windows.Media.Brush)window.FindResource("PrimaryBrush")
                : (System.Windows.Media.Brush)window.FindResource("TextPrimaryBrush");
        }
    }

    // ---- Win11 圆角 ----
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // 旧版本 Windows 不支持该属性,忽略即可(降级为直角)。
        }
    }

    public static void TryHideBorder(IntPtr hwnd)
    {
        try
        {
            int color = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
        }
        catch
        {
            // 旧版本 Windows 不支持该属性,忽略即可。
        }
    }
}
