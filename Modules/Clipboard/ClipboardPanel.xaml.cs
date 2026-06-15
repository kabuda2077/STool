using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STool.Core;

namespace STool.Modules.Clipboard;

public partial class ClipboardPanel : Window
{
    private enum Tab { All, Text, Image, File, Favorite }

    private readonly ClipboardManager _manager;
    private List<ClipboardItem> _allRaw = new();
    private Tab _tab = Tab.All;
    private string _searchText = string.Empty;

    public ClipboardPanel(ClipboardManager manager)
    {
        _manager = manager;
        InitializeComponent();
        LoadRecent();
    }

    private void LoadRecent()
    {
        _allRaw = _manager.GetRecent(200);
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

        var vms = q.Select(ToViewModel).ToList();
        itemsList.ItemsSource = vms;

        var any = vms.Count > 0;
        emptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        listScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        emptyTitle.Text = hasSearch ? "没有匹配结果" : "暂无记录";
        emptyDescription.Text = hasSearch ? "换个关键词试试" : "复制内容会自动保存到剪贴板历史中";
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
        ApplyFilter();
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

    // 双击复制(恢复到剪贴板),不弹出通知
    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        if (sender is Border border && border.Tag is string id)
        {
            var item = _allRaw.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                try
                {
                    _manager.RestoreToClipboard(item);
                    Close();
                }
                catch { /* 忽略恢复失败 */ }
            }
        }
    }

    // 右键菜单:收藏 / 删除
    private void MenuFavorite_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        if (id != null)
        {
            _manager.ToggleFavorite(id);
            LoadRecent();
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        if (id != null)
        {
            _manager.Delete(id);
            LoadRecent();
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
        var action = GetClearActionText();
        var confirmed = ConfirmDialog.Show(
            this,
            action,
            $"确定{action}吗？此操作不可恢复。",
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
                case Tab.Favorite:
                    _manager.ClearFavorites();
                    break;
                default:
                    _manager.ClearAll();
                    break;
            }

            LoadRecent();
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

    private ClipboardItemViewModel ToViewModel(ClipboardItem item)
    {
        var vm = new ClipboardItemViewModel
        {
            Id = item.Id,
            RelativeTime = RelativeTimeFormatter.Format(item.CreatedAt),
            FullTimestamp = RelativeTimeFormatter.GetFullTimestamp(item.CreatedAt),
            IsFavorite = item.IsFavorite,
            FavoriteGlyph = item.IsFavorite ? "" : "",
            FavoriteTooltip = item.IsFavorite ? "取消收藏" : "收藏",
            SourceApp = item.SourceApp ?? string.Empty
        };

        if (item.Type == ClipboardItemType.Image && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
        {
            vm.IsImage = true;
            vm.ImageSource = LoadThumbnail(item.ImagePath!);
            var (w, h) = ReadPngSize(item.ImagePath!);
            vm.SizeText = w > 0 ? $"{w} × {h}" : "图片";
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
            vm.SizeText = $"{item.TextContent?.Length ?? 0} 字符";
        }

        return vm;
    }

    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立即加载,不锁文件
            bmp.DecodePixelHeight = 240;                  // 缩略图,降低内存
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
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
public class ClipboardItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public bool IsText { get; set; }
    public string DisplayText { get; set; } = string.Empty;
    public ImageSource? ImageSource { get; set; }
    public string RelativeTime { get; set; } = string.Empty;
    public string FullTimestamp { get; set; } = string.Empty;
    public string SourceApp { get; set; } = string.Empty;
    public bool HasSource => !string.IsNullOrEmpty(SourceApp);
    public string SizeText { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public string FavoriteGlyph { get; set; } = "";
    public string FavoriteTooltip { get; set; } = "收藏";
}
