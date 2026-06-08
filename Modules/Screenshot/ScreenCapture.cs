using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace STool.Modules.Screenshot;

public static class ScreenCapture
{
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, CopyPixelOperation rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    /// <summary>
    /// 获取虚拟屏幕边界（包含所有显示器）
    /// </summary>
    public static Rectangle GetVirtualScreenBounds()
    {
        return SystemInformation.VirtualScreen;
    }

    /// <summary>
    /// 捕获指定区域的屏幕内容
    /// </summary>
    public static Bitmap CaptureRegion(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        return bitmap;
    }

    /// <summary>
    /// 捕获整个虚拟屏幕（所有显示器）
    /// </summary>
    public static Bitmap CaptureAllScreens()
    {
        var bounds = GetVirtualScreenBounds();
        return CaptureRegion(bounds);
    }

    /// <summary>
    /// 捕获主显示器
    /// </summary>
    public static Bitmap CapturePrimaryScreen()
    {
        var screen = Screen.PrimaryScreen;
        if (screen == null)
            throw new InvalidOperationException("No primary screen found");

        return CaptureRegion(screen.Bounds);
    }
}
