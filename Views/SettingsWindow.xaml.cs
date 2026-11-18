using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using STool.Core;
using STool.Views.Settings;

namespace STool.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly IAppShell _shell;
    private readonly Func<GeneralSettingsPanel> _createGeneralPanel;
    private readonly Func<ServiceAccountTarget?, AccountsSettingsPanel> _createAccountsPanel;
    private System.Windows.Controls.Button? _currentSelectedButton;
    private Action? _pendingNavigation;
    private Func<UIElement>? _currentPanelFactory;
    private string _currentTitle = string.Empty;
    private readonly System.Windows.Threading.DispatcherTimer _savedTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    private static readonly Duration NavigationDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly KeySpline NavigationSpline = new(.2, .8, .2, 1);

    public SettingsWindow(ConfigManager configManager, IAppShell shell)
        : this(configManager, shell, () => new GeneralSettingsPanel(configManager, shell))
    {
    }

    internal SettingsWindow(ConfigManager configManager, IAppShell shell, Func<GeneralSettingsPanel> createGeneralPanel,
        Func<ServiceAccountTarget?, AccountsSettingsPanel>? createAccountsPanel = null)
    {
        _createGeneralPanel = createGeneralPanel;
        _createAccountsPanel = createAccountsPanel ?? (target => new AccountsSettingsPanel(configManager, target));
        InitializeComponent();
        _configManager = configManager;
        _shell = shell;
        _savedTimer.Tick += (_, _) => { _savedTimer.Stop(); saveStatus.Text = string.Empty; };
        AddHandler(SettingsFeedback.ChangedEvent, new RoutedPropertyChangedEventHandler<string>((_, e) =>
        {
            _savedTimer.Stop();
            saveStatus.Text = e.NewValue;
            if (CurrentAutoSave?.HasSaveError != true) _savedTimer.Start();
            saveFailure.Visibility = CurrentAutoSave?.HasSaveError == true ? Visibility.Visible : Visibility.Collapsed;
            saveError.Text = CurrentAutoSave?.SaveErrorSummary ?? string.Empty;
            saveError.ToolTip = CurrentAutoSave?.SaveError;
            e.Handled = true;
        }));
        ApplyLayoutMetrics();
        Loaded += (_, _) => UpdateNavigationSlider(animate: false);
        SizeChanged += (_, _) => CloseFloatingControls();

        // 默认显示通用设置
        ShowGeneralSettings();
    }

    private void BtnGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneralSettings();
    }

    private void Brand_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowGeneralSettings();

    private void BtnClipboardSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("剪贴板", btnClipboardSettings, () => new ClipboardSettingsPanel(_configManager, _shell));
    }

    private void BtnAccountsSettings_Click(object sender, RoutedEventArgs e) => ShowAccountsSettings();

    /// <summary>OCR/翻译页提示"未配置账号"时跳转到服务账号页。</summary>
    public void ShowAccountsSettings()
    {
        ShowPanel("服务账号", btnAccountsSettings, () => _createAccountsPanel(null));
    }

    private void OpenAccount(ServiceAccountTarget target)
    {
        if (!ShowPanel("服务账号", btnAccountsSettings, () => _createAccountsPanel(target)))
        {
            _pendingNavigation = () => OpenAccount(target);
        }
    }

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
        CloseFloatingControls();
        e.Handled = true;
    }

    private void ContentScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentPanelWidth();
    }

    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.HorizontalChange != 0) CloseFloatingControls();
    }

    private void SelectNavigationButton(System.Windows.Controls.Button button)
    {
        // 清除之前的选中状态
        if (_currentSelectedButton != null)
            NavigationSelection.SetIsSelected(_currentSelectedButton, false);

        NavigationSelection.SetIsSelected(button, true);
        _currentSelectedButton = button;
        UpdateNavigationSlider(animate: IsLoaded && navigationSlider.Visibility == Visibility.Visible);
    }

    private void NavigationTabs_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateNavigationSlider(animate: false);

    private void UpdateNavigationSlider(bool animate)
    {
        if (_currentSelectedButton is not { ActualWidth: > 0 } button) return;
        var target = button.TranslatePoint(new System.Windows.Point(), navigationTabs).X;
        SegmentedSliderMotion.MoveTo(navigationSlider, navigationTranslate, navigationScale,
            target, button.ActualWidth, animate, NavigationDuration, NavigationSpline, animateLayoutWidth: true);
        navigationSlider.Visibility = Visibility.Visible;
    }

    private void CloseFloatingControls()
    {
        foreach (var element in Descendants(contentPanel))
        {
            if (element is System.Windows.Controls.ComboBox combo) combo.IsDropDownOpen = false;
            if (element is FrameworkElement { ToolTip: ToolTip tip }) tip.IsOpen = false;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private void ShowGeneralSettings()
    {
        ShowPanel("通用", btnGeneralSettings, () => _createGeneralPanel());
    }

    private void ShowOcrSettings()
    {
        ShowPanel("文字识别", btnOcrSettings, () => new OcrSettingsPanel(_configManager, OpenAccount));
    }

    private void ShowTranslationSettings()
    {
        ShowPanel("翻译", btnTranslationSettings, () => new TranslationSettingsPanel(_configManager, OpenAccount));
    }

    private SettingsAutoSaveController? CurrentAutoSave => contentPanel.Children.Count > 0
        ? SettingsAutoSaveController.GetController(contentPanel.Children[0])
        : null;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        _savedTimer.Stop();
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
        CloseFloatingControls();
        var panel = createPanel();
        Title = $"STool 设置 · {title}";
        saveStatus.Text = string.Empty;
        saveStatus.ToolTip = null;
        saveFailure.Visibility = Visibility.Collapsed;
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
        contentScroll.ScrollToVerticalOffset(0);
        return true;
    }

    private void ApplyLayoutMetrics()
    {
        contentPanel.Margin = new Thickness(
            SettingsLayout.ContentHorizontalMargin,
            SettingsLayout.ContentTopMargin,
            SettingsLayout.ContentHorizontalMargin,
            22);
        UpdateContentPanelWidth();
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

}
