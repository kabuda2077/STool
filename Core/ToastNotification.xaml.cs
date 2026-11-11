using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace STool.Core
{
    public partial class ToastNotification : Window
    {
        private const string SettingsSavedKey = "settings-saved";
        private DispatcherTimer? _timer;
        private Action? _action;
        private const int DEFAULT_DURATION = 2500; // 毫秒
        private const double ToastGap = 12;

        private static readonly System.Collections.Generic.Dictionary<string, ToastNotification> ActiveByKey = new();
        private static readonly System.Collections.Generic.List<ToastNotification> VisibleToasts = new();

        public enum ToastType
        {
            Success,
            Info,
            Warning,
            Error
        }

        public enum ToastPlacement
        {
            BottomCenter,
            BottomRight
        }

        private ToastPlacement _placement = ToastPlacement.BottomCenter;

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
                Top = owner.Top + owner.ActualHeight - ActualHeight - 44;
                Left = _placement == ToastPlacement.BottomRight
                    ? owner.Left + owner.ActualWidth - ActualWidth - 24
                    : owner.Left + (owner.ActualWidth - ActualWidth) / 2;
            }
            else
            {
                var workArea = SystemParameters.WorkArea;
                Top = workArea.Bottom - ActualHeight - 44;
                Left = _placement == ToastPlacement.BottomRight
                    ? workArea.Right - ActualWidth - 24
                    : workArea.Left + (workArea.Width - ActualWidth) / 2;
            }

            // 并发提示向上堆叠，避免互相遮挡。
            lock (VisibleToasts)
            {
                foreach (var other in VisibleToasts)
                {
                    if (ReferenceEquals(other, this) || !other.IsVisible)
                        continue;
                    Top -= other.ActualHeight + ToastGap;
                }
                VisibleToasts.Add(this);
            }

            Closed += (_, _) =>
            {
                lock (VisibleToasts)
                    VisibleToasts.Remove(this);
            };

            if (MotionSettings.ShouldReduceMotion)
            {
                rootBorder.Opacity = 1;
                if (rootBorder.RenderTransform is TranslateTransform transform)
                {
                    transform.Y = 0;
                }
                return;
            }

            // Entry animation (Fade-In + Slide-Up)
            var sb = new System.Windows.Media.Animation.Storyboard();
            
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, MotionSettings.EnterDuration)
            {
                EasingFunction = MotionSettings.EaseOut
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeIn, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            
            var slideUp = new System.Windows.Media.Animation.DoubleAnimation(20, 0, MotionSettings.EnterDuration)
            {
                EasingFunction = MotionSettings.EaseOut
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

            if (MotionSettings.ShouldReduceMotion)
            {
                base.Close();
                return;
            }

            var sb = new System.Windows.Media.Animation.Storyboard();

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(rootBorder.Opacity, 0, MotionSettings.ExitDuration)
            {
                EasingFunction = MotionSettings.EaseIn
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeOut, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));

            var slideUpFurther = new System.Windows.Media.Animation.DoubleAnimation(0, -15, MotionSettings.ExitDuration)
            {
                EasingFunction = MotionSettings.EaseIn
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(slideUpFurther, rootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(slideUpFurther, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

            sb.Children.Add(fadeOut);
            sb.Children.Add(slideUpFurther);
            sb.Completed += (s, e) => base.Close();
            sb.Begin();
        }

        /// <summary>设置自动保存后的轻量提示；连续保存只延长同一条提示，不会堆叠。</summary>
        public static void ShowSettingsSaved() =>
            Show("设置已保存", type: ToastType.Success, duration: 1600, dedupeKey: SettingsSavedKey);

        public static void Show(
            string title,
            string message = "",
            ToastType type = ToastType.Success,
            int duration = DEFAULT_DURATION,
            string? actionText = null,
            Action? action = null,
            string? dedupeKey = null,
            ToastPlacement placement = ToastPlacement.BottomCenter)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // 同一去重键的提示仍在显示时只重置计时，避免连续操作刷出一串相同的 Toast。
                if (dedupeKey != null &&
                    ActiveByKey.TryGetValue(dedupeKey, out var active) &&
                    active.IsVisible)
                {
                    active.ResetTimer(duration);
                    return;
                }

                var toast = new ToastNotification();
                toast._placement = placement;
                toast.titleText.Text = title;

                if (!string.IsNullOrWhiteSpace(message))
                {
                    toast.messageText.Text = message;
                    toast.messageText.Visibility = Visibility.Visible;
                }

                if (!string.IsNullOrWhiteSpace(actionText) && action != null)
                {
                    toast._action = action;
                    toast.actionButton.Content = actionText;
                    toast.actionButton.Visibility = Visibility.Visible;
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

                if (dedupeKey != null)
                {
                    ActiveByKey[dedupeKey] = toast;
                    toast.Closed += (_, _) =>
                    {
                        if (ActiveByKey.TryGetValue(dedupeKey, out var current) && ReferenceEquals(current, toast))
                            ActiveByKey.Remove(dedupeKey);
                    };
                }

                toast.Show();
                toast.ResetTimer(duration);
            });
        }

        private void ResetTimer(int duration)
        {
            _timer?.Stop();
            _timer ??= new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(duration);
            _timer.Tick -= Timer_Tick;
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            _timer?.Stop();
            Close();
        }

        public static void ShowWithAction(
            string title,
            string message,
            string actionText,
            Action action,
            ToastType type = ToastType.Success,
            int duration = 5000,
            ToastPlacement placement = ToastPlacement.BottomCenter)
        {
            Show(title, message, type, duration, actionText, action, placement: placement);
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            var action = _action;
            _action = null;
            try
            {
                action?.Invoke();
            }
            finally
            {
                Close();
            }
        }

    }
}
