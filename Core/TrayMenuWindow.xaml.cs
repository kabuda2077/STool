using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// 项目同时启用 WPF 与 WinForms,以下类型存在歧义,显式指向 WPF 版本。
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace STool.Core;

/// <summary>
/// 托盘右键菜单 —— 自定义 WPF 弹窗,圆角卡片 + 柔和阴影,统一全局设计风格。
/// 纯文字菜单项 + 右对齐快捷键,失焦/Esc/点击后自动关闭。
/// </summary>
public partial class TrayMenuWindow : Window
{
    private bool _closed;

    public TrayMenuWindow()
    {
        InitializeComponent();
        // 注意:失焦关闭(Deactivated)在 ShowNearCursor 激活之后才挂载,
        // 避免 Show() 到激活之间的瞬时失焦导致菜单"闪一下就关"。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                CloseMenu();
        };
    }

    public void AddSeparator()
    {
        menuPanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("BorderBrush"),
            Margin = new Thickness(6, 4, 6, 4)
        });
    }

    public void AddItem(string label, string shortcut, Action onClick, bool danger = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource(danger ? "TrayMenuItemDanger" : "TrayMenuItem")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (!string.IsNullOrEmpty(shortcut))
        {
            var shortcutText = new TextBlock
            {
                Text = shortcut,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            Grid.SetColumn(shortcutText, 1);
            grid.Children.Add(shortcutText);
        }

        button.Content = grid;
        button.Click += (_, _) =>
        {
            CloseMenu();
            onClick?.Invoke();
        };
        menuPanel.Children.Add(button);
    }

    public void ShowNearCursor()
    {
        Loaded += (_, _) =>
        {
            PositionNearCursor();
            Opacity = 1;
            Activate();
            // 菜单已显示并激活,现在才允许失焦自动关闭。
            Deactivated += (_, _) => CloseMenu();
        };
        Show();
    }

    private void PositionNearCursor()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;

        double cursorX = cursor.X / dpi.DpiScaleX;
        double cursorY = cursor.Y / dpi.DpiScaleY;
        double workLeft = work.Left / dpi.DpiScaleX;
        double workTop = work.Top / dpi.DpiScaleY;
        double workRight = work.Right / dpi.DpiScaleX;
        double workBottom = work.Bottom / dpi.DpiScaleY;

        // 锚定到光标的左上方(托盘通常在右下角),弹窗向上展开。
        // +14 抵消用于投影的透明外边距,让可见卡片贴近光标。
        double left = cursorX - ActualWidth + 14;
        double top = cursorY - ActualHeight + 14;

        if (left + ActualWidth > workRight) left = workRight - ActualWidth;
        if (left < workLeft) left = workLeft;
        if (top + ActualHeight > workBottom) top = workBottom - ActualHeight;
        if (top < workTop) top = workTop;

        Left = left;
        Top = top;
    }

    private void CloseMenu()
    {
        if (_closed)
            return;
        _closed = true;
        Close();
    }
}
