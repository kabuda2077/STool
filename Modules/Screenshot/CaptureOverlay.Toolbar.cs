using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Serilog;
using STool.Modules.Screenshot.Annotations;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 工具栏与动作（标注工具、确认/保存/固定/OCR/翻译/取消）
/// </summary>
public partial class CaptureOverlay
{
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
            "Mosaic" => AnnotationTool.Mosaic,
            _ => AnnotationTool.None,
        };
        // 再次点击当前工具 → 取消(回到选区模式)
        _currentTool = _currentTool == tool ? AnnotationTool.None : tool;
        if (_annotation != null) _annotation.CurrentTool = _currentTool;

        annotationCanvas.IsHitTestVisible = _currentTool != AnnotationTool.None;
        UpdateCursorState();

        // 高亮当前工具按钮
        foreach (var b in _toolButtons)
            b.Tag = b.Tag; // 无操作占位
        HighlightTools();
        PositionHandles();
    }

    private void HighlightTools()
    {
        foreach (var b in _toolButtons)
        {
            var active = (b == btnRect && _currentTool == AnnotationTool.Rectangle)
                      || (b == btnEllipse && _currentTool == AnnotationTool.Ellipse)
                      || (b == btnArrow && _currentTool == AnnotationTool.Arrow)
                      || (b == btnPen && _currentTool == AnnotationTool.Pen)
                      || (b == btnMosaic && _currentTool == AnnotationTool.Mosaic);
            b.Background = active ? ResourceBrush("PrimarySoftBrush") : ResourceBrush("TransparentBrush");
        }
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => _annotation?.Undo();
    private void BtnRedo_Click(object sender, RoutedEventArgs e) => _annotation?.Redo();

    // ---------- 动作 ----------
    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private void CopyAndClose()
    {
        try
        {
            using var bmp = RenderSelectionBitmap();
            System.Windows.Clipboard.SetImage(ToBitmapSource(bmp));
        }
        catch { /* 忽略,直接关闭 */ }
        CloseOverlay();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        using var bmp = RenderSelectionBitmap();
        CloseOverlay();   // 先关取景窗,保存对话框显示在真实桌面上

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "STool");
        try { Directory.CreateDirectory(dir); } catch { }

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
            Core.ToastNotification.Show("截图已保存", dlg.FileName, Core.ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            Core.ToastNotification.Show("保存失败", ex.Message, Core.ToastNotification.ToastType.Error);
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        // 选区在屏幕上的 DIP 位置 = 覆盖窗口原点(虚拟屏左上)+ 窗口内 DIP 偏移
        var screenRect = new Rect(Left + _selection.X, Top + _selection.Y, _selection.Width, _selection.Height);
        CloseOverlay();
        new PinWindow(bmp, screenRect).Show();
    }

    private async void BtnOcr_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        CloseOverlay();
        var ocr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Ocr.OcrManager>();
        if (ocr == null) { Core.ToastNotification.Show("OCR 不可用", "服务未初始化", Core.ToastNotification.ToastType.Error); bmp.Dispose(); return; }
        try
        {
            var result = await ocr.RecognizeAsync(bmp);
            new STool.Modules.Ocr.OcrResultWindow(result.FullText, result.Provider).Show();
        }
        catch (Exception ex) { Core.ToastNotification.Show("OCR 失败", ex.Message, Core.ToastNotification.ToastType.Error); }
        finally { bmp.Dispose(); }
    }

    private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
    {
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
        ShowTranslationOverlay("翻译中...");
        var ocr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Ocr.OcrManager>();
        var tr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Translation.TranslationManager>();
        if (ocr == null || tr == null) { Core.ToastNotification.Show("翻译不可用", "服务未初始化", Core.ToastNotification.ToastType.Error); HideTranslationOverlay(); bmp.Dispose(); return; }
        try
        {
            var o = await ocr.RecognizeAsync(bmp, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!o.Success || string.IsNullOrWhiteSpace(o.FullText))
            { Core.ToastNotification.Show("OCR 失败", o.ErrorMessage ?? "未识别到文字", Core.ToastNotification.ToastType.Warning); HideTranslationOverlay(); return; }

            if (await TryShowBlockTranslationAsync(o, tr, bmp, cancellationToken))
                return;

            var t = await tr.TranslateAsync(o.FullText, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!t.Success) { Core.ToastNotification.Show("翻译失败", t.ErrorMessage ?? "", Core.ToastNotification.ToastType.Error); HideTranslationOverlay(); return; }
            ShowTranslationOverlay(t.TranslatedText);
        }
        catch (OperationCanceledException) { HideTranslationOverlay(); }
        catch (Exception ex) { HideTranslationOverlay(); Core.ToastNotification.Show("翻译失败", ex.Message, Core.ToastNotification.ToastType.Error); }
        finally
        {
            bmp.Dispose();
            currentTranslationCts.Dispose();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseOverlay(); return; }
        if (e.Key == Key.Enter) { CopyAndClose(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { _annotation?.Undo(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { _annotation?.Redo(); return; }
    }

}
