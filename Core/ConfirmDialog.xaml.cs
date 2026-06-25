using System.Windows;

namespace STool.Core;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        titleText.Text = title;
        messageText.Text = message;
        confirmButton.Content = confirmText;
        cancelButton.Content = cancelText;
        Loaded += ConfirmDialog_Loaded;
    }

    private void ConfirmDialog_Loaded(object sender, RoutedEventArgs e)
    {
        var sb = new System.Windows.Media.Animation.Storyboard();

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, System.TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(fadeIn, dialogBorder);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));

        var scaleX = new System.Windows.Media.Animation.DoubleAnimation(0.95, 1.0, System.TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleX, dialogBorder);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

        var scaleY = new System.Windows.Media.Animation.DoubleAnimation(0.95, 1.0, System.TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, dialogBorder);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        sb.Children.Add(fadeIn);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        sb.Begin();
    }

    public static bool Show(Window owner, string title, string message, string confirmText = "确认", string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, confirmText, cancelText)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    private bool _isClosing = false;
    private void CloseWithResult(bool result)
    {
        if (_isClosing) return;
        _isClosing = true;

        var sb = new System.Windows.Media.Animation.Storyboard();

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(dialogBorder.Opacity, 0, System.TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(fadeOut, dialogBorder);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));

        var scaleX = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.95, System.TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleX, dialogBorder);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));

        var scaleY = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.95, System.TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, dialogBorder);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

        sb.Children.Add(fadeOut);
        sb.Children.Add(scaleX);
        sb.Children.Add(scaleY);
        
        sb.Completed += (s, e) =>
        {
            DialogResult = result;
        };
        sb.Begin();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        CloseWithResult(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            CloseWithResult(false);
            e.Handled = true;
        }
    }
}
