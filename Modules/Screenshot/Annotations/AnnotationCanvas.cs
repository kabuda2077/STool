using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace STool.Modules.Screenshot.Annotations;

/// <summary>
/// 标注画布管理器
/// </summary>
public class AnnotationCanvas
{
    private readonly Canvas _canvas;
    private readonly Stack<IAnnotationCommand> _undoStack = new();
    private readonly Stack<IAnnotationCommand> _redoStack = new();
    private Func<Rect, System.Windows.Media.Color>? _mosaicSampler;

    private AnnotationTool _currentTool = AnnotationTool.None;
    private System.Windows.Media.Color _currentColor;
    private double _currentThickness = 3;

    private System.Windows.Point _startPoint;
    private FrameworkElement? _currentElement;
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
        set => _currentTool = value;
    }

    public System.Windows.Media.Color CurrentColor
    {
        get => _currentColor;
        set => _currentColor = value;
    }

    public double CurrentThickness
    {
        get => _currentThickness;
        set => _currentThickness = value;
    }

    public Func<Rect, System.Windows.Media.Color>? MosaicSampler
    {
        get => _mosaicSampler;
        set
        {
            _mosaicSampler = value;
            foreach (var mosaic in _canvas.Children.OfType<MosaicAnnotation>())
                mosaic.SampleColor = value;
        }
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool == AnnotationTool.None)
            return;

        _startPoint = e.GetPosition(_canvas);
        _isDrawing = true;

        _currentElement = CreateAnnotationElement(_currentTool);
        if (_currentElement != null)
        {
            Canvas.SetLeft(_currentElement, _startPoint.X);
            Canvas.SetTop(_currentElement, _startPoint.Y);
            if (_currentElement is MosaicAnnotation mosaic)
            {
                mosaic.BrushSize = _currentThickness * 8;
                mosaic.Width = _canvas.ActualWidth;
                mosaic.Height = _canvas.ActualHeight;
                mosaic.SampleColor = _mosaicSampler;
                Canvas.SetLeft(mosaic, 0);
                Canvas.SetTop(mosaic, 0);
                mosaic.AddPoint(_startPoint);
            }
            _canvas.Children.Add(_currentElement);
        }
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

        // 添加到命令栈
        var command = new AddShapeCommand(_currentElement);
        ExecuteCommand(command);

        _currentElement = null;
    }

    private FrameworkElement? CreateAnnotationElement(AnnotationTool tool)
    {
        var brush = new SolidColorBrush(_currentColor);

        return tool switch
        {
            AnnotationTool.Rectangle => new System.Windows.Shapes.Rectangle
            {
                Stroke = brush,
                StrokeThickness = _currentThickness,
                Fill = ResourceBrush("TransparentBrush")
            },
            AnnotationTool.Ellipse => new Ellipse
            {
                Stroke = brush,
                StrokeThickness = _currentThickness,
                Fill = ResourceBrush("TransparentBrush")
            },
            AnnotationTool.Arrow => new ArrowAnnotation
            {
                Stroke = brush,
                StrokeThickness = _currentThickness
            },
            AnnotationTool.Pen => new Polyline
            {
                Stroke = brush,
                StrokeThickness = _currentThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            },
            AnnotationTool.Mosaic => new MosaicAnnotation(),
            _ => null
        };
    }

    private System.Windows.Media.Brush ResourceBrush(string key)
        => (System.Windows.Media.Brush)_canvas.FindResource(key);

    private void UpdateAnnotationElement(FrameworkElement element, System.Windows.Point start, System.Windows.Point current)
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
                {
                    polyline.Points.Add(start);
                }
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
        if (_undoStack.Count > 0)
        {
            var command = _undoStack.Pop();
            command.Undo(_canvas);
            _redoStack.Push(command);
        }
    }

    public void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var command = _redoStack.Pop();
            command.Execute(_canvas);
            _undoStack.Push(command);
        }
    }

    public void Clear()
    {
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
            new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public System.Windows.Media.Brush Stroke
    {
        get => (System.Windows.Media.Brush)GetValue(StrokeProperty);
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

        var dx = End.X - Start.X;
        var dy = End.Y - Start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return;

        var pen = new System.Windows.Media.Pen(Stroke, StrokeThickness)
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
        dc.DrawGeometry(Stroke, null, geometry);
    }
}

public sealed class MosaicAnnotation : FrameworkElement
{
    public const double BlockSize = 12;
    private readonly List<System.Windows.Point> _points = new();

    public IReadOnlyList<System.Windows.Point> Points => _points;

    public double BrushSize { get; set; } = 24;

    public Func<Rect, System.Windows.Media.Color>? SampleColor { get; set; }

    public void AddPoint(System.Windows.Point point)
    {
        if (_points.Count > 0)
        {
            var previous = _points[^1];
            var dx = point.X - previous.X;
            var dy = point.Y - previous.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < Math.Max(2, BrushSize / 6))
                return;
        }

        _points.Add(point);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (_points.Count == 0)
            return;

        var radius = BrushSize / 2;

        foreach (var point in _points)
        {
            var left = point.X - radius;
            var top = point.Y - radius;
            for (double y = top; y < top + BrushSize; y += BlockSize)
            {
                for (double x = left; x < left + BrushSize; x += BlockSize)
                {
                    var centerX = x + BlockSize / 2 - point.X;
                    var centerY = y + BlockSize / 2 - point.Y;
                    if (centerX * centerX + centerY * centerY > radius * radius)
                        continue;

                    var rect = new Rect(x, y, BlockSize, BlockSize);
                    var color = SampleColor?.Invoke(rect) ?? System.Windows.Media.Color.FromRgb(160, 160, 166);
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, rect);
                }
            }
        }
    }
}
