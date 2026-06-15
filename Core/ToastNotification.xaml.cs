using System;
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
            // 定位到屏幕右下角。Border 外留 20px 给阴影、期望卡片距边缘 20px,二者抵消。
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth;
            Top = workArea.Bottom - ActualHeight;
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
                        toast.iconBorder.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PrimaryBrush");
                        break;
                    case ToastType.Warning:
                        toast.iconText.Text = ""; // Warning
                        toast.iconBorder.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PrimaryBrush");
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

    }
}
