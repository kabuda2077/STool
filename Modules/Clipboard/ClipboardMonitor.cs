using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Serilog;
using STool.Core;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板监听器
/// </summary>
public class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    private readonly Window _listenerWindow;
    private HwndSource? _hwndSource;
    private bool _isMonitoring;
    private bool _suppressNextUpdate;

    public event EventHandler<ClipboardItem>? ClipboardChanged;

    public ClipboardMonitor()
    {
        // 创建一个隐藏窗口用于接收剪贴板消息
        _listenerWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Visibility = Visibility.Hidden
        };

        _listenerWindow.Show();
        _listenerWindow.Hide();
    }

    public void Start()
    {
        if (_isMonitoring)
            return;

        var windowHandle = new WindowInteropHelper(_listenerWindow).Handle;
        _hwndSource = HwndSource.FromHwnd(windowHandle);

        if (_hwndSource != null)
        {
            _hwndSource.AddHook(WndProc);
            AddClipboardFormatListener(windowHandle);
            _isMonitoring = true;
            Log.Information("Clipboard monitoring started");
        }
    }

    public void Stop()
    {
        if (!_isMonitoring)
            return;

        var windowHandle = new WindowInteropHelper(_listenerWindow).Handle;
        RemoveClipboardFormatListener(windowHandle);

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
        }

        _isMonitoring = false;
        Log.Information("Clipboard monitoring stopped");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdate();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        try
        {
            if (_suppressNextUpdate)
            {
                _suppressNextUpdate = false;
                return;
            }

            var item = CaptureClipboardContent();
            if (item != null)
            {
                ClipboardChanged?.Invoke(this, item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to capture clipboard content");
        }
    }

    public void SuppressNextUpdate()
    {
        _suppressNextUpdate = true;
    }

    private ClipboardItem? CaptureClipboardContent()
    {
        // 复制发生时,前台窗口通常就是来源应用
        var sourceApp = GetForegroundApp();

        if (System.Windows.Clipboard.ContainsText())
        {
            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return new ClipboardItem
            {
                Type = ClipboardItemType.Text,
                TextContent = text,
                SourceApp = sourceApp
            };
        }
        else if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image == null)
                return null;

            if (IsVisuallyBlankImage(image))
            {
                Log.Information("Skipping visually blank clipboard image from {SourceApp}", sourceApp ?? "unknown");
                return null;
            }

            // 保存图片到本地
            var imagePath = SaveClipboardImage(image);
            if (imagePath == null)
                return null;

            return new ClipboardItem
            {
                Type = ClipboardItemType.Image,
                ImagePath = imagePath,
                SourceApp = sourceApp
            };
        }
        else if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var files = System.Windows.Clipboard.GetFileDropList();
            if (files == null || files.Count == 0)
                return null;

            var filePaths = files.Cast<string>().ToArray();

            return new ClipboardItem
            {
                Type = ClipboardItemType.File,
                FilePaths = filePaths,
                SourceApp = sourceApp
            };
        }

        return null;
    }

    /// <summary>抓取当前前台窗口所属进程名(如 Code.exe),失败返回 null。</summary>
    private static string? GetForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0)
                return null;

            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            var name = proc.ProcessName;
            return string.IsNullOrEmpty(name) ? null : name + ".exe";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVisuallyBlankImage(BitmapSource image)
    {
        try
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return true;

            const int maxSampleSize = 64;
            var scale = Math.Min(1.0, (double)maxSampleSize / Math.Max(image.PixelWidth, image.PixelHeight));
            BitmapSource sample = image;

            if (scale < 1.0)
            {
                sample = new TransformedBitmap(image, new ScaleTransform(scale, scale));
                sample.Freeze();
            }

            var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
                return true;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var opaquePixels = 0;
            var nonWhitePixels = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];

                if (a <= 8)
                    continue;

                opaquePixels++;
                if (r < 244 || g < 244 || b < 244)
                    nonWhitePixels++;
            }

            if (opaquePixels == 0)
                return true;

            return nonWhitePixels <= 3 || (double)nonWhitePixels / opaquePixels < 0.001;
        }
        catch
        {
            return false;
        }
    }

    private string? SaveClipboardImage(BitmapSource image)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ClipboardImagesDirectory);

            var fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
            var filePath = Path.Combine(AppPaths.ClipboardImagesDirectory, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var fileStream = new FileStream(filePath, FileMode.Create);
            encoder.Save(fileStream);

            return filePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save clipboard image");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _listenerWindow?.Close();
    }
}
