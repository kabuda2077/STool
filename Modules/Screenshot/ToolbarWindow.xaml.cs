using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace STool.Modules.Screenshot;

public partial class ToolbarWindow : Window
{
    private readonly Bitmap _screenshot;
    private readonly System.Drawing.Rectangle _selectionBounds;

    public ToolbarWindow(Bitmap screenshot, System.Drawing.Rectangle selectionBounds)
    {
        InitializeComponent();

        _screenshot = screenshot;
        _selectionBounds = selectionBounds;

        // 定位工具条
        PositionToolbar();
    }

    private void PositionToolbar()
    {
        // 计算工具条位置（选区下方居中）
        var screenBounds = ScreenCapture.GetVirtualScreenBounds();
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        // 工具条宽度（估算）
        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = (int)DesiredSize.Width;
        var toolbarHeight = (int)DesiredSize.Height;

        // 选区中心X坐标
        var centerX = (_selectionBounds.X + _selectionBounds.Width / 2.0) * transform.M11;

        // 默认在选区下方
        var left = centerX - toolbarWidth / 2.0;
        var top = _selectionBounds.Bottom * transform.M22 + 10;

        // 检查是否超出屏幕底部
        var screenBottom = screenBounds.Bottom * transform.M22;
        var screenLeft = screenBounds.Left * transform.M11;
        var screenRight = screenBounds.Right * transform.M11;
        if (top + toolbarHeight > screenBottom)
        {
            // 翻转到选区上方
            top = _selectionBounds.Top * transform.M22 - toolbarHeight - 10;
        }

        // 确保不超出屏幕左右边界
        left = Math.Max(screenLeft + 10, Math.Min(left, screenRight - toolbarWidth - 10));

        Left = left;
        Top = top;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 默认保存路径
            var picturesPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var stoolPath = Path.Combine(picturesPath, "STool");
            Directory.CreateDirectory(stoolPath);

            var fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(stoolPath, fileName);

            _screenshot.Save(filePath, ImageFormat.Png);

            System.Windows.MessageBox.Show($"截图已保存到：\n{filePath}", "STool", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "STool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 转换为 WPF BitmapSource
            using var memory = new MemoryStream();
            _screenshot.Save(memory, ImageFormat.Png);
            memory.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = memory;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            System.Windows.Clipboard.SetImage(bitmapImage);

            System.Windows.MessageBox.Show("截图已复制到剪贴板", "STool", MessageBoxButton.OK, MessageBoxImage.Information);

            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"复制失败：{ex.Message}", "STool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnAnnotate_Click(object sender, RoutedEventArgs e)
    {
        // 打开标注编辑器
        var editor = new AnnotationEditor(CloneBitmap(_screenshot));

        editor.AnnotationCompleted += (s, annotatedBitmap) =>
        {
            // 用标注后的图片替换原图
            var toolbar = new ToolbarWindow(annotatedBitmap, _selectionBounds);
            toolbar.Show();
            Close();
        };

        editor.AnnotationCancelled += (s, ev) =>
        {
            // 取消标注，返回工具条
            Show();
        };

        Hide();
        editor.Show();
    }

    private async void BtnOcr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 禁用按钮防止重复点击
            btnOcr.IsEnabled = false;
            btnOcr.Content = new System.Windows.Controls.TextBlock { Text = "...", FontSize = 13, FontWeight = FontWeights.SemiBold };

            // 获取 OcrManager
            var ocrManager = ((App)System.Windows.Application.Current)
                .GetService<STool.Modules.Ocr.OcrManager>();

            if (ocrManager == null)
            {
                System.Windows.MessageBox.Show("OCR 服务未初始化", "STool",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 执行 OCR
            var result = await ocrManager.RecognizeAsync(_screenshot);

            // 显示结果窗口
            var resultWindow = new STool.Modules.Ocr.OcrResultWindow(result.FullText, result.Provider);
            resultWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"OCR 失败: {ex.Message}", "STool",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 恢复按钮
            btnOcr.IsEnabled = true;
            btnOcr.Content = new System.Windows.Controls.TextBlock { Text = "OCR", FontSize = 10, FontWeight = FontWeights.SemiBold };
        }
    }

    private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 禁用按钮
            btnTranslate.IsEnabled = false;
            btnTranslate.Content = new System.Windows.Controls.TextBlock { Text = "...", FontSize = 13, FontWeight = FontWeights.SemiBold };

            // 获取服务
            var ocrManager = ((App)System.Windows.Application.Current)
                .GetService<STool.Modules.Ocr.OcrManager>();
            var translationManager = ((App)System.Windows.Application.Current)
                .GetService<STool.Modules.Translation.TranslationManager>();

            if (ocrManager == null || translationManager == null)
            {
                System.Windows.MessageBox.Show("OCR 或翻译服务未初始化", "STool",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 执行 OCR
            var ocrResult = await ocrManager.RecognizeAsync(_screenshot);

            if (!ocrResult.Success || string.IsNullOrWhiteSpace(ocrResult.FullText))
            {
                System.Windows.MessageBox.Show($"OCR 识别失败: {ocrResult.ErrorMessage}", "STool",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 执行翻译
            var translationResult = await translationManager.TranslateAsync(ocrResult.FullText);

            if (!translationResult.Success)
            {
                System.Windows.MessageBox.Show($"翻译失败: {translationResult.ErrorMessage}", "STool",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 显示翻译结果窗口（简化版）
            var resultWindow = new STool.Modules.Translation.InPlaceTranslationWindow(
                ocrResult.FullText,
                translationResult.TranslatedText,
                translationResult.Provider
            );
            resultWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"原位翻译失败: {ex.Message}", "STool",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 恢复按钮
            btnTranslate.IsEnabled = true;
            btnTranslate.Content = new System.Windows.Controls.TextBlock { Text = "TR", FontSize = 11, FontWeight = FontWeights.SemiBold };
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        // 创建钉图窗口
        var pinWindow = new PinWindow(CloneBitmap(_screenshot));
        pinWindow.Show();
        Close();
    }

    private static Bitmap CloneBitmap(Bitmap bitmap)
    {
        return bitmap.Clone(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), bitmap.PixelFormat);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenshot?.Dispose();
        base.OnClosed(e);
    }
}
