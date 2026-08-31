using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板管理器
/// </summary>
public class ClipboardManager : IDisposable
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private const int ClipboardWriteAttempts = 6;
    private const int ClipboardRetryDelayMs = 50;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardStorage _storage;
    private readonly ConfigManager _configManager;

    public event EventHandler<ClipboardItem>? ItemAdded;

    public ClipboardManager(ConfigManager configManager)
    {
        _configManager = configManager;
        _monitor = new ClipboardMonitor();
        _storage = new ClipboardStorage();

        _monitor.ClipboardChanged += OnClipboardChanged;
    }

    public void Start()
    {
        var config = _configManager.Get().Clipboard;
        if (!config.Enabled)
        {
            Log.Information("Clipboard monitoring is disabled");
            return;
        }

        _monitor.Start();

        // 定期清理旧条目
        CleanOldEntries();
    }

    public void Stop()
    {
        _monitor.Stop();
    }

    private void OnClipboardChanged(object? sender, ClipboardItem item)
    {
        try
        {
            var config = _configManager.Get().Clipboard;

            // 检查图片大小限制
            if (item.Type == ClipboardItemType.Image && !string.IsNullOrEmpty(item.ImagePath))
            {
                var fileInfo = new System.IO.FileInfo(item.ImagePath);
                var sizeKB = fileInfo.Length / 1024;

                if (sizeKB > config.MaxImageSizeKB)
                {
                    Log.Information($"Skipping clipboard image (size {sizeKB}KB exceeds limit {config.MaxImageSizeKB}KB)");
                    TryDeleteFile(item.ImagePath);
                    return;
                }
            }

            // 去重:与最近一条相同则跳过
            if (IsDuplicateOfLatest(item))
            {
                if (item.Type == ClipboardItemType.Image && !string.IsNullOrEmpty(item.ImagePath))
                    TryDeleteFile(item.ImagePath);
                return;
            }

            _storage.Add(item);
            ItemAdded?.Invoke(this, item);

            Log.Information($"Clipboard item captured: {item.Type}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save clipboard item");
        }
    }

    /// <summary>
    /// 与最近一条记录内容相同则视为重复。
    /// 单次复制常触发多次 WM_CLIPBOARDUPDATE(程序分多步写入多种格式),据此去重避免重复入库。
    /// </summary>
    private bool IsDuplicateOfLatest(ClipboardItem item)
    {
        var recent = _storage.GetRecent(1);
        if (recent.Count == 0)
            return false;

        var last = recent[0];
        if (last.Type != item.Type)
            return false;

        return item.Type switch
        {
            ClipboardItemType.Text => last.TextContent == item.TextContent,
            ClipboardItemType.File => (last.FilePaths ?? Array.Empty<string>())
                .SequenceEqual(item.FilePaths ?? Array.Empty<string>()),
            ClipboardItemType.Image => IsSameImage(last, item),
            _ => false
        };
    }

    private static bool IsSameImage(ClipboardItem a, ClipboardItem b)
    {
        // 近时间窗(2s)内、字节大小相同视为同一次复制的重复
        try
        {
            if (string.IsNullOrEmpty(a.ImagePath) || string.IsNullOrEmpty(b.ImagePath))
                return false;
            if (Math.Abs((b.CreatedAt - a.CreatedAt).TotalSeconds) > 2)
                return false;
            var fa = new System.IO.FileInfo(a.ImagePath);
            var fb = new System.IO.FileInfo(b.ImagePath);
            return fa.Exists && fb.Exists && fa.Length == fb.Length;
        }
        catch
        {
            return false;
        }
    }

    public List<ClipboardItem> GetRecent(int count = 100)
    {
        return _storage.GetRecent(count);
    }

    public List<ClipboardItem> Search(string keyword, int limit = 100)
    {
        return _storage.Search(keyword, limit);
    }

    public List<ClipboardItem> GetFavorites()
    {
        return _storage.GetFavorites();
    }

    public void ToggleFavorite(string id)
    {
        _storage.ToggleFavorite(id);
    }

    public void Delete(string id)
    {
        _storage.Delete(id);
    }

    public void ClearAll()
    {
        _storage.ClearAll();
    }

    public void ClearByType(ClipboardItemType type)
    {
        _storage.ClearByType(type);
    }

    public void ClearFavorites()
    {
        _storage.ClearFavorites();
    }

    public async Task RestoreToClipboardAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            var restored = false;
            switch (item.Type)
            {
                case ClipboardItemType.Text:
                    if (!string.IsNullOrEmpty(item.TextContent))
                    {
                        await SetClipboardWithoutRecaptureAsync(
                            () => System.Windows.Clipboard.SetText(item.TextContent),
                            cancellationToken);
                        restored = true;
                    }
                    break;

                case ClipboardItemType.Image:
                    if (!string.IsNullOrEmpty(item.ImagePath) && System.IO.File.Exists(item.ImagePath))
                    {
                        using var bitmap = new System.Drawing.Bitmap(item.ImagePath);
                        var hBitmap = bitmap.GetHbitmap();
                        try
                        {
                            var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap,
                                IntPtr.Zero,
                                System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()
                            );
                            bitmapSource.Freeze();

                            await SetClipboardWithoutRecaptureAsync(
                                () => System.Windows.Clipboard.SetImage(bitmapSource),
                                cancellationToken);
                            restored = true;
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                    break;

                case ClipboardItemType.File:
                    if (item.FilePaths != null && item.FilePaths.Length > 0)
                    {
                        var fileDropList = new System.Collections.Specialized.StringCollection();
                        fileDropList.AddRange(item.FilePaths);
                        await SetClipboardWithoutRecaptureAsync(
                            () => System.Windows.Clipboard.SetFileDropList(fileDropList),
                            cancellationToken);
                        restored = true;
                    }
                    break;
            }

            if (!restored)
                throw new InvalidOperationException("剪贴板记录内容已不可用。");

            Log.Information("Restored clipboard item: {ItemId}", item.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore clipboard item {ItemId}", item.Id);
            throw;
        }
    }

    private Task SetClipboardWithoutRecaptureAsync(
        Action setClipboard,
        CancellationToken cancellationToken)
    {
        return SetClipboardWithRetryAsync(() =>
        {
            _monitor.BeginUpdateSuppression();
            try
            {
                setClipboard();
                _monitor.CompleteUpdateSuppression();
            }
            catch
            {
                _monitor.CancelUpdateSuppression();
                throw;
            }
        }, cancellationToken);
    }

    internal static async Task SetClipboardWithRetryAsync(
        Action setClipboard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setClipboard);

        for (var attempt = 1; attempt <= ClipboardWriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                setClipboard();
                return;
            }
            catch (COMException ex) when (
                ex.HResult == ClipboardBusyHResult && attempt < ClipboardWriteAttempts)
            {
                await Task.Delay(ClipboardRetryDelayMs, cancellationToken);
            }
        }
    }

    private void CleanOldEntries()
    {
        try
        {
            var config = _configManager.Get().Clipboard;
            _storage.CleanOldEntries(config.RetentionDays, config.MaxEntries);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clean old clipboard entries");
        }
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        _storage?.Dispose();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"Failed to delete clipboard image: {path}");
        }
    }
}
