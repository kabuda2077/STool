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
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            ModernWindowChrome.TryRoundCorners(hwnd);
            ModernWindowChrome.TryHideBorder(hwnd);
        };
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
        var brush = ((Brush)FindResource("BorderBrush")).Clone();
        brush.Opacity = 0.55;

        menuPanel.Children.Add(new Border
        {
            Height = 0.5,
            Background = brush,
            Margin = new Thickness(38, 2, 38, 2)
        });
    }

    public void AddItem(string label, string shortcut, Action onClick, TrayMenuIconKind icon, bool danger = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource(danger ? "TrayMenuItemDanger" : "TrayMenuItem")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBrush = (Brush)FindResource(danger ? "ErrorBrush" : "TextSecondaryBrush");
        var iconView = CreateIcon(icon, iconBrush);
        iconView.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(iconView, 0);
        grid.Children.Add(iconView);

        var text = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource(danger ? "ErrorBrush" : "TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
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
            Grid.SetColumn(shortcutText, 2);
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

    private static Viewbox CreateIcon(TrayMenuIconKind icon, Brush brush)
    {
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24
        };

        foreach (var data in GetIconData(icon))
        {
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = brush,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = System.Windows.Media.Brushes.Transparent
            });
        }

        return new Viewbox
        {
            Width = 18,
            Height = 18,
            Child = canvas,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static string[] GetIconData(TrayMenuIconKind icon) => icon switch
    {
        TrayMenuIconKind.Screenshot => new[]
        {
            "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z",
            "M8 8 H16 M8 12 H14 M8 16 H12"
        },
        TrayMenuIconKind.Translate => new[]
        {
            "M5 8 L11 14 M4 14 L10 8 L12 5 M2 5 H14 M7 2 H8",
            "M22 22 L17 12 L12 22 M14 18 H20"
        },
        TrayMenuIconKind.Clipboard => new[]
        {
            "M8 4 H16 A2 2 0 0 1 18 6 V20 A2 2 0 0 1 16 22 H8 A2 2 0 0 1 6 20 V6 A2 2 0 0 1 8 4 Z",
            "M9 4 A3 3 0 0 1 15 4 M9 10 H15 M9 14 H14"
        },
        TrayMenuIconKind.Settings => new[]
        {
            "M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 Z",
            "M12 2 V4 M12 20 V22 M4.93 4.93 L6.34 6.34 M17.66 17.66 L19.07 19.07 M2 12 H4 M20 12 H22 M4.93 19.07 L6.34 17.66 M17.66 6.34 L19.07 4.93"
        },
        TrayMenuIconKind.Exit => new[]
        {
            "M18 6 L6 18 M6 6 L18 18"
        },
        _ => Array.Empty<string>()
    };

    public void ShowNearCursor()
    {
        Loaded += (_, _) =>
        {
            PositionNearCursor();
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
        double left = cursorX - ActualWidth;
        double top = cursorY - ActualHeight;

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
