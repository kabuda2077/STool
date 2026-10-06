using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Serilog;
using STool.Core;

namespace STool.Modules.Clipboard;

public partial class ClipboardPanel : Window
{
    private enum Tab { All, Text, Image, File, Favorite }

    private const int PageSize = 60;
    private const double LoadMoreThreshold = 240;
    private static readonly TimeSpan TabContentDelay = TimeSpan.FromMilliseconds(70);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly ClipboardManager _manager;
    private readonly IntPtr _targetHwnd;
    private readonly ObservableCollection<ClipboardItemViewModel> _items = new();
    private readonly Dictionary<string, ClipboardItem> _itemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipboardItemViewModel> _viewModelsById = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _tabContentDelay;
    private readonly SemaphoreSlim _clipboardRestoreGate = new(1, 1);
    private Tab _tab = Tab.All;
    private string _searchText = string.Empty;
    private int _queryGeneration;
    private bool _queryDirty = true;
    private bool _loadingPage;
    private bool _hasMore;
    private bool _closing;

    public ClipboardPanel(ClipboardManager manager)
    {
        _manager = manager;
        _targetHwnd = GetForegroundWindow();
        InitializeComponent();
        itemsList.ItemsSource = _items;

        _tabContentDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TabContentDelay
        };
        _tabContentDelay.Tick += TabContentDelay_Tick;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ReloadItems();
        };
        Loaded += (_, _) =>
        {
            UpdateTabSlider();
            MemoryDiagnostics.LogCheckpoint("ClipboardOpened");

            // Run after the window's initial activation so typing can start a search
            // immediately without requiring a click in the search box.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusSearch));
        };
        tabSegmentGrid.SizeChanged += (_, _) => UpdateTabSlider();
        _thumbnailCache.ItemEvicted += OnThumbnailEvicted;
        _manager.ItemAdded += Manager_ItemAdded;

        ReloadItems();
    }

    private void FocusSearch()
    {
        if (_closing)
            return;

        txtSearch.Focus();
        Keyboard.Focus(txtSearch);
        txtSearch.CaretIndex = txtSearch.Text.Length;
    }

    // ---------- 数据加载 ----------

    private void ReloadItems() => _ = LoadPageAsync(reset: true);

    private void InvalidateQuery()
    {
        _queryGeneration++;
        _queryDirty = true;
        _hasMore = false;
        itemsList.IsEnabled = false;
    }

    /// <summary>按当前分类和关键词分页查询。reset 时从头加载，否则追加下一页；过期的查询结果会被丢弃。</summary>
    private async Task LoadPageAsync(bool reset)
    {
        if (_closing || (!reset && (_loadingPage || !_hasMore)))
            return;

        if (reset)
        {
            _searchDebounce.Stop();
            _tabContentDelay.Stop();
            ResetThumbnailGeneration();
            InvalidateQuery();
        }

        var generation = _queryGeneration;
        _loadingPage = true;
        var query = new ClipboardQuery(
            TabType(_tab),
            _tab == Tab.Favorite,
            string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim(),
            reset ? 0 : _items.Count,
            PageSize + 1);

        List<ClipboardItem> page;
        try
        {
            page = await Task.Run(() => _manager.Query(query));
        }
        catch (Exception ex)
        {
            if (generation == _queryGeneration)
                _loadingPage = false;
            if (ex is not ObjectDisposedException && !_closing)
            {
                Log.Warning(ex, "Failed to query clipboard history");
                ToastNotification.Show("读取剪贴板历史失败", ex.Message, ToastNotification.ToastType.Error);
            }
            return;
        }

        if (_closing || generation != _queryGeneration)
            return;

        _hasMore = page.Count > PageSize;
        if (_hasMore)
            page.RemoveAt(page.Count - 1);

        if (reset)
            ClearItems();

        foreach (var item in page)
        {
            if (!_itemsById.ContainsKey(item.Id))
                AppendItem(item);
        }

        UpdateEmptyState();
        if (reset && _items.Count > 0)
        {
            itemsList.SelectedIndex = 0;
            itemsList.ScrollIntoView(_items[0]);
        }
        _loadingPage = false;
        _queryDirty = false;
        itemsList.IsEnabled = true;
    }

    private static ClipboardItemType? TabType(Tab tab) => tab switch
    {
        Tab.Text => ClipboardItemType.Text,
        Tab.Image => ClipboardItemType.Image,
        Tab.File => ClipboardItemType.File,
        _ => null
    };

    private bool MatchesTab(ClipboardItem item) => _tab switch
    {
        Tab.Favorite => item.IsFavorite,
        Tab.All => true,
        _ => TabType(_tab) == item.Type
    };

    private void AppendItem(ClipboardItem item)
    {
        var vm = ToViewModel(item);
        _itemsById[item.Id] = item;
        _viewModelsById[item.Id] = vm;
        _items.Add(vm);
    }

    private void RemoveItem(string id)
    {
        _itemsById.Remove(id);
        if (!_viewModelsById.Remove(id, out var vm))
            return;

        _items.Remove(vm);
        if (!string.IsNullOrEmpty(vm.ThumbnailCacheKey))
            _thumbnailCache.Remove(vm.ThumbnailCacheKey);
        vm.ImageSource = null;
    }

    private void ClearItems()
    {
        foreach (var vm in _items)
        {
            vm.ImageSource = null;
            vm.ThumbRequested = false;
        }

        _items.Clear();
        _itemsById.Clear();
        _viewModelsById.Clear();
    }

    private void Manager_ItemAdded(object? sender, ClipboardItem item)
    {
        if (_closing || Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnItemAdded(item)));
    }

    /// <summary>新记录或被重新复制的记录插到最前，不重建整个列表。</summary>
    private void OnItemAdded(ClipboardItem item)
    {
        if (_closing)
            return;

        // 关键词匹配以数据库中的完整文本为准，搜索时直接重新查询。
        if (!string.IsNullOrWhiteSpace(_searchText) || _loadingPage || _queryDirty)
        {
            ReloadItems();
            return;
        }

        if (!MatchesTab(item))
            return;

        var selected = itemsList.SelectedItem;
        RemoveItem(item.Id);
        var vm = ToViewModel(item);
        _itemsById[item.Id] = item;
        _viewModelsById[item.Id] = vm;
        _items.Insert(0, vm);
        if (selected == null)
            itemsList.SelectedIndex = 0;
        else if (selected is ClipboardItemViewModel selectedVm && selectedVm.Id == item.Id)
            itemsList.SelectedItem = vm;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var any = _items.Count > 0;
        var hasSearch = !string.IsNullOrWhiteSpace(_searchText);
        emptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        itemsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        emptyClipboardIcon.Visibility = hasSearch ? Visibility.Collapsed : Visibility.Visible;
        emptySearchIcon.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        emptyTitle.Text = hasSearch ? "没有匹配结果" : "暂无记录";
        emptyDescription.Text = hasSearch ? "试试更短的关键词，或切换分类查看" : "复制内容会自动保存到剪贴板历史中";
        btnClearAll.ToolTip = GetClearButtonTooltip();
    }

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_hasMore && !_loadingPage && e.ExtentHeight > 0 &&
            e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - LoadMoreThreshold)
        {
            _ = LoadPageAsync(reset: false);
        }
    }

    // ---------- 搜索与分类 ----------

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = txtSearch.Text;
        InvalidateQuery();
        var hasSearch = !string.IsNullOrWhiteSpace(_searchText);
        searchPlaceholder.Visibility = hasSearch ? Visibility.Collapsed : Visibility.Visible;
        btnClearSearch.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;

        // 去抖,停止输入 200ms 后再查询
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
        var nextTab = ReferenceEquals(sender, tabText) ? Tab.Text
                    : ReferenceEquals(sender, tabImage) ? Tab.Image
                    : ReferenceEquals(sender, tabFile) ? Tab.File
                    : ReferenceEquals(sender, tabFavorite) ? Tab.Favorite
                    : Tab.All;
        SelectTab(nextTab);
    }

    private void SelectTab(Tab nextTab)
    {
        if (nextTab == _tab)
            return;

        _tab = nextTab;
        InvalidateQuery();
        UpdateTabs();

        _tabContentDelay.Stop();
        if (MotionSettings.ShouldReduceMotion)
        {
            ReloadItems();
            return;
        }

        _tabContentDelay.Start();
    }

    private void TabContentDelay_Tick(object? sender, EventArgs e)
    {
        _tabContentDelay.Stop();
        if (!_closing)
            ReloadItems();
    }

    private void UpdateTabs()
    {
        tabAll.Tag = _tab == Tab.All ? "on" : null;
        tabText.Tag = _tab == Tab.Text ? "on" : null;
        tabImage.Tag = _tab == Tab.Image ? "on" : null;
        tabFile.Tag = _tab == Tab.File ? "on" : null;
        tabFavorite.Tag = _tab == Tab.Favorite ? "on" : null;
        btnClearAll.ToolTip = GetClearButtonTooltip();
        UpdateTabSlider();
    }

    private void UpdateTabSlider()
    {
        var selectedButton = _tab switch
        {
            Tab.Text => tabText,
            Tab.Image => tabImage,
            Tab.File => tabFile,
            Tab.Favorite => tabFavorite,
            _ => tabAll
        };
        var target = selectedButton.TranslatePoint(new System.Windows.Point(0, 0), tabSegmentGrid).X;
        SegmentedSliderMotion.MoveTo(
            tabSlider,
            tabSliderTransform,
            tabSliderScale,
            target,
            selectedButton.ActualWidth,
            IsLoaded);
    }

    // ---------- 键盘 ----------

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (imagePreviewOverlay.Visibility == Visibility.Visible)
                HideImagePreview();
            else
                Close();
            return;
        }

        if (imagePreviewOverlay.Visibility == Visibility.Visible)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control)
        {
            var tab = e.Key switch
            {
                Key.D1 or Key.NumPad1 => Tab.All,
                Key.D2 or Key.NumPad2 => Tab.Text,
                Key.D3 or Key.NumPad3 => Tab.Image,
                Key.D4 or Key.NumPad4 => Tab.File,
                Key.D5 or Key.NumPad5 => Tab.Favorite,
                _ => (Tab?)null
            };
            if (tab != null)
            {
                e.Handled = true;
                SelectTab(tab.Value);
                return;
            }

            if (e.Key == Key.F)
            {
                e.Handled = true;
                FocusSearch();
                txtSearch.SelectAll();
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                MoveSelection(8);
                break;
            case Key.PageUp:
                e.Handled = true;
                MoveSelection(-8);
                break;
            case Key.Enter:
                e.Handled = true;
                // 防抖尚未结束时先执行最新查询，不能粘贴旧列表中的第一条。
                _ = ActivateSelectionAsync(paste: modifiers != ModifierKeys.Control);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0)
            return;

        var current = itemsList.SelectedIndex;
        var index = current < 0 ? (delta > 0 ? 0 : _items.Count - 1) : Math.Clamp(current + delta, 0, _items.Count - 1);
        itemsList.SelectedIndex = index;
        itemsList.ScrollIntoView(_items[index]);

        if (index >= _items.Count - 3)
            _ = LoadPageAsync(reset: false);
    }

    // ---------- 条目操作 ----------

    // 单击复制不关闭;双击复制、关闭面板并尝试粘贴到原前台文本框
    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string id })
            return;

        e.Handled = true;
        if (_viewModelsById.TryGetValue(id, out var vm))
            itemsList.SelectedItem = vm;

        _ = ActivateItemAsync(id, paste: e.ClickCount >= 2);
    }

    private async Task ActivateSelectionAsync(bool paste)
    {
        if (_queryDirty)
            await LoadPageAsync(reset: true);
        if (!_closing && !_queryDirty && itemsList.SelectedItem is ClipboardItemViewModel selected)
            await ActivateItemAsync(selected.Id, paste);
    }

    private async Task ActivateItemAsync(string id, bool paste)
    {
        if (_closing || _queryDirty || !_itemsById.TryGetValue(id, out var item))
            return;

        if (!await TryRestoreToClipboardAsync(item))
            return;

        if (paste)
        {
            PasteToTargetAfterClose();
            return;
        }

        ToastNotification.Show("已复制");
    }

    private async Task<bool> TryRestoreToClipboardAsync(ClipboardItem item)
    {
        if (_closing)
            return false;
        var cancellationToken = _thumbnailLifetime.Token;
        try
        {
            await _clipboardRestoreGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (_closing)
        {
            return false;
        }

        try
        {
            await _manager.RestoreToClipboardAsync(item, cancellationToken);
            return !_closing;
        }
        catch (OperationCanceledException) when (_closing)
        {
            return false;
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
            return false;
        }
        finally
        {
            _clipboardRestoreGate.Release();
        }
    }

    private void PasteToTargetAfterClose()
    {
        var targetHwnd = _targetHwnd;
        var panelHwnd = new WindowInteropHelper(this).Handle;

        Close();

        if (targetHwnd == IntPtr.Zero || targetHwnd == panelHwnd)
        {
            ToastNotification.Show("内容已复制", "原窗口不可用，请手动粘贴。", ToastNotification.ToastType.Info);
            return;
        }

        _ = PasteToTargetAsync(targetHwnd);
    }

    private static async Task PasteToTargetAsync(IntPtr targetHwnd)
    {
        try
        {
            if (!await ForegroundPaste.PasteToAsync(targetHwnd))
                ToastNotification.Show("内容已复制", "无法切回原窗口，请手动粘贴。", ToastNotification.ToastType.Info);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("内容已复制", $"自动粘贴失败：{ex.Message}", ToastNotification.ToastType.Info);
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ClipboardItemViewModel vm })
        {
            var favorite = ToggleFavorite(vm.Id);
            if (favorite != null)
                ToastNotification.Show(favorite.Value ? "已收藏" : "已取消收藏");
        }
    }

    // 右键菜单:收藏 / 删除
    private void MenuFavorite_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        if (id != null)
            ToggleFavorite(id);
    }

    private bool? ToggleFavorite(string id)
    {
        bool? favorite;
        try
        {
            favorite = _manager.ToggleFavorite(id);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("操作失败", ex.Message, ToastNotification.ToastType.Error);
            return null;
        }

        if (favorite == null)
        {
            RemoveItem(id);
            UpdateEmptyState();
            return null;
        }

        if (_itemsById.TryGetValue(id, out var item))
            item.IsFavorite = favorite.Value;
        if (_viewModelsById.TryGetValue(id, out var vm))
            vm.SetFavorite(favorite.Value);

        // 收藏页里取消收藏后，该条目不再属于当前分类。
        if (_tab == Tab.Favorite && !favorite.Value)
        {
            RemoveItem(id);
            UpdateEmptyState();
        }

        return favorite;
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        if (id == null)
            return;

        _itemsById.TryGetValue(id, out var item);
        try
        {
            _manager.Delete(id);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("删除失败", ex.Message, ToastNotification.ToastType.Error);
            return;
        }

        RemoveItem(id);
        UpdateEmptyState();
        ToastNotification.Show("已删除剪贴板记录", item == null ? "" : $"已删除{GetItemKindText(item)}记录");
    }

    private void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        var id = IdFromMenu(sender);
        var item = id != null && _itemsById.TryGetValue(id, out var found) ? found : null;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
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
            ToastNotification.Show("收藏需逐条删除", "请在收藏条目上右键选择删除。", ToastNotification.ToastType.Info);
            return;
        }

        var confirmed = ConfirmDialog.Show(
            this,
            GetClearConfirmTitle(),
            GetClearConfirmMessage(),
            _tab == Tab.All ? "清空全部" : "清空分类",
            "取消");

        if (!confirmed)
            return;

        try
        {
            if (TabType(_tab) is { } type)
                _manager.ClearByType(type);
            else
                _manager.ClearAll();
        }
        catch (Exception ex)
        {
            ToastNotification.Show("清空失败", ex.Message, ToastNotification.ToastType.Error);
            return;
        }

        ReloadItems();
        ToastNotification.Show(GetClearSuccessTitle(), GetClearSuccessMessage());
    }

    private string GetClearButtonTooltip() => _tab switch
    {
        Tab.Text => "清空文本",
        Tab.Image => "清空图像",
        Tab.File => "清空文件",
        Tab.Favorite => "收藏条目请右键逐条删除",
        _ => "清空全部"
    };

    private string GetClearConfirmTitle() =>
        _tab == Tab.All ? "清空全部剪贴板历史" : $"清空{GetCurrentCategoryName()}分类";

    private string GetClearConfirmMessage() => _tab == Tab.All
        ? "将删除全部非收藏剪贴板记录。收藏条目会保留，如需删除收藏，请在条目右键菜单中删除。"
        : $"将删除{GetCurrentCategoryName()}分类中的非收藏记录。其他分类和收藏条目会保留。";

    private string GetClearSuccessTitle() =>
        _tab == Tab.All ? "已清空全部" : $"已清空{GetCurrentCategoryName()}分类";

    private string GetClearSuccessMessage() => _tab == Tab.All
        ? "已删除全部非收藏剪贴板记录。"
        : $"已删除{GetCurrentCategoryName()}分类中的非收藏记录。";

    private string GetCurrentCategoryName() => _tab switch
    {
        Tab.Text => "文本",
        Tab.Image => "图像",
        Tab.File => "文件",
        _ => "全部"
    };

    private static string GetItemKindText(ClipboardItem item) => item.Type switch
    {
        ClipboardItemType.Image => "图像",
        ClipboardItemType.File => "文件",
        _ => "文本"
    };

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _manager.ItemAdded -= Manager_ItemAdded;
        _searchDebounce.Stop();
        _tabContentDelay.Stop();
        _tabContentDelay.Tick -= TabContentDelay_Tick;
        _thumbnailLifetime.Cancel();
        _thumbnailGeneration.Cancel();

        itemsList.ItemsSource = null;
        HideImagePreview();
        ClearItems();

        _thumbnailCache.Clear();
        _thumbnailCache.ItemEvicted -= OnThumbnailEvicted;
        _thumbnailGeneration.Dispose();
        _thumbnailLifetime.Dispose();
        MemoryDiagnostics.LogCheckpoint(
            "ClipboardClosed",
            _thumbnailCache.Count,
            _thumbnailCache.EstimatedBytes);
        base.OnClosed(e);
    }
}
