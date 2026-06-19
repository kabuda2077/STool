using System.Windows.Controls;
using System.Windows;

namespace STool.Modules.Screenshot.Annotations;

/// <summary>
/// 标注命令接口
/// </summary>
public interface IAnnotationCommand
{
    void Execute(Canvas canvas);
    void Undo(Canvas canvas);
}

/// <summary>
/// 添加形状命令
/// </summary>
public class AddShapeCommand : IAnnotationCommand
{
    private readonly UIElement _element;

    public AddShapeCommand(UIElement element)
    {
        _element = element;
    }

    public void Execute(Canvas canvas)
    {
        if (!canvas.Children.Contains(_element))
        {
            canvas.Children.Add(_element);
        }
    }

    public void Undo(Canvas canvas)
    {
        canvas.Children.Remove(_element);
    }
}
