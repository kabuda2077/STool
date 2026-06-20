using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using STool.Core;

namespace STool.Modules.Clipboard;

public partial class ClipboardPanel : Window
{
    private enum Tab { All, Text, Image, File, Favorite }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly ClipboardManager _manager;
    private List<ClipboardItem> _allRaw = new();
    private Tab _tab = Tab.All;
    private string _searchText = string.Empty;
    private readonly IntPtr _targetHwnd;

    // D: ViewModel 按 Id 缓存,切分类/搜索时复用,避免重复造 VM 与重复解码
    private readonly Dictionary<string, ClipboardItemViewModel> _vmCache = new();

    // C: 搜索去抖
    private readonly DispatcherTimer _searchDebounce;

    // B: 缩略图后台解码并发限流(最多 3 个同时解码)
    private static readonly SemaphoreSlim ThumbThrottle = new(3);
    private const long LargeImageBytes = 2 * 1024 * 1024;

    public ClipboardPanel(ClipboardManager manager)
    {
        _manager = manager;
        _targetHwnd = GetForegroundWindow();
        InitializeComponent();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };

        LoadRecent();
    }

    private void LoadRecent()
    {
        var fresh = _manager.GetRecent(200);

        // D: 数据刷新时,清理缓存中已不存在的条目,避免无限增长
        var liveIds = new HashSet<string>(fresh.Select(i => i.Id));
        foreach (var staleId in _vmCache.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _vmCache.Remove(staleId);

        _allRaw = fresh;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<ClipboardItem> q = _allRaw;
        var hasSearch = !string.IsNullOrWhiteSpace(_searchText);

        q = _tab switch
        {
            Tab.Text => q.Where(i => i.Type == ClipboardItemType.Text),
            Tab.Image => q.Where(i => i.Type == ClipboardItemType.Image),
            Tab.File => q.Where(i => i.Type == ClipboardItemType.File),
            Tab.Favorite => q.Where(i => i.IsFavorite),
            _ => q
        };

        if (hasSearch)
        {
            q = q.Where(MatchesSearch);
        }

        var vms = q.Select(GetOrCreateViewModel).ToList();
        itemsList.ItemsSource = vms;

        var any = vms.Count > 0;
        emptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        itemsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        emptyClipboardIcon.Visibility = hasSearch ? Visibility.Collapsed : Visibility.Visible;
        emptySearchIcon.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        emptyTitle.Text = hasSearch ? "没有匹配结果" : "暂无记录";
        emptyDescription.Text = hasSearch ? "试试更短的关键词，或切换分类查看" : "复制内容会自动保存到剪贴板历史中";
        btnClearAll.ToolTip = GetClearActionText();
    }

    private bool MatchesSearch(ClipboardItem item)
    {
        var keyword = _searchText.Trim();
        if (keyword.Length == 0)
        {
            return true;
        }

        return Contains(item.TextContent, keyword)
            || Contains(item.SourceApp, keyword)
            || Contains(item.Tag, keyword)
            || Contains(item.ImagePath, keyword)
            || (item.FilePaths?.Any(path => Contains(path, keyword)) == true);
    }

    private static bool Contains(string? text, string keyword)
    {
        return !string.IsNullOrEmpty(text)
            && text.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = txtSearch.Text;
        var hasSearch = !string.IsNullOrWhiteSpace(_searchText);
        searchPlaceholder.Visibility = hasSearch ? Visibility.Collapsed : Visibility.Visible;
        btnClearSearch.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;

        // C: 去抖,停止输入 200ms 后再过滤,避免每键全量重建
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
    {
        txtSearch.Clear();
        txtSearch.Focus();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _tab = ReferenceEquals(sender, tabText) ? Tab.Text
             : ReferenceEquals(sender, tabImage) ? Tab.Image
             : ReferenceEquals(sender, tabFile) ? Tab.File
             : ReferenceEquals(sender, tabFavorite) ? Tab.Favorite
             : Tab.All;
        UpdateTabs();
        ApplyFilter();
    }

    private void UpdateTabs()
    {
        tabAll.Tag = _tab == Tab.All ? "on" : null;
        tabText.Tag = _tab == Tab.Text ? "on" : null;
        tabImage.Tag = _tab == Tab.Image ? "on" : null;
        tabFile.Tag = _tab == Tab.File ? "on" : null;
        tabFavorite.Tag = _tab == Tab.Favorite ? "on" : null;
        btnClearAll.ToolTip = GetClearActionText();
    }

    // 单击复制不关闭;双击复制、关闭面板并尝试粘贴到原前台文本框
    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string id)
        {
            var item = _allRaw.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                try
                {
                    _manager.RestoreToClipboard(item);
                    if (e.ClickCount >= 2)
                    {
                        Close();
                        PasteToTarget();
                    }
                    else
                    {
                        ToastNotification.Show("已复制");
                    }
                }
                catch
                {
                    ToastNotification.Show("复制失败", type: ToastNotification.ToastType.Error);
                }
            }
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement fe && fe.DataContext is ClipboardItemViewModel vm)
        {
            _manager.ToggleFavorite(vm.Id);
            _vmCache.Remove(vm.Id);
            LoadRecent();
            ToastNotification.Show(vm.IsFavorite ? "已取消收藏" : "已收藏");
        }
    }

    // 右键菜单:收藏 / 删除
    private void MenuFavorite_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        if (id != null)
        {
            _manager.ToggleFavorite(id);
            _vmCache.Remove(id);   // D: 收藏态变了,使该 VM 失效以便重建
            LoadRecent();
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        if (id != null)
        {
            _manager.Delete(id);
            _vmCache.Remove(id);
            LoadRecent();
        }
    }

    private void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        var item = id == null ? null : _allRaw.FirstOrDefault(i => i.Id == id);
        if (item?.Type != ClipboardItemType.Image || string.IsNullOrEmpty(item.ImagePath) || !File.Exists(item.ImagePath))
        {
            ToastNotification.Show("图片不存在", type: ToastNotification.ToastType.Error);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为图片",
            FileName = Path.GetFileName(item.ImagePath),
            Filter = "PNG 图片 (*.png)|*.png|所有文件 (*.*)|*.*",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.Copy(item.ImagePath, dialog.FileName, true);
            ToastNotification.Show("已保存");
        }
        catch
        {
            ToastNotification.Show("保存失败", type: ToastNotification.ToastType.Error);
        }
    }

    private static string? IdFromMenu(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.Tag is string id)
            return id;
        return null;
    }

    // 悬浮垃圾桶:按当前分类清空;只有"全部"页清空所有记录
    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_tab == Tab.Favorite)
        {
            ToastNotification.Show("收藏只能右键删除", type: ToastNotification.ToastType.Info);
            return;
        }

        var action = GetClearActionText();
        var confirmed = ConfirmDialog.Show(
            this,
            action,
            $"确定{action}吗？收藏条目会保留，只能右键删除。",
            "清空",
            "取消");

        if (confirmed)
        {
            switch (_tab)
            {
                case Tab.Text:
                    _manager.ClearByType(ClipboardItemType.Text);
                    break;
                case Tab.Image:
                    _manager.ClearByType(ClipboardItemType.Image);
                    break;
                case Tab.File:
                    _manager.ClearByType(ClipboardItemType.File);
                    break;
                default:
                    _manager.ClearAll();
                    break;
            }

            LoadRecent();
            ToastNotification.Show("已清理");
        }
    }

    private string GetClearActionText()
    {
        return _tab switch
        {
            Tab.Text => "清空文本",
            Tab.Image => "清空图像",
            Tab.File => "清空文件",
            Tab.Favorite => "清空收藏",
            _ => "清空全部"
        };
    }

    private void PasteToTarget()
    {
        if (_targetHwnd == IntPtr.Zero)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                var panelHwnd = new WindowInteropHelper(this).Handle;
                if (_targetHwnd == panelHwnd)
                    return;

                SetForegroundWindow(_targetHwnd);
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch
            {
                // 已经复制到剪贴板,粘贴失败不再打断用户
            }
        }));
    }

    // D: 命中缓存直接复用;否则新建(不在此处解码图片,解码留给可见时的 ThumbImage_Loaded)
    private ClipboardItemViewModel GetOrCreateViewModel(ClipboardItem item)
    {
        if (_vmCache.TryGetValue(item.Id, out var cached))
            return cached;

        var vm = ToViewModel(item);
        _vmCache[item.Id] = vm;
        return vm;
    }

    private ClipboardItemViewModel ToViewModel(ClipboardItem item)
    {
        var vm = new ClipboardItemViewModel
        {
            Id = item.Id,
            RelativeTime = RelativeTimeFormatter.Format(item.CreatedAt),
            FullTimestamp = RelativeTimeFormatter.GetFullTimestamp(item.CreatedAt),
            IsFavorite = item.IsFavorite,
            FavoriteGlyph = item.IsFavorite ? "★" : "☆",
            FavoriteTooltip = item.IsFavorite ? "取消收藏" : "收藏",
            FavoriteState = item.IsFavorite ? "on" : null,
            SourceApp = item.SourceApp ?? string.Empty
        };

        if (item.Type == ClipboardItemType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
        {
            vm.IsImage = true;
            vm.ImagePath = item.ImagePath;            // B: 仅记录路径,延迟到可见时解码
            vm.DisplayText = Path.GetFileName(item.ImagePath);
            var (w, h) = ReadPngSize(item.ImagePath!);
            var fileSize = new FileInfo(item.ImagePath).Length;
            vm.ImageInfoText = FormatImageInfo(w, h, fileSize);
            vm.SizeText = vm.ImageInfoText;
            vm.IsLargeImage = fileSize >= LargeImageBytes || Math.Max(w, h) >= 3000;

            var thumbWidth = 150d;
            var thumbHeight = 96d;
            if (w > 0 && h > 0)
            {
                var ratio = (double)w / h;
                if (ratio >= 1)
                {
                    thumbWidth = Math.Min(180, Math.Max(118, 96 * ratio));
                    thumbHeight = 96;
                }
                else
                {
                    thumbWidth = 96;
                    thumbHeight = Math.Min(130, Math.Max(92, 96 / ratio));
                }
            }

            vm.ThumbnailBoxWidth = thumbWidth;
            vm.ThumbnailBoxHeight = thumbHeight;
        }
        else if (item.Type == ClipboardItemType.File)
        {
            vm.IsText = true;
            vm.DisplayText = item.GetDisplayText(200);
            vm.SizeText = $"{item.FilePaths?.Length ?? 0} 个文件";
        }
        else
        {
            vm.IsText = true;
            vm.DisplayText = item.GetDisplayText(200);
            vm.SizeText = "文本";
        }

        return vm;
    }

    // B: 行进入可视区(虚拟化实例化)时才触发解码;后台线程解码,限流,完成后回 UI 线程赋值
    private void ThumbImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image img || img.DataContext is not ClipboardItemViewModel vm)
            return;
        if (!vm.IsImage || vm.ImageSource != null || vm.ThumbRequested || string.IsNullOrEmpty(vm.ImagePath))
            return;

        vm.ThumbRequested = true;
        var path = vm.ImagePath;

        _ = Task.Run(async () =>
        {
            await ThumbThrottle.WaitAsync();
            try
            {
                var bmp = LoadThumbnail(path);
                if (bmp != null)
                    Dispatcher.Invoke(() => vm.ImageSource = bmp);
                else
                    vm.ThumbRequested = false;   // 解码失败,允许后续重试
            }
            finally
            {
                ThumbThrottle.Release();
            }
        });
    }

    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            var thumbPath = GetThumbnailPath(path);
            EnsureThumbnail(path, thumbPath);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立即加载,不锁文件
            bmp.DecodePixelHeight = 220;                  // 缩略图,降低内存
            bmp.UriSource = new Uri(File.Exists(thumbPath) ? thumbPath : path);
            bmp.EndInit();
            bmp.Freeze();                                 // 跨线程:冻结后可在 UI 线程使用
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureThumbnail(string sourcePath, string thumbPath)
    {
        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var thumbInfo = new FileInfo(thumbPath);
            if (thumbInfo.Exists && thumbInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
                return;

            Directory.CreateDirectory(AppPaths.ClipboardThumbnailsDirectory);

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelHeight = 240;
            source.UriSource = new Uri(sourcePath);
            source.EndInit();
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new FileStream(thumbPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            File.SetLastWriteTimeUtc(thumbPath, sourceInfo.LastWriteTimeUtc);
        }
        catch
        {
            // 缩略图缓存失败时回退到源图加载,不影响主流程
        }
    }

    private static string GetThumbnailPath(string imagePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(imagePath);
        return Path.Combine(AppPaths.ClipboardThumbnailsDirectory, fileName + ".thumb.png");
    }

    private static string FormatImageInfo(int width, int height, long bytes)
    {
        var dimensions = width > 0 && height > 0 ? $"{width} × {height}" : "图片";
        return $"{dimensions} · {FormatBytes(bytes)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024d / 1024d:0.#} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes} B";
    }

    private void ThumbPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is ClipboardItemViewModel vm)
        {
            ShowImagePreview(vm);
        }
    }

    private void ShowImagePreview(ClipboardItemViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.ImagePath) || !File.Exists(vm.ImagePath))
        {
            ToastNotification.Show("图片不存在", type: ToastNotification.ToastType.Error);
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1200;
            bmp.UriSource = new Uri(vm.ImagePath);
            bmp.EndInit();
            bmp.Freeze();

            imagePreview.Source = bmp;
            imagePreviewTitle.Text = vm.ImageInfoText;
            imagePreviewOverlay.Visibility = Visibility.Visible;
        }
        catch
        {
            ToastNotification.Show("预览失败", type: ToastNotification.ToastType.Error);
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        HideImagePreview();
    }

    private void ImagePreviewOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideImagePreview();
    }

    private void ImagePreviewCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void HideImagePreview()
    {
        imagePreview.Source = null;
        imagePreviewOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>从 PNG 文件头读取原始尺寸(剪贴板图片均存为 PNG),避免整图解码。</summary>
    private static (int w, int h) ReadPngSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[24];
            if (fs.Read(buf, 0, 24) < 24)
                return (0, 0);

            // PNG 签名 89 50 4E 47;IHDR 中 width@16、height@20(大端)
            if (buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
            {
                int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                return (w, h);
            }
        }
        catch { }
        return (0, 0);
    }
}

/// <summary>
/// 剪贴板条目视图模型
/// </summary>
public class ClipboardItemViewModel : INotifyPropertyChanged
{
    public string Id { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public bool IsText { get; set; }
    public string DisplayText { get; set; } = string.Empty;

    // B: 图片路径与延迟解码标记
    public string? ImagePath { get; set; }
    public bool ThumbRequested { get; set; }

    private ImageSource? _imageSource;
    public ImageSource? ImageSource
    {
        get => _imageSource;
        set
        {
            if (ReferenceEquals(_imageSource, value)) return;
            _imageSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
        }
    }

    public string RelativeTime { get; set; } = string.Empty;
    public string FullTimestamp { get; set; } = string.Empty;
    public string SourceApp { get; set; } = string.Empty;
    public bool HasSource => !string.IsNullOrEmpty(SourceApp);
    public string SizeText { get; set; } = string.Empty;
    public string ImageInfoText { get; set; } = string.Empty;
    public bool IsLargeImage { get; set; }
    public double ThumbnailBoxWidth { get; set; } = 150;
    public double ThumbnailBoxHeight { get; set; } = 96;
    public bool IsFavorite { get; set; }
    public string FavoriteGlyph { get; set; } = "☆";
    public string FavoriteTooltip { get; set; } = "收藏";
    public string? FavoriteState { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
