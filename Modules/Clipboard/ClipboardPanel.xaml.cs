using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using STool.Core;

namespace STool.Modules.Clipboard;

public partial class ClipboardPanel : Window
{
    private readonly ClipboardManager _manager;
    private List<ClipboardItemViewModel> _currentItems = new();

    public ClipboardPanel(ClipboardManager manager)
    {
        _manager = manager;
        InitializeComponent();

        LoadRecent();
    }

    private void LoadRecent()
    {
        var items = _manager.GetRecent(100);
        DisplayItems(items);
    }

    private void DisplayItems(List<ClipboardItem> items)
    {
        _currentItems = items.Select(item => new ClipboardItemViewModel
        {
            Id = item.Id,
            DisplayText = item.GetDisplayText(200),
            CreatedAt = item.CreatedAt,
            RelativeTime = RelativeTimeFormatter.Format(item.CreatedAt),
            FullTimestamp = RelativeTimeFormatter.GetFullTimestamp(item.CreatedAt),
            IsFavorite = item.IsFavorite,
            FavoriteGlyph = item.IsFavorite ? "" : "",
            FavoriteTooltip = item.IsFavorite ? "取消收藏" : "收藏"
        }).ToList();

        itemsList.ItemsSource = _currentItems;

        // 显示空状态
        if (!items.Any())
        {
            emptyState.Visibility = Visibility.Visible;
            itemsList.Visibility = Visibility.Collapsed;
        }
        else
        {
            emptyState.Visibility = Visibility.Collapsed;
            itemsList.Visibility = Visibility.Visible;
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
            return;

        var keyword = txtSearch.Text;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            LoadRecent();
            return;
        }

        var items = _manager.Search(keyword, 100);
        DisplayItems(items);
    }

    private void BtnShowAll_Click(object sender, RoutedEventArgs e)
    {
        LoadRecent();
    }

    private void BtnShowFavorites_Click(object sender, RoutedEventArgs e)
    {
        var items = _manager.GetFavorites();
        DisplayItems(items);
    }

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string id)
        {
            try
            {
                var item = _manager.GetRecent(1000).FirstOrDefault(i => i.Id == id);
                if (item != null)
                {
                    _manager.RestoreToClipboard(item);
                    ToastNotification.Show("已恢复到剪贴板", type: ToastNotification.ToastType.Success);
                }
            }
            catch (Exception ex)
            {
                ToastNotification.Show("恢复失败", ex.Message, ToastNotification.ToastType.Error);
            }
        }
    }

    private void BtnFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string id)
        {
            var item = _manager.GetRecent(1000).FirstOrDefault(i => i.Id == id);
            var wasFavorite = item?.IsFavorite ?? false;

            _manager.ToggleFavorite(id);
            LoadRecent(); // 刷新列表

            ToastNotification.Show(
                wasFavorite ? "已取消收藏" : "已添加收藏",
                type: ToastNotification.ToastType.Success
            );

            e.Handled = true;
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string id)
        {
            var result = System.Windows.MessageBox.Show("确定删除此条目？", "STool",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _manager.Delete(id);
                LoadRecent(); // 刷新列表
                ToastNotification.Show("已删除", type: ToastNotification.ToastType.Success);
            }

            e.Handled = true;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// 剪贴板条目视图模型
/// </summary>
public class ClipboardItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string RelativeTime { get; set; } = string.Empty;
    public string FullTimestamp { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public string FavoriteGlyph { get; set; } = "";
    public string FavoriteTooltip { get; set; } = "收藏";
}
