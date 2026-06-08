using System.Windows;
using System.Windows.Controls;
using STool.Core;
using STool.Views.Settings;

namespace STool.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigManager _configManager;
    private System.Windows.Controls.Button? _currentSelectedButton;

    public SettingsWindow(ConfigManager configManager)
    {
        InitializeComponent();
        _configManager = configManager;

        // 默认显示通用设置
        ShowGeneralSettings();
        SelectNavigationButton(btnGeneralSettings);
    }

    private void BtnGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowGeneralSettings();
        SelectNavigationButton(btnGeneralSettings);
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
        var panel = new GeneralSettingsPanel(_configManager);
        contentPanel.Children.Clear();
        contentPanel.Children.Add(panel);
    }

    private void ShowOcrSettings()
    {
        var panel = new OcrSettingsPanel(_configManager);
        contentPanel.Children.Clear();
        contentPanel.Children.Add(panel);
    }

    private void ShowTranslationSettings()
    {
        var panel = new TranslationSettingsPanel(_configManager);
        contentPanel.Children.Clear();
        contentPanel.Children.Add(panel);
    }
}
