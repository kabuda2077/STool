using System;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace STool.Modules.Screenshot;

public class ScreenshotResult
{
    public Bitmap Bitmap { get; set; } = null!;
    public System.Drawing.Rectangle Bounds { get; set; }
}

public partial class CaptureOverlay : Window
{
    private System.Windows.Point _startPoint;
    private bool _isSelecting;
    private Bitmap? _screenshot;
    private System.Drawing.Rectangle _selectedRegion;

    public event EventHandler<ScreenshotResult>? SelectionCompleted;
    public event EventHandler? SelectionCancelled;

    public CaptureOverlay()
    {
        InitializeComponent();

        // Window coordinates are WPF device-independent pixels; capture bounds are physical pixels.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 捕获屏幕
        _screenshot = ScreenCapture.CaptureAllScreens();

        Cursor = System.Windows.Input.Cursors.Cross;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _startPoint = e.GetPosition(canvas);
        _isSelecting = true;

        selectionRect.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(selectionRect, _startPoint.X);
        System.Windows.Controls.Canvas.SetTop(selectionRect, _startPoint.Y);
        selectionRect.Width = 0;
        selectionRect.Height = 0;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isSelecting)
            return;

        var currentPoint = e.GetPosition(canvas);

        var left = Math.Min(_startPoint.X, currentPoint.X);
        var top = Math.Min(_startPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _startPoint.X);
        var height = Math.Abs(currentPoint.Y - _startPoint.Y);

        System.Windows.Controls.Canvas.SetLeft(selectionRect, left);
        System.Windows.Controls.Canvas.SetTop(selectionRect, top);
        selectionRect.Width = width;
        selectionRect.Height = height;

        // 更新尺寸提示
        if (width > 0 && height > 0)
        {
            var pixelSize = ToPhysicalSize(width, height);
            sizeText.Text = $"{pixelSize.Width} x {pixelSize.Height}";
            sizeLabel.Visibility = Visibility.Visible;

            // 定位尺寸标签在矩形上方
            System.Windows.Controls.Canvas.SetLeft(sizeLabel, left);
            System.Windows.Controls.Canvas.SetTop(sizeLabel, top - 30);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_isSelecting)
            return;

        _isSelecting = false;

        var width = selectionRect.Width;
        var height = selectionRect.Height;

        if (width < 5 || height < 5)
        {
            // 选区太小，取消
            Cancel();
            return;
        }

        // Convert the selected WPF DIP rect to physical screen pixels for GDI capture.
        var left = System.Windows.Controls.Canvas.GetLeft(selectionRect) + Left;
        var top = System.Windows.Controls.Canvas.GetTop(selectionRect) + Top;
        var physicalRect = ToPhysicalRect(left, top, width, height);

        _selectedRegion = new System.Drawing.Rectangle(
            physicalRect.X,
            physicalRect.Y,
            physicalRect.Width,
            physicalRect.Height
        );

        // 裁剪选区
        var croppedBitmap = CropBitmap(_screenshot!, _selectedRegion);

        var result = new ScreenshotResult
        {
            Bitmap = croppedBitmap,
            Bounds = _selectedRegion
        };

        SelectionCompleted?.Invoke(this, result);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
        }
    }

    private void Cancel()
    {
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private Bitmap CropBitmap(Bitmap source, System.Drawing.Rectangle cropArea)
    {
        // 调整裁剪区域以适应源图像边界
        var bounds = ScreenCapture.GetVirtualScreenBounds();
        var x = cropArea.X - bounds.X;
        var y = cropArea.Y - bounds.Y;
        var width = Math.Min(cropArea.Width, source.Width - x);
        var height = Math.Min(cropArea.Height, source.Height - y);

        if (x < 0 || y < 0 || width <= 0 || height <= 0)
            return source;

        var cropRect = new System.Drawing.Rectangle(x, y, width, height);
        return source.Clone(cropRect, source.PixelFormat);
    }

    private System.Drawing.Rectangle ToPhysicalRect(double left, double top, double width, double height)
    {
        var transform = GetTransformToDevice();
        return new System.Drawing.Rectangle(
            (int)Math.Round(left * transform.M11),
            (int)Math.Round(top * transform.M22),
            Math.Max(1, (int)Math.Round(width * transform.M11)),
            Math.Max(1, (int)Math.Round(height * transform.M22))
        );
    }

    private System.Drawing.Size ToPhysicalSize(double width, double height)
    {
        var transform = GetTransformToDevice();
        return new System.Drawing.Size(
            Math.Max(1, (int)Math.Round(width * transform.M11)),
            Math.Max(1, (int)Math.Round(height * transform.M22))
        );
    }

    private Matrix GetTransformToDevice()
    {
        return PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? Matrix.Identity;
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenshot?.Dispose();
        base.OnClosed(e);
    }
}
