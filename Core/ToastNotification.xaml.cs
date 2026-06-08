using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            // 定位到屏幕右下角
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - 20;
            Top = workArea.Bottom - ActualHeight - 20;

            // 淡入动画
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fadeIn);

            // 从下方滑入
            var slideIn = new DoubleAnimation
            {
                From = Top + 20,
                To = Top,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(TopProperty, slideIn);
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
                        toast.iconBorder.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SuccessBrush");
                        break;
                    case ToastType.Info:
                        toast.iconText.Text = ""; // Info
                        toast.iconBorder.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("InfoBrush");
                        break;
                    case ToastType.Warning:
                        toast.iconText.Text = ""; // Warning
                        toast.iconBorder.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarningBrush");
                        break;
                    case ToastType.Error:
                        toast.iconText.Text = ""; // Error
                        toast.iconBorder.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ErrorBrush");
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

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 淡出动画
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(150)
            };

            fadeOut.Completed += (s, args) =>
            {
                base.OnClosing(new System.ComponentModel.CancelEventArgs(false));
            };

            e.Cancel = true;
            BeginAnimation(OpacityProperty, fadeOut);
        }
    }
}
