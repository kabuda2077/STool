using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Views.Settings;

namespace STool.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly IAppShell _shell;
    private System.Windows.Controls.Button? _currentSelectedButton;
    private bool _syncingScrollBar;

    public SettingsWindow(ConfigManager configManager, IAppShell shell)
    {
        InitializeComponent();
        _configManager = configManager;
        _shell = shell;
        ApplyLayoutMetrics();

        // 默认显示通用设置
        ShowGeneralSettings();
        SelectNavigationButton(btnGeneralSettings);
    }

    private void BtnGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneralSettings();
        SelectNavigationButton(btnGeneralSettings);
    }

    private void BtnClipboardSettings_Click(object sender, RoutedEventArgs e)
    {
        Title = "剪贴板设置";
        ShowPanel(new ClipboardSettingsPanel(_configManager, _shell));
        SelectNavigationButton(btnClipboardSettings);
    }

    private void BtnOcrSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowOcrSettings();
        SelectNavigationButton(btnOcrSettings);
    }

    private void BtnTranslationSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowTranslationSettings();
        SelectNavigationButton(btnTranslationSettings);
    }

    // 让鼠标停在输入框上(未点击)时滚轮也能滚动整页:
    // 输入框会吞掉冒泡的 MouseWheel,这里在隧道阶段直接滚外层 ScrollViewer。
    private void ContentScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        contentScroll.ScrollToVerticalOffset(contentScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void ContentScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentPanelWidth();
        UpdateOverlayScrollBar();
    }

    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateOverlayScrollBar();
    }

    private void ContentScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingScrollBar)
        {
            return;
        }

        contentScroll.ScrollToVerticalOffset(e.NewValue);
    }

    private void SelectNavigationButton(System.Windows.Controls.Button button)
    {
        // 清除之前的选中状态
        if (_currentSelectedButton != null)
        {
            _currentSelectedButton.Tag = null;
        }

        // 设置当前按钮为选中
        button.Tag = "Selected";
        _currentSelectedButton = button;
    }

    private void ShowGeneralSettings()
    {
        Title = "通用设置";
        ShowPanel(new GeneralSettingsPanel(_configManager, _shell));
    }

    private void ShowOcrSettings()
    {
        Title = "OCR 设置";
        ShowPanel(new OcrSettingsPanel(_configManager));
    }

    private void ShowTranslationSettings()
    {
        Title = "翻译设置";
        ShowPanel(new TranslationSettingsPanel(_configManager));
    }

    private void ShowPanel(UIElement panel)
    {
        contentPanel.Children.Clear();
        contentPanel.Children.Add(panel);
        UpdateContentPanelWidth();
        UpdateOverlayScrollBar();
        contentScroll.ScrollToTop();
    }

    private void ApplyLayoutMetrics()
    {
        contentPanel.Margin = new Thickness(
            SettingsLayout.ContentHorizontalMargin,
            SettingsLayout.ContentTopMargin,
            SettingsLayout.ContentHorizontalMargin,
            18);
        UpdateContentPanelWidth();
        UpdateOverlayScrollBar();
    }

    private void UpdateContentPanelWidth()
    {
        var availableWidth = contentScroll.ActualWidth
            - SettingsLayout.ContentHorizontalMargin * 2;

        if (availableWidth <= 0)
        {
            return;
        }

        contentPanel.Width = availableWidth;
    }

    private void UpdateOverlayScrollBar()
    {
        if (contentScrollBar == null)
        {
            return;
        }

        var scrollableHeight = Math.Max(0, contentScroll.ScrollableHeight);
        contentScrollBar.Visibility = scrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        contentScrollBar.Maximum = scrollableHeight;
        contentScrollBar.ViewportSize = Math.Max(0, contentScroll.ViewportHeight);
        contentScrollBar.LargeChange = Math.Max(16, contentScroll.ViewportHeight * 0.9);
        contentScrollBar.SmallChange = 32;

        _syncingScrollBar = true;
        contentScrollBar.Value = Math.Min(contentScroll.VerticalOffset, scrollableHeight);
        _syncingScrollBar = false;
    }

}
