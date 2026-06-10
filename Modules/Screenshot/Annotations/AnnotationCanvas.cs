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

    private AnnotationTool _currentTool = AnnotationTool.None;
    private System.Windows.Media.Color _currentColor;
    private double _currentThickness = 3;

    private System.Windows.Point _startPoint;
    private Shape? _currentShape;
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

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool == AnnotationTool.None)
            return;

        _startPoint = e.GetPosition(_canvas);
        _isDrawing = true;

        _currentShape = CreateShape(_currentTool);
        if (_currentShape != null)
        {
            Canvas.SetLeft(_currentShape, _startPoint.X);
            Canvas.SetTop(_currentShape, _startPoint.Y);
            _canvas.Children.Add(_currentShape);
        }
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDrawing || _currentShape == null)
            return;

        var currentPoint = e.GetPosition(_canvas);
        UpdateShape(_currentShape, _startPoint, currentPoint);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing || _currentShape == null)
            return;

        _isDrawing = false;

        // 添加到命令栈
        var command = new AddShapeCommand(_currentShape);
        ExecuteCommand(command);

        _currentShape = null;
    }

    private Shape? CreateShape(AnnotationTool tool)
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
            AnnotationTool.Arrow => new Line
            {
                Stroke = brush,
                StrokeThickness = _currentThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Triangle
            },
            AnnotationTool.Pen => new Polyline
            {
                Stroke = brush,
                StrokeThickness = _currentThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            },
            _ => null
        };
    }

    private System.Windows.Media.Brush ResourceBrush(string key)
        => (System.Windows.Media.Brush)_canvas.FindResource(key);

    private void UpdateShape(Shape shape, System.Windows.Point start, System.Windows.Point current)
    {
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        switch (shape)
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

            case Line line:
                line.X1 = start.X;
                line.Y1 = start.Y;
                line.X2 = current.X;
                line.Y2 = current.Y;
                Canvas.SetLeft(line, 0);
                Canvas.SetTop(line, 0);
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
