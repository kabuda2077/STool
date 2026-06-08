using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace STool.Core
{
    /// <summary>
    /// TextBox 水印附加属性
    /// </summary>
    public static class TextBoxWatermark
    {
        // 水印文本附加属性
        public static readonly DependencyProperty WatermarkProperty =
            DependencyProperty.RegisterAttached(
                "Watermark",
                typeof(string),
                typeof(TextBoxWatermark),
                new PropertyMetadata(string.Empty, OnWatermarkChanged));

        public static string GetWatermark(DependencyObject obj)
        {
            return (string)obj.GetValue(WatermarkProperty);
        }

        public static void SetWatermark(DependencyObject obj, string value)
        {
            obj.SetValue(WatermarkProperty, value);
        }

        private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is System.Windows.Controls.TextBox textBox)
            {
                textBox.Loaded -= TextBox_Loaded;
                textBox.Loaded += TextBox_Loaded;

                if (textBox.IsLoaded)
                {
                    UpdateWatermark(textBox);
                }
            }
        }

        private static void TextBox_Loaded(object sender, RoutedEventArgs e)
        {
            var textBox = (System.Windows.Controls.TextBox)sender;
            UpdateWatermark(textBox);

            textBox.TextChanged -= TextBox_TextChanged;
            textBox.TextChanged += TextBox_TextChanged;
            textBox.GotFocus -= TextBox_GotFocus;
            textBox.GotFocus += TextBox_GotFocus;
            textBox.LostFocus -= TextBox_LostFocus;
            textBox.LostFocus += TextBox_LostFocus;
        }

        private static void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateWatermark((System.Windows.Controls.TextBox)sender);
        }

        private static void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateWatermark((System.Windows.Controls.TextBox)sender);
        }

        private static void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateWatermark((System.Windows.Controls.TextBox)sender);
        }

        private static void UpdateWatermark(System.Windows.Controls.TextBox textBox)
        {
            var watermark = GetWatermark(textBox);
            if (string.IsNullOrEmpty(watermark))
                return;

            var adornerLayer = AdornerLayer.GetAdornerLayer(textBox);
            if (adornerLayer == null)
                return;

            // 移除旧的 Adorner
            var adorners = adornerLayer.GetAdorners(textBox);
            if (adorners != null)
            {
                foreach (var adorner in adorners)
                {
                    if (adorner is WatermarkAdorner)
                    {
                        adornerLayer.Remove(adorner);
                    }
                }
            }

            // 如果文本为空且未聚焦，添加水印
            if (string.IsNullOrEmpty(textBox.Text) && !textBox.IsFocused)
            {
                adornerLayer.Add(new WatermarkAdorner(textBox, watermark));
            }
        }

        /// <summary>
        /// 水印 Adorner
        /// </summary>
        private class WatermarkAdorner : Adorner
        {
            private readonly string _watermarkText;
            private readonly System.Windows.Controls.TextBox _textBox;

            public WatermarkAdorner(System.Windows.Controls.TextBox textBox, string watermarkText) : base(textBox)
            {
                _textBox = textBox;
                _watermarkText = watermarkText;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                var textColor = (SolidColorBrush?)_textBox.TryFindResource("TextSecondaryBrush")
                    ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)); // #94A3B8

                var formattedText = new FormattedText(
                    _watermarkText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface(_textBox.FontFamily, _textBox.FontStyle, _textBox.FontWeight, _textBox.FontStretch),
                    _textBox.FontSize,
                    textColor,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                // 计算水印位置（考虑 Padding）
                var left = _textBox.Padding.Left + 2;
                var top = (_textBox.ActualHeight - formattedText.Height) / 2;

                drawingContext.DrawText(formattedText, new System.Windows.Point(left, top));
            }
        }
    }
}
