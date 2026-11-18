using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;

namespace STool.Views.Settings;

/// <summary>菜单内容对齐字段右侧；滚动时关闭，沿用原生 Popup 的屏幕边界处理。</summary>
public static class SettingsPickerPlacement
{
    private const double ShadowLeft = 46;
    private const double ShadowTop = 24;
    private const double ShadowBottom = 50;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SettingsPickerPlacement), new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty MenuAnchorProperty = DependencyProperty.RegisterAttached(
        "MenuAnchor", typeof(FrameworkElement), typeof(SettingsPickerPlacement));
    public static readonly DependencyProperty MenuMinWidthProperty = DependencyProperty.RegisterAttached(
        "MenuMinWidth", typeof(double), typeof(SettingsPickerPlacement), new PropertyMetadata(120d));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static FrameworkElement? GetMenuAnchor(DependencyObject element) => (FrameworkElement?)element.GetValue(MenuAnchorProperty);
    public static void SetMenuAnchor(DependencyObject element, FrameworkElement? value) => element.SetValue(MenuAnchorProperty, value);
    public static double GetMenuMinWidth(DependencyObject element) => (double)element.GetValue(MenuMinWidthProperty);
    public static void SetMenuMinWidth(DependencyObject element, double value) => element.SetValue(MenuMinWidthProperty, value);

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var combo = (ComboBox)sender;
        if ((bool)args.NewValue)
        {
            combo.DropDownOpened += DropDownOpened;
            combo.PreviewKeyDown += PreviewKeyDown;
            combo.Loaded += Loaded;
            combo.Unloaded += Unloaded;
        }
        else
        {
            combo.DropDownOpened -= DropDownOpened;
            combo.PreviewKeyDown -= PreviewKeyDown;
            combo.Loaded -= Loaded;
            combo.Unloaded -= Unloaded;
        }
    }

    private static void PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var combo = (ComboBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!combo.IsDropDownOpen)
        {
            if (combo.IsEditable)
            {
                // The model field is a text editor; only the explicit fetch button opens its menu.
                if (key is Key.Up or Key.Down or Key.F4) e.Handled = true;
            }
            else if (key is Key.Enter or Key.Space or Key.Up or Key.Down or Key.F4)
            {
                combo.IsDropDownOpen = true;
                e.Handled = true;
            }
            return;
        }

        if (key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            if (combo.Items.Count == 0) return;
            var focused = Keyboard.FocusedElement as DependencyObject;
            var item = focused == null ? null : ItemsControl.ContainerFromElement(combo, focused) as ComboBoxItem;
            var index = item == null ? Math.Max(combo.SelectedIndex, 0) : combo.ItemContainerGenerator.IndexFromContainer(item);
            var next = key switch
            {
                Key.Home => 0,
                Key.End => combo.Items.Count - 1,
                Key.Down => (index + 1) % combo.Items.Count,
                _ => (index + combo.Items.Count - 1) % combo.Items.Count
            };
            if (combo.ItemContainerGenerator.ContainerFromIndex(next) is ComboBoxItem candidate)
                candidate.Focus();
            e.Handled = true;
        }
        else if (key is Key.Enter or Key.Space && Keyboard.FocusedElement is ComboBoxItem selected)
        {
            combo.SelectedItem = selected;
            combo.IsDropDownOpen = false;
            (GetMenuAnchor(combo) ?? combo).Focus();
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            combo.IsDropDownOpen = false;
            (GetMenuAnchor(combo) ?? combo).Focus();
            e.Handled = true;
        }
        else if (key == Key.Tab)
        {
            combo.IsDropDownOpen = false;
            var anchor = GetMenuAnchor(combo) ?? combo;
            anchor.Focus();
            anchor.MoveFocus(new TraversalRequest(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private static void DropDownOpened(object? sender, EventArgs args)
    {
        var combo = (ComboBox)sender!;
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        var anchor = GetMenuAnchor(combo) ?? combo;
        SetMenuMinWidth(combo, Math.Clamp(anchor.ActualWidth, 120, 330));
        popup.PlacementTarget = anchor;
        popup.CustomPopupPlacementCallback = (size, target, offset) =>
        [
            new CustomPopupPlacement(new System.Windows.Point(target.Width - size.Width + ShadowLeft, target.Height + 5 - ShadowTop), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new System.Windows.Point(target.Width - size.Width + ShadowLeft, -size.Height - 5 + ShadowBottom), PopupPrimaryAxis.Horizontal)
        ];
        combo.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!combo.IsDropDownOpen) return;
            if (combo.ItemContainerGenerator.ContainerFromIndex(Math.Max(combo.SelectedIndex, 0)) is ComboBoxItem selected)
                selected.Focus();
        });
    }

    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (FindScrollViewer((ComboBox)sender) is { } viewer) viewer.ScrollChanged += ScrollChanged;
    }

    private static void Unloaded(object sender, RoutedEventArgs args)
    {
        var combo = (ComboBox)sender;
        combo.IsDropDownOpen = false;
        if (FindScrollViewer(combo) is { } viewer) viewer.ScrollChanged -= ScrollChanged;
    }

    private static void ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.VerticalChange == 0 && args.HorizontalChange == 0) return;
        ClosePickers((DependencyObject)sender);
    }

    private static void ClosePickers(DependencyObject element)
    {
        if (element is ComboBox combo) combo.IsDropDownOpen = false;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            ClosePickers(VisualTreeHelper.GetChild(element, i));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }
}
