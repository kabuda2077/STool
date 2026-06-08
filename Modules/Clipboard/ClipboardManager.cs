using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Serilog;
using STool.Core;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板管理器
/// </summary>
public class ClipboardManager : IDisposable
{
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

            _storage.Add(item);
            ItemAdded?.Invoke(this, item);

            Log.Information($"Clipboard item captured: {item.Type}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save clipboard item");
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

    public void RestoreToClipboard(ClipboardItem item)
    {
        try
        {
            switch (item.Type)
            {
                case ClipboardItemType.Text:
                    if (!string.IsNullOrEmpty(item.TextContent))
                    {
                        _monitor.SuppressNextUpdate();
                        System.Windows.Clipboard.SetText(item.TextContent);
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

                            _monitor.SuppressNextUpdate();
                            System.Windows.Clipboard.SetImage(bitmapSource);
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
                        _monitor.SuppressNextUpdate();
                        System.Windows.Clipboard.SetFileDropList(fileDropList);
                    }
                    break;
            }

            Log.Information($"Restored clipboard item: {item.Id}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore clipboard item");
            throw;
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
