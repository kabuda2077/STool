using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using STool.Core;
using STool.Modules.Screenshot.Annotations;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 工具栏与动作（标注工具、颜色粗细、确认/保存/固定/OCR/翻译/取消）
/// </summary>
public partial class CaptureOverlay
{
    private string _annotationColorKey = "AnnotationDefaultColor";

    private void PositionToolbar()
    {
        toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tbW = toolbar.DesiredSize.Width, tbH = toolbar.DesiredSize.Height;

        double tx = _selection.X + (_selection.Width - tbW) / 2;
        double ty = _selection.Bottom + 8;
        if (ty + tbH > ActualH - 2)            // 下方放不下 → 翻到上方
            ty = _selection.Y - tbH - 8;
        if (ty < 2)                             // 上方也放不下(选区贴顶/占满)→ 压在选区内底部
            ty = Math.Max(2, _selection.Bottom - tbH - 8);

        tx = Clamp(tx, 4, ActualW - tbW - 4);
        Canvas.SetLeft(toolbar, tx);
        Canvas.SetTop(toolbar, ty);
    }

    private bool IsOverToolbar(MouseButtonEventArgs e)
    {
        if (toolbar.Visibility != Visibility.Visible) return false;
        var p = e.GetPosition(toolbar);
        return p.X >= 0 && p.Y >= 0 && p.X <= toolbar.ActualWidth && p.Y <= toolbar.ActualHeight;
    }

    // ---------- 工具条:标注工具 ----------
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        var tool = tag switch
        {
            "Rectangle" => AnnotationTool.Rectangle,
            "Ellipse" => AnnotationTool.Ellipse,
            "Arrow" => AnnotationTool.Arrow,
            "Pen" => AnnotationTool.Pen,
            "Text" => AnnotationTool.Text,
            "Mosaic" => AnnotationTool.Mosaic,
            _ => AnnotationTool.None,
        };
        // 再次点击当前工具 → 取消(回到选区模式)
        _currentTool = _currentTool == tool ? AnnotationTool.None : tool;
        if (_annotation != null) _annotation.CurrentTool = _currentTool;
        if (_currentTool == AnnotationTool.Mosaic)
            EnsureMosaicLayer();

        annotationCanvas.IsHitTestVisible = _currentTool != AnnotationTool.None;
        UpdateCursorState();

        HighlightTools();
        UpdateAnnotationOptionsVisibility();
        PositionHandles();
        PositionToolbar();
    }

    private void HighlightTools()
    {
        foreach (var b in _toolButtons)
        {
            var active = (b == btnRect && _currentTool == AnnotationTool.Rectangle)
                      || (b == btnEllipse && _currentTool == AnnotationTool.Ellipse)
                      || (b == btnArrow && _currentTool == AnnotationTool.Arrow)
                      || (b == btnPen && _currentTool == AnnotationTool.Pen)
                      || (b == btnText && _currentTool == AnnotationTool.Text)
                      || (b == btnMosaic && _currentTool == AnnotationTool.Mosaic);
            b.Background = active ? ResourceBrush("PrimarySoftBrush") : ResourceBrush("TransparentBrush");
            b.Foreground = active ? ResourceBrush("PrimaryBrush") : ResourceBrush("TextPrimaryBrush");
        }
    }

    /// <summary>选中绘制工具时显示粗细与颜色；马赛克没有颜色，只显示粗细。</summary>
    private void UpdateAnnotationOptionsVisibility()
    {
        var hasTool = _currentTool != AnnotationTool.None;
        var hasColor = hasTool && _currentTool != AnnotationTool.Mosaic;
        annotationOptions.Visibility = hasTool ? Visibility.Visible : Visibility.Collapsed;
        annotationColors.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;
        annotationColorSeparator.Visibility = hasColor ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation == null || sender is not Button { Tag: string tag } || !int.TryParse(tag, out var level))
            return;

        _annotation.SizeLevel = level;
        HighlightAnnotationOptions();
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (_annotation == null || sender is not Button { Tag: string key })
            return;

        _annotation.CurrentColor = (System.Windows.Media.Color)FindResource(key);
        _annotationColorKey = key;
        HighlightAnnotationOptions();
    }

    private void HighlightAnnotationOptions()
    {
        var level = _annotation?.SizeLevel ?? 1;
        foreach (var (button, index) in new[] { btnSizeSmall, btnSizeMedium, btnSizeLarge }.Select((b, i) => (b, i)))
            button.Background = index == level ? ResourceBrush("PrimarySoftBrush") : ResourceBrush("TransparentBrush");

        foreach (var button in annotationColors.Children.OfType<Button>())
        {
            var selected = string.Equals(button.Tag as string, _annotationColorKey, StringComparison.Ordinal);
            button.Background = selected ? ResourceBrush("PrimarySoftBrush") : ResourceBrush("TransparentBrush");
        }
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => _annotation?.Undo();
    private void BtnRedo_Click(object sender, RoutedEventArgs e) => _annotation?.Redo();

    // ---------- 动作 ----------
    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private async void CopyAndClose()
    {
        try
        {
            using var bmp = RenderSelectionBitmap();
            var image = BitmapInterop.ToBitmapSource(bmp);
            await ClipboardWriter.SetImageAsync(image);
            CloseOverlay();
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveSelection();

    private void SaveSelection()
    {
        if (!_interactionReady || _closing)
            return;

        using var bmp = RenderSelectionBitmap();
        CloseOverlay();   // 先关取景窗,保存对话框显示在真实桌面上

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "STool");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to prepare screenshot save directory {Directory}", dir);
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Directory.Exists(dir) ? dir : string.Empty
        };

        if (dlg.ShowDialog() != true)
            return;   // 用户取消

        try
        {
            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            ToastNotification.Show("截图已保存", dlg.FileName, ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("保存失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        var screenRect = CoordinateMapper.CanvasToPhysical(_selection);
        CloseOverlay();
        try
        {
            new PinWindow(bmp, screenRect).Show();
        }
        catch (Exception ex)
        {
            bmp.Dispose();
            ToastNotification.Show("钉图失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }

    private async void BtnOcr_Click(object sender, RoutedEventArgs e)
    {
        if (!btnOcr.IsEnabled || _services == null)
            return;

        var bmp = RenderSelectionBitmap();
        using var request = new System.Threading.CancellationTokenSource();
        _ocrCts = request;
        btnOcr.IsEnabled = false;
        btnOcr.ToolTip = "正在识别...";
        try
        {
            var result = await _services.Ocr.RecognizeAsync(bmp, request.Token);
            if (_closing || request.IsCancellationRequested)
                return;
            if (!result.Success)
            {
                ToastNotification.Show("OCR 失败", result.ErrorMessage ?? "未识别到文字", ToastNotification.ToastType.Warning);
                return;
            }
            CloseOverlay();
            new STool.Modules.Ocr.OcrResultWindow(result.FullText, result.Provider).Show();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_closing)
                ToastNotification.Show("OCR 失败", ex.Message, ToastNotification.ToastType.Error);
        }
        finally
        {
            _ocrCts = null;
            bmp.Dispose();
            if (!_closing)
            {
                btnOcr.IsEnabled = true;
                btnOcr.ToolTip = "OCR 识别";
            }
        }
    }

    private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_services == null)
            return;

        if (IsTranslationOverlayVisible())
        {
            CancelCurrentTranslation();
            HideTranslationOverlay();
            return;
        }

        CancelCurrentTranslation();
        var currentTranslationCts = new System.Threading.CancellationTokenSource();
        _translationCts = currentTranslationCts;
        var cancellationToken = currentTranslationCts.Token;

        var bmp = RenderSelectionBitmap();
        btnTranslate.ToolTip = "取消翻译";
        ShowTranslationLoading();
        var ocr = _services.Ocr;
        var translation = _services.Translation;
        try
        {
            var translationTimer = System.Diagnostics.Stopwatch.StartNew();
            var o = await ocr.RecognizeAsync(bmp, cancellationToken);
            Log.Information(
                "[ScreenshotTranslate] OCR completed in {Ms}ms success={Success} provider={Provider}",
                translationTimer.ElapsedMilliseconds,
                o.Success,
                o.Provider);
            cancellationToken.ThrowIfCancellationRequested();
            if (!o.Success || string.IsNullOrWhiteSpace(o.FullText))
            {
                ToastNotification.Show("OCR 失败", o.ErrorMessage ?? "未识别到文字", ToastNotification.ToastType.Warning);
                HideTranslationOverlay();
                return;
            }

            var blockStart = translationTimer.ElapsedMilliseconds;
            if (await TryShowBlockTranslationAsync(o, translation, bmp, cancellationToken))
            {
                Log.Information(
                    "[ScreenshotTranslate] Overlay completed in {TotalMs}ms after OCR={AfterOcrMs}ms",
                    translationTimer.ElapsedMilliseconds,
                    translationTimer.ElapsedMilliseconds - blockStart);
                return;
            }

            var t = await translation.TranslateAsync(o.FullText, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!t.Success)
            {
                ToastNotification.Show("翻译失败", t.ErrorMessage ?? "", ToastNotification.ToastType.Error);
                HideTranslationOverlay();
                return;
            }
            ShowTranslationOverlay(t.TranslatedText);
        }
        catch (OperationCanceledException)
        {
            if (!_closing && ReferenceEquals(_translationCts, currentTranslationCts))
                HideTranslationOverlay();
        }
        catch (Exception ex)
        {
            if (!_closing && ReferenceEquals(_translationCts, currentTranslationCts))
            {
                HideTranslationOverlay();
                ToastNotification.Show("翻译失败", ex.Message, ToastNotification.ToastType.Error);
            }
        }
        finally
        {
            bmp.Dispose();
            var isCurrent = ReferenceEquals(_translationCts, currentTranslationCts);
            if (isCurrent)
                _translationCts = null;
            currentTranslationCts.Dispose();
            if (isCurrent && !_closing)
                UpdateTranslationToolTip();
        }
    }

    private void UpdateTranslationToolTip()
    {
        var mode = _services?.Translation.GetConfiguredScreenshotMode() == STool.Models.ScreenshotTranslationMode.Smart
            ? "智能"
            : "快速";
        btnTranslate.ToolTip = $"截图翻译（{mode}模式）";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 键盘快捷键主要由 WndProc 处理;这里兜底,文字输入时不拦截任何按键。
        if (_annotation?.IsEditingText == true)
            return;

        if (e.Key == Key.Escape) { CloseOverlay(); return; }
        if (e.Key == Key.Enter) { CopyAndClose(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { _annotation?.Undo(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { _annotation?.Redo(); return; }
    }
}
