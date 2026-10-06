using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace STool.Modules.Screenshot.Annotations;

/// <summary>
/// 标注画布管理器
/// </summary>
public class AnnotationCanvas
{
    /// <summary>三档粗细：线条宽度、文字字号、马赛克笔刷直径（DIP）。</summary>
    public static readonly double[] StrokeSizes = { 2, 3, 5 };
    public static readonly double[] TextSizes = { 16, 20, 28 };
    public static readonly double[] MosaicSizes = { 16, 24, 40 };

    private readonly Canvas _canvas;
    private readonly Stack<IAnnotationCommand> _undoStack = new();
    private readonly Stack<IAnnotationCommand> _redoStack = new();

    private AnnotationTool _currentTool = AnnotationTool.None;
    private System.Windows.Media.Color _currentColor;
    private int _sizeLevel = 1;
    private ImageSource? _mosaicSource;
    private Rect _mosaicSourceRect;

    private System.Windows.Point _startPoint;
    private FrameworkElement? _currentElement;
    private System.Windows.Controls.TextBox? _editingText;
    private bool _isDrawing;

    public AnnotationCanvas(Canvas canvas)
    {
        _canvas = canvas;
        _currentColor = (System.Windows.Media.Color)canvas.FindResource("AnnotationDefaultColor");
        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    public AnnotationTool CurrentTool
    {
        get => _currentTool;
        set
        {
            if (_currentTool != value)
                CommitText();
            _currentTool = value;
        }
    }

    public System.Windows.Media.Color CurrentColor
    {
        get => _currentColor;
        set => _currentColor = value;
    }

    /// <summary>粗细档位 0~2。</summary>
    public int SizeLevel
    {
        get => _sizeLevel;
        set => _sizeLevel = Math.Clamp(value, 0, StrokeSizes.Length - 1);
    }

    public bool IsEditingText => _editingText != null;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// 设置马赛克图层：整屏预先像素化的位图，以及它在画布局部坐标中的位置（随选区移动而变化）。
    /// 已画的马赛克同步更新，保证预览与导出一致。
    /// </summary>
    public void SetMosaicSource(ImageSource? source, Rect sourceRect)
    {
        _mosaicSource = source;
        _mosaicSourceRect = sourceRect;
        foreach (var mosaic in _canvas.Children.OfType<MosaicAnnotation>())
        {
            mosaic.Width = _canvas.Width;
            mosaic.Height = _canvas.Height;
            mosaic.SetSource(source, sourceRect);
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool == AnnotationTool.None)
            return;

        _startPoint = e.GetPosition(_canvas);
        if (_currentTool == AnnotationTool.Text)
        {
            // 编辑中点击画布其他位置只提交当前文字，再次点击才开始新的文字。
            if (IsEditingText)
                CommitText();
            else
                StartText(_startPoint);
            e.Handled = true;
            return;
        }

        _isDrawing = true;
        _currentElement = CreateAnnotationElement(_currentTool);
        if (_currentElement == null)
            return;

        Canvas.SetLeft(_currentElement, _startPoint.X);
        Canvas.SetTop(_currentElement, _startPoint.Y);
        if (_currentElement is MosaicAnnotation mosaic)
        {
            mosaic.BrushSize = MosaicSizes[_sizeLevel];
            mosaic.Width = _canvas.ActualWidth;
            mosaic.Height = _canvas.ActualHeight;
            mosaic.SetSource(_mosaicSource, _mosaicSourceRect);
            Canvas.SetLeft(mosaic, 0);
            Canvas.SetTop(mosaic, 0);
            mosaic.AddPoint(_startPoint);
        }
        _canvas.Children.Add(_currentElement);
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDrawing || _currentElement == null)
            return;

        var currentPoint = e.GetPosition(_canvas);
        UpdateAnnotationElement(_currentElement, _startPoint, currentPoint);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing || _currentElement == null)
            return;

        _isDrawing = false;
        ExecuteCommand(new AddShapeCommand(_currentElement));
        _currentElement = null;
    }

    private FrameworkElement? CreateAnnotationElement(AnnotationTool tool)
    {
        var brush = new SolidColorBrush(_currentColor);
        var thickness = StrokeSizes[_sizeLevel];

        return tool switch
        {
            AnnotationTool.Rectangle => new System.Windows.Shapes.Rectangle
            {
                Stroke = brush,
                StrokeThickness = thickness,
                Fill = ResourceBrush("TransparentBrush")
            },
            AnnotationTool.Ellipse => new Ellipse
            {
                Stroke = brush,
                StrokeThickness = thickness,
                Fill = ResourceBrush("TransparentBrush")
            },
            AnnotationTool.Arrow => new ArrowAnnotation
            {
                Stroke = brush,
                StrokeThickness = thickness
            },
            AnnotationTool.Pen => new Polyline
            {
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            },
            AnnotationTool.Mosaic => new MosaicAnnotation(),
            _ => null
        };
    }

    private void StartText(System.Windows.Point point)
    {
        var color = new SolidColorBrush(_currentColor);
        var box = new System.Windows.Controls.TextBox
        {
            MinWidth = 40,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Background = ResourceBrush("TransparentBrush"),
            BorderBrush = ResourceBrush("PrimaryBrush"),
            BorderThickness = new Thickness(1),
            Foreground = color,
            CaretBrush = color,
            FontSize = TextSizes[_sizeLevel],
            Padding = new Thickness(2, 0, 2, 0)
        };
        Canvas.SetLeft(box, point.X);
        Canvas.SetTop(box, point.Y);
        box.LostKeyboardFocus += (_, _) => CommitText();
        box.PreviewKeyDown += (_, e) =>
        {
            // Enter 换行，Ctrl+Enter 完成输入。
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CommitText();
                e.Handled = true;
            }
        };

        _canvas.Children.Add(box);
        _editingText = box;
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(_editingText, box))
                return;
            box.Focus();
            Keyboard.Focus(box);
        }));
    }

    /// <summary>结束文字输入：非空时转成文字标注并加入撤销栈，空内容直接丢弃。</summary>
    public void CommitText()
    {
        var box = _editingText;
        if (box == null)
            return;

        _editingText = null;
        _canvas.Children.Remove(box);
        if (string.IsNullOrWhiteSpace(box.Text))
            return;

        var text = new TextBlock
        {
            Text = box.Text,
            Foreground = box.Foreground,
            FontSize = box.FontSize,
            FontFamily = box.FontFamily,
            // TextBox 内容区比外框多出边框与内边距，这里对齐到输入时的位置。
            Padding = new Thickness(box.Padding.Left + box.BorderThickness.Left + 2, box.BorderThickness.Top, 0, 0)
        };
        Canvas.SetLeft(text, Canvas.GetLeft(box));
        Canvas.SetTop(text, Canvas.GetTop(box));
        ExecuteCommand(new AddShapeCommand(text));
    }

    private System.Windows.Media.Brush ResourceBrush(string key)
        => (System.Windows.Media.Brush)_canvas.FindResource(key);

    private static void UpdateAnnotationElement(FrameworkElement element, System.Windows.Point start, System.Windows.Point current)
    {
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        switch (element)
        {
            case System.Windows.Shapes.Rectangle rect:
                Canvas.SetLeft(rect, left);
                Canvas.SetTop(rect, top);
                rect.Width = width;
                rect.Height = height;
                break;

            case Ellipse ellipse:
                Canvas.SetLeft(ellipse, left);
                Canvas.SetTop(ellipse, top);
                ellipse.Width = width;
                ellipse.Height = height;
                break;

            case ArrowAnnotation arrow:
                arrow.Start = start;
                arrow.End = current;
                Canvas.SetLeft(arrow, 0);
                Canvas.SetTop(arrow, 0);
                break;

            case Polyline polyline:
                if (polyline.Points.Count == 0)
                    polyline.Points.Add(start);
                polyline.Points.Add(current);
                Canvas.SetLeft(polyline, 0);
                Canvas.SetTop(polyline, 0);
                break;

            case MosaicAnnotation mosaic:
                mosaic.AddPoint(current);
                break;
        }
    }

    private void ExecuteCommand(IAnnotationCommand command)
    {
        command.Execute(_canvas);
        _undoStack.Push(command);
        _redoStack.Clear();
    }

    public void Undo()
    {
        CommitText();
        if (_undoStack.Count > 0)
        {
            var command = _undoStack.Pop();
            command.Undo(_canvas);
            _redoStack.Push(command);
        }
    }

    public void Redo()
    {
        CommitText();
        if (_redoStack.Count > 0)
        {
            var command = _redoStack.Pop();
            command.Execute(_canvas);
            _undoStack.Push(command);
        }
    }

    public void Clear()
    {
        _editingText = null;
        _canvas.Children.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
    }
}

public sealed class ArrowAnnotation : FrameworkElement
{
    public static readonly DependencyProperty StartProperty =
        DependencyProperty.Register(nameof(Start), typeof(System.Windows.Point), typeof(ArrowAnnotation),
            new FrameworkPropertyMetadata(default(System.Windows.Point), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndProperty =
        DependencyProperty.Register(nameof(End), typeof(System.Windows.Point), typeof(ArrowAnnotation),
            new FrameworkPropertyMetadata(default(System.Windows.Point), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(System.Windows.Media.Brush), typeof(ArrowAnnotation),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(ArrowAnnotation),
            new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Point Start
    {
        get => (System.Windows.Point)GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public System.Windows.Point End
    {
        get => (System.Windows.Point)GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public System.Windows.Media.Brush? Stroke
    {
        get => (System.Windows.Media.Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var stroke = Stroke;
        var dx = End.X - Start.X;
        var dy = End.Y - Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (stroke == null || length < 1)
            return;

        var pen = new System.Windows.Media.Pen(stroke, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        dc.DrawLine(pen, Start, End);

        var unitX = dx / length;
        var unitY = dy / length;
        var headLength = Math.Max(12, StrokeThickness * 4.5);
        var headWidth = Math.Max(7, StrokeThickness * 2.6);
        var basePoint = new System.Windows.Point(End.X - unitX * headLength, End.Y - unitY * headLength);
        var normalX = -unitY;
        var normalY = unitX;

        var left = new System.Windows.Point(basePoint.X + normalX * headWidth, basePoint.Y + normalY * headWidth);
        var right = new System.Windows.Point(basePoint.X - normalX * headWidth, basePoint.Y - normalY * headWidth);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(End, true, true);
            context.LineTo(left, true, true);
            context.LineTo(right, true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(stroke, null, geometry);
    }
}

/// <summary>
/// 马赛克笔迹：把预先像素化的整屏图层裁剪到笔刷经过的区域。预览和导出画的是同一张图层，
/// 块大小在任何 DPI 下都一致；每次加点只追加一段笔迹几何，不重算已有部分。
/// </summary>
public sealed class MosaicAnnotation : FrameworkElement
{
    /// <summary>马赛克块边长（DIP）。</summary>
    public const double BlockSize = 12;

    private readonly GeometryGroup _area = new() { FillRule = FillRule.Nonzero };
    private System.Windows.Point? _lastPoint;
    private System.Windows.Media.Pen? _pen;
    private ImageSource? _source;
    private Rect _sourceRect;

    public MosaicAnnotation()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public double BrushSize { get; set; } = 24;

    public void SetSource(ImageSource? source, Rect sourceRect)
    {
        _source = source;
        _sourceRect = sourceRect;
        InvalidateVisual();
    }

    public void AddPoint(System.Windows.Point point)
    {
        if (_lastPoint is System.Windows.Point last)
        {
            var dx = point.X - last.X;
            var dy = point.Y - last.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < Math.Max(2, BrushSize / 6))
                return;

            _pen ??= CreatePen();
            _area.Children.Add(new LineGeometry(last, point).GetWidenedPathGeometry(_pen));
        }
        else
        {
            _area.Children.Add(new EllipseGeometry(point, BrushSize / 2, BrushSize / 2));
        }

        _lastPoint = point;
        InvalidateVisual();
    }

    private System.Windows.Media.Pen CreatePen()
    {
        // 只用于计算笔迹轮廓，不参与绘制，因此不需要画刷。
        var pen = new System.Windows.Media.Pen(null, BrushSize)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_source == null || _area.Children.Count == 0)
            return;

        dc.PushClip(_area);
        dc.DrawImage(_source, _sourceRect);
        dc.Pop();
    }
}
