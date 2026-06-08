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
    private double _currentScale = 1.0;

    public PinWindow(Bitmap screenshot)
    {
        InitializeComponent();

        _screenshot = screenshot;

        // 显示截图
        screenshotImage.Source = BitmapToImageSource(screenshot);

        // 设置初始位置（屏幕中心）
        Left = (SystemParameters.PrimaryScreenWidth - screenshot.Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - screenshot.Height) / 2;
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
        // Ctrl + 滚轮缩放
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            double scaleFactor = e.Delta > 0 ? 1.1 : 0.9;
            _currentScale *= scaleFactor;

            // 限制缩放范围
            _currentScale = Math.Max(0.1, Math.Min(_currentScale, 5.0));

            // 应用缩放
            var newWidth = _screenshot.Width * _currentScale;
            var newHeight = _screenshot.Height * _currentScale;

            screenshotImage.Width = newWidth;
            screenshotImage.Height = newHeight;

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
