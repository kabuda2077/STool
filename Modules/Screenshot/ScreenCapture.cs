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
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("Failed to get screen DC");

        var memoryDc = IntPtr.Zero;
        var bitmapHandle = IntPtr.Zero;
        var oldObject = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create memory DC");

            bitmapHandle = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (bitmapHandle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create capture bitmap");

            oldObject = SelectObject(memoryDc, bitmapHandle);

            if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.Left, bounds.Top, CopyPixelOperation.SourceCopy))
                throw new InvalidOperationException("Screen capture failed");

            using var captured = Image.FromHbitmap(bitmapHandle);
            return new Bitmap(captured);
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, oldObject);
            if (bitmapHandle != IntPtr.Zero)
                DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
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
