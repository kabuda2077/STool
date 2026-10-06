using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Serilog;

namespace STool.Modules.Screenshot;

/// <summary>
/// 一次屏幕抓取的结果。优先把像素抓进共享内存段：GDI+ 的 <see cref="Bitmap"/> 与 WPF 的
/// <see cref="Source"/> 读的是同一块内存，省掉多次整屏拷贝和常驻的第二份像素。
/// </summary>
public sealed class CapturedScreen : IDisposable
{
    private IntPtr _dib;
    private IntPtr _section;

    internal CapturedScreen(Bitmap bitmap, BitmapSource source, IntPtr dib, IntPtr section)
    {
        Bitmap = bitmap;
        Source = source;
        _dib = dib;
        _section = section;
    }

    /// <summary>供裁剪、取色和 OCR 使用的像素（32bpp，无 alpha）。</summary>
    public Bitmap Bitmap { get; }

    /// <summary>供界面显示与放大镜使用的位图，DPI 为 96，1 DIP 对应 1 像素。</summary>
    public BitmapSource Source { get; }

    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public void Dispose()
    {
        Bitmap.Dispose();
        if (_dib != IntPtr.Zero)
        {
            ScreenCapture.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        if (_section != IntPtr.Zero)
        {
            ScreenCapture.CloseHandle(_section);
            _section = IntPtr.Zero;
        }
    }
}

public static class ScreenCapture
{
    private const uint PageReadWrite = 0x04;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, CopyPixelOperation rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 获取虚拟屏幕边界（包含所有显示器）
    /// </summary>
    public static Rectangle GetVirtualScreenBounds()
    {
        return SystemInformation.VirtualScreen;
    }

    /// <summary>抓取指定物理像素区域。共享内存方式失败时回退到普通拷贝。</summary>
    public static CapturedScreen Capture(Rectangle bounds)
    {
        try
        {
            var shared = TryCaptureToSharedSection(bounds);
            if (shared != null)
                return shared;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[Capture] Shared-section capture failed; falling back to copied bitmap");
        }

        var bitmap = CaptureRegion(bounds);
        return new CapturedScreen(bitmap, BitmapInterop.ToBitmapSource(bitmap), IntPtr.Zero, IntPtr.Zero);
    }

    private static CapturedScreen? TryCaptureToSharedSection(Rectangle bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        var stride = checked(width * 4);
        var size = checked((long)stride * height);

        var section = CreateFileMapping(InvalidHandleValue, IntPtr.Zero, PageReadWrite, (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), null);
        if (section == IntPtr.Zero)
            return null;

        var screenDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var oldObject = IntPtr.Zero;
        Bitmap? bitmap = null;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException("Failed to get screen DC");

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create memory DC");

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // 负高度 = 自上而下的行序，与 GDI+ / WPF 的内存布局一致
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb
                }
            };
            dib = CreateDIBSection(screenDc, ref info, DibRgbColors, out var bits, section, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create DIB section");

            oldObject = SelectObject(memoryDc, dib);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, bounds.Left, bounds.Top, CopyPixelOperation.SourceCopy))
                throw new InvalidOperationException("Screen capture failed");
            GdiFlush();

            bitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, bits);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromMemorySection(
                section,
                width,
                height,
                System.Windows.Media.PixelFormats.Bgr32,
                stride,
                0);

            var captured = new CapturedScreen(bitmap, source, dib, section);
            dib = IntPtr.Zero;
            section = IntPtr.Zero;
            bitmap = null;
            return captured;
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, oldObject);
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);

            // 只有构造 CapturedScreen 失败时才会走到这里释放资源。
            bitmap?.Dispose();
            if (dib != IntPtr.Zero)
                DeleteObject(dib);
            if (section != IntPtr.Zero)
                CloseHandle(section);
        }
    }

    /// <summary>
    /// 捕获指定区域的屏幕内容（独立拷贝，调用方负责释放）。
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

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }
}
