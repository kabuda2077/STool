using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Views.Settings;

namespace STool.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly IAppShell _shell;
    private readonly Func<GeneralSettingsPanel> _createGeneralPanel;
    private System.Windows.Controls.Button? _currentSelectedButton;
    private bool _syncingScrollBar;
    private Action? _pendingNavigation;
    private Action? _returnToPage;
    private Func<UIElement>? _currentPanelFactory;
    private string _currentTitle = string.Empty;
    private readonly Dictionary<System.Windows.Controls.Button, double> _scrollOffsets = new();

    public SettingsWindow(ConfigManager configManager, IAppShell shell)
        : this(configManager, shell, () => new GeneralSettingsPanel(configManager, shell))
    {
    }

    internal SettingsWindow(ConfigManager configManager, IAppShell shell, Func<GeneralSettingsPanel> createGeneralPanel)
    {
        _createGeneralPanel = createGeneralPanel;
        InitializeComponent();
        _configManager = configManager;
        _shell = shell;
        AddHandler(SettingsFeedback.ChangedEvent, new RoutedPropertyChangedEventHandler<string>((_, e) =>
        {
            saveStatus.Text = e.NewValue;
            saveStatus.ToolTip = CurrentAutoSave?.SaveError;
            saveFailure.Visibility = CurrentAutoSave?.HasSaveError == true ? Visibility.Visible : Visibility.Collapsed;
            saveError.Text = CurrentAutoSave?.SaveErrorSummary ?? string.Empty;
            saveError.ToolTip = CurrentAutoSave?.SaveError;
            e.Handled = true;
        }));
        ApplyLayoutMetrics();

        // 默认显示通用设置
        ShowGeneralSettings();
    }

    private void BtnGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneralSettings();
    }

    private void BtnClipboardSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("剪贴板设置", btnClipboardSettings, () => new ClipboardSettingsPanel(_configManager, _shell));
    }

    private void BtnAccountsSettings_Click(object sender, RoutedEventArgs e) => ShowAccountsSettings();

    /// <summary>OCR/翻译页提示"未配置账号"时跳转到服务账号页。</summary>
    public void ShowAccountsSettings()
    {
        ShowPanel("服务账号", btnAccountsSettings, () => new AccountsSettingsPanel(_configManager));
    }

    private void OpenAccount(ServiceAccountTarget target)
    {
        var returnAction = _currentSelectedButton == btnOcrSettings
            ? (Action)ShowOcrSettings : ShowTranslationSettings;
        var returnTitle = _currentTitle;
        if (ShowPanel("服务账号", btnAccountsSettings, () => new AccountsSettingsPanel(_configManager, target)))
        {
            _returnToPage = returnAction;
            returnToSettings.Content = $"返回{returnTitle}";
            returnToSettings.Visibility = Visibility.Visible;
        }
        else
        {
            _pendingNavigation = () => OpenAccount(target);
        }
    }

    private void ReturnToSettings_Click(object sender, RoutedEventArgs e) => _returnToPage?.Invoke();

    private void BtnOcrSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowOcrSettings();
    }

    private void BtnTranslationSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowTranslationSettings();
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
            NavigationSelection.SetIsSelected(_currentSelectedButton, false);

        NavigationSelection.SetIsSelected(button, true);
        _currentSelectedButton = button;
    }

    private void ShowGeneralSettings()
    {
        ShowPanel("通用设置", btnGeneralSettings, () => _createGeneralPanel());
    }

    private void ShowOcrSettings()
    {
        ShowPanel("OCR 设置", btnOcrSettings, () => new OcrSettingsPanel(_configManager, OpenAccount));
    }

    private void ShowTranslationSettings()
    {
        ShowPanel("翻译设置", btnTranslationSettings, () => new TranslationSettingsPanel(_configManager, OpenAccount));
    }

    private SettingsAutoSaveController? CurrentAutoSave => contentPanel.Children.Count > 0
        ? SettingsAutoSaveController.GetController(contentPanel.Children[0])
        : null;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (CurrentAutoSave?.SaveNow() == false)
        {
            _pendingNavigation = Close;
            e.Cancel = true;
        }
    }

    private void RetrySave_Click(object sender, RoutedEventArgs e)
    {
        var pending = _pendingNavigation;
        if (CurrentAutoSave?.SaveNow() == false) return;
        _pendingNavigation = null;
        pending?.Invoke();
    }

    private void DiscardSave_Click(object sender, RoutedEventArgs e)
    {
        CurrentAutoSave?.DiscardChanges();
        var pending = _pendingNavigation;
        _pendingNavigation = null;
        if (pending != null) pending();
        else if (_currentSelectedButton != null && _currentPanelFactory != null)
            ShowPanel(_currentTitle, _currentSelectedButton, _currentPanelFactory);
    }

    private bool ShowPanel(string title, System.Windows.Controls.Button navigationButton, Func<UIElement> createPanel)
    {
        if (CurrentAutoSave?.SaveNow() == false)
        {
            _pendingNavigation = () => ShowPanel(title, navigationButton, createPanel);
            return false;
        }

        _pendingNavigation = null;
        if (_currentSelectedButton != null)
            _scrollOffsets[_currentSelectedButton] = contentScroll.VerticalOffset;

        var panel = createPanel();
        pageTitle.Text = title;
        Title = $"STool 设置 · {title}";
        saveStatus.Text = string.Empty;
        saveStatus.ToolTip = null;
        saveFailure.Visibility = Visibility.Collapsed;
        returnToSettings.Visibility = Visibility.Collapsed;
        _returnToPage = null;
        _currentTitle = title;
        _currentPanelFactory = createPanel;
        contentPanel.Children.Clear();
        contentPanel.Children.Add(panel);
        if (CurrentAutoSave is { } controller)
            controller.DraftChanged += (sender, _) =>
            {
                if (ReferenceEquals(CurrentAutoSave, sender)) _pendingNavigation = null;
            };
        SelectNavigationButton(navigationButton);
        UpdateContentPanelWidth();
        UpdateOverlayScrollBar();
        contentScroll.ScrollToVerticalOffset(_scrollOffsets.GetValueOrDefault(navigationButton));
        return true;
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
