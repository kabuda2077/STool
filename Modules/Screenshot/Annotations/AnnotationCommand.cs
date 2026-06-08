using System.Windows.Shapes;
using System.Windows.Controls;

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
    private readonly Shape _shape;

    public AddShapeCommand(Shape shape)
    {
        _shape = shape;
    }

    public void Execute(Canvas canvas)
    {
        if (!canvas.Children.Contains(_shape))
        {
            canvas.Children.Add(_shape);
        }
    }

    public void Undo(Canvas canvas)
    {
        canvas.Children.Remove(_shape);
    }
}
