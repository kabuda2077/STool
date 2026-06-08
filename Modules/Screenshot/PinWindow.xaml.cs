using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace STool.Modules.Screenshot;

public partial class PinWindow : Window
{
    private readonly Bitmap _screenshot;

    public PinWindow(Bitmap screenshot) : this(screenshot, null) { }

    public PinWindow(Bitmap screenshot, Rect? targetDip)
    {
        InitializeComponent();

        _screenshot = screenshot;
        screenshotImage.Source = BitmapToImageSource(screenshot);

        WindowStartupLocation = WindowStartupLocation.Manual;

        if (targetDip is Rect r && r.Width >= 1 && r.Height >= 1)
        {
            // 在选区原位、原尺寸钉住(不再跳到屏幕中间)
            Left = r.X;
            Top = r.Y;
            Width = r.Width;
            Height = r.Height;
        }
        else
        {
            // 回退:屏幕居中
            Width = screenshot.Width;
            Height = screenshot.Height;
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

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private BitmapSource BitmapToImageSource(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
        memory.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = memory;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        return bitmapImage;
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenshot?.Dispose();
        base.OnClosed(e);
    }
}
