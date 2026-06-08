using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace STool.Core;

public static class ModernWindowChrome
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ModernWindowChrome),
            new PropertyMetadata(false, OnEnabledChanged));

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

        window.Loaded -= Window_Loaded;
        window.Loaded += Window_Loaded;
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        AttachTitleBar(window);
        AttachButton(window, "PART_MinimizeButton", (_, _) => window.WindowState = WindowState.Minimized);
        AttachButton(window, "PART_MaximizeButton", (_, _) => ToggleMaximize(window));
        AttachButton(window, "PART_CloseButton", (_, _) => window.Close());
        window.StateChanged -= Window_StateChanged;
        window.StateChanged += Window_StateChanged;
        UpdateMaximizeGlyph(window);
    }

    private static void AttachTitleBar(Window window)
    {
        if (window.Template.FindName("PART_TitleBar", window) is not FrameworkElement titleBar)
            return;

        titleBar.MouseLeftButtonDown -= TitleBar_MouseLeftButtonDown;
        titleBar.MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
    }

    private static void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement titleBar ||
            Window.GetWindow(titleBar) is not Window window)
        {
            return;
        }

        if (e.ClickCount == 2 && window.ResizeMode != ResizeMode.NoResize)
        {
            ToggleMaximize(window);
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try
        {
            window.DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse state changes during window activation.
        }
    }

    private static void AttachButton(Window window, string name, RoutedEventHandler handler)
    {
        if (window.Template.FindName(name, window) is not System.Windows.Controls.Primitives.ButtonBase button)
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

    private static void Window_StateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            UpdateMaximizeGlyph(window);
        }
    }

    private static void UpdateMaximizeGlyph(Window window)
    {
        if (window.Template.FindName("PART_MaximizeGlyph", window) is TextBlock glyph)
        {
            glyph.Text = window.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        if (window.Template.FindName("PART_MaximizeButton", window) is System.Windows.Controls.Button button)
        {
            button.ToolTip = window.WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }
}
