using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace STool.Core
{
    public partial class ToastNotification : Window
    {
        private DispatcherTimer? _timer;
        private const int DEFAULT_DURATION = 2500; // 毫秒

        public enum ToastType
        {
            Success,
            Info,
            Warning,
            Error
        }

        public ToastNotification()
        {
            InitializeComponent();
            Loaded += ToastNotification_Loaded;
        }

        private void ToastNotification_Loaded(object sender, RoutedEventArgs e)
        {
            var owner = System.Windows.Application.Current.Windows
                .OfType<Window>()
                .Where(w => w.IsVisible && !ReferenceEquals(w, this))
                .OrderByDescending(w => w.IsActive)
                .FirstOrDefault();

            if (owner != null)
            {
                Left = owner.Left + (owner.ActualWidth - ActualWidth) / 2;
                Top = owner.Top + owner.ActualHeight - ActualHeight - 44;
            }
            else
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
                Top = workArea.Bottom - ActualHeight - 44;
            }

            // Entry animation (Fade-In + Slide-Up)
            var sb = new System.Windows.Media.Animation.Storyboard();
            
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeIn, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            
            var slideUp = new System.Windows.Media.Animation.DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(slideUp, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(slideUp, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            
            sb.Children.Add(fadeIn);
            sb.Children.Add(slideUp);
            sb.Begin();
        }

        private bool _isClosing = false;
        public new void Close()
        {
            if (_isClosing) return;
            _isClosing = true;

            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            var sb = new System.Windows.Media.Animation.Storyboard();

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(rootBorder.Opacity, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeOut, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));

            var slideUpFurther = new System.Windows.Media.Animation.DoubleAnimation(0, -15, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(slideUpFurther, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(slideUpFurther, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

            sb.Children.Add(fadeOut);
            sb.Children.Add(slideUpFurther);
            sb.Completed += (s, e) => base.Close();
            sb.Begin();
        }

        public static void Show(string title, string message = "", ToastType type = ToastType.Success, int duration = DEFAULT_DURATION)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var toast = new ToastNotification();
                toast.titleText.Text = title;

                if (!string.IsNullOrWhiteSpace(message))
                {
                    toast.messageText.Text = message;
                    toast.messageText.Visibility = Visibility.Visible;
                }

                // 根据类型设置图标和颜色
                switch (type)
                {
                    case ToastType.Success:
                        toast.iconText.Text = ""; // CheckMark
                        toast.iconText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SuccessBrush");
                        break;
                    case ToastType.Info:
                        toast.iconText.Text = ""; // Info
                        toast.iconText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PrimaryBrush");
                        break;
                    case ToastType.Warning:
                        toast.iconText.Text = ""; // Warning
                        toast.iconText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PrimaryBrush");
                        break;
                    case ToastType.Error:
                        toast.iconText.Text = ""; // Error
                        toast.iconText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ErrorBrush");
                        break;
                }

                toast.Show();

                // 自动关闭定时器
                toast._timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(duration)
                };
                toast._timer.Tick += (s, e) =>
                {
                    toast._timer.Stop();
                    toast.Close();
                };
                toast._timer.Start();
            });
        }

    }
}
