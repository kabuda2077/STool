using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace STool.Modules.Screenshot;

public partial class PinWindow : Window
{
    private const double ShadowPadding = 14;
    private readonly Bitmap _screenshot;

    public PinWindow(Bitmap screenshot) : this(screenshot, null) { }

    public PinWindow(Bitmap screenshot, Rect? targetDip)
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateTopmostButton();

        _screenshot = screenshot;
        screenshotImage.Source = BitmapInterop.ToBitmapSource(screenshot);

        WindowStartupLocation = WindowStartupLocation.Manual;

        if (targetDip is Rect r && r.Width >= 1 && r.Height >= 1)
        {
            // 在选区原位、原尺寸钉住(不再跳到屏幕中间)
            Left = r.X - ShadowPadding;
            Top = r.Y - ShadowPadding;
            Width = r.Width + ShadowPadding * 2;
            Height = r.Height + ShadowPadding * 2;
        }
        else
        {
            // 回退:屏幕居中
            Width = screenshot.Width + ShadowPadding * 2;
            Height = screenshot.Height + ShadowPadding * 2;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        }
    }

    private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)   // 双击关闭
        {
            Close();
            return;
        }
        DragMove();              // WPF 内置拖动,稳定不抖动
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        toolbar.Visibility = Visibility.Visible;
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        toolbar.Visibility = Visibility.Collapsed;
    }

    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl + 滚轮缩放(图片 Stretch=Uniform 填满窗口,故缩放窗口本身,保持原位左上角)
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            double factor = e.Delta > 0 ? 1.1 : 0.9;
            var newWidth = Width * factor;
            var newHeight = Height * factor;

            if (newWidth >= 60 && newWidth <= 4000)
            {
                Width = newWidth;
                Height = newHeight;
            }
            e.Handled = true;
        }
    }

    private void BtnTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdateTopmostButton();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateTopmostButton()
    {
        btnTopmost.ToolTip = Topmost ? "取消置顶" : "置顶窗口";

        if (btnTopmost.Template.FindName("pinIcon", btnTopmost) is System.Windows.Shapes.Path pinIcon)
        {
            pinIcon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            pinIcon.RenderTransform = new System.Windows.Media.RotateTransform(Topmost ? 0 : -45);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenshot?.Dispose();
        base.OnClosed(e);
    }
}
