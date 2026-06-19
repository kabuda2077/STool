using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STool.Modules.Screenshot.Annotations;

namespace STool.Modules.Screenshot;

public partial class AnnotationEditor : Window
{
    private readonly Bitmap _screenshot;
    private readonly AnnotationCanvas _annotationManager;
    private AnnotationTool _selectedTool = AnnotationTool.None;

    public event EventHandler<Bitmap>? AnnotationCompleted;
    public event EventHandler? AnnotationCancelled;

    public AnnotationEditor(Bitmap screenshot)
    {
        InitializeComponent();

        _screenshot = screenshot;

        // 设置窗口大小
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        // 显示截图
        screenshotImage.Source = BitmapInterop.ToBitmapSource(screenshot);

        // 初始化标注管理器
        _annotationManager = new AnnotationCanvas(annotationCanvas);
    }

    private void BtnRectangle_Click(object sender, RoutedEventArgs e)
    {
        SelectTool(AnnotationTool.Rectangle);
    }

    private void BtnEllipse_Click(object sender, RoutedEventArgs e)
    {
        SelectTool(AnnotationTool.Ellipse);
    }

    private void BtnArrow_Click(object sender, RoutedEventArgs e)
    {
        SelectTool(AnnotationTool.Arrow);
    }

    private void BtnPen_Click(object sender, RoutedEventArgs e)
    {
        SelectTool(AnnotationTool.Pen);
    }

    private void SelectTool(AnnotationTool tool)
    {
        _selectedTool = tool;
        _annotationManager.CurrentTool = tool;

        // 更新按钮状态（可选：添加视觉反馈）
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        _annotationManager.Undo();
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        _annotationManager.Redo();
    }

    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        // 合成标注到截图
        var annotatedBitmap = RenderAnnotatedBitmap();
        AnnotationCompleted?.Invoke(this, annotatedBitmap);
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        AnnotationCancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private Bitmap RenderAnnotatedBitmap()
    {
        // 创建渲染目标
        var width = (int)screenshotImage.ActualWidth;
        var height = (int)screenshotImage.ActualHeight;

        if (width == 0 || height == 0)
        {
            width = _screenshot.Width;
            height = _screenshot.Height;
        }

        var renderTarget = new RenderTargetBitmap(
            width,
            height,
            96, 96,
            PixelFormats.Pbgra32
        );

        // 创建包含图片和标注的视觉树
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // 绘制原始截图
            context.DrawImage(screenshotImage.Source, new Rect(0, 0, width, height));

            // 绘制标注画布
            var brush = new VisualBrush(annotationCanvas);
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        renderTarget.Render(visual);

        // 转换为 Bitmap
        return BitmapSourceToBitmap(renderTarget);
    }

    private Bitmap BitmapSourceToBitmap(BitmapSource bitmapSource)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

        using var memoryStream = new MemoryStream();
        encoder.Save(memoryStream);
        memoryStream.Position = 0;

        return new Bitmap(memoryStream);
    }

    protected override void OnClosed(EventArgs e)
    {
        _screenshot?.Dispose();
        base.OnClosed(e);
    }
}
