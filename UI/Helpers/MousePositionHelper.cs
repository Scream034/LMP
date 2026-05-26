using Avalonia;
using Avalonia.Input;

namespace LMP.UI.Helpers;

/// <summary>
/// Глобальный трекер позиции курсора мыши.
/// Регистрируется один раз в MainWindow — все контролы читают без подписок.
/// </summary>
public static class MousePositionHelper
{
    private static Point _position;

    /// <summary>Последняя известная позиция курсора в координатах окна.</summary>
    public static Point Position => _position;

    /// <summary>
    /// Привязывает трекер к корневому элементу окна.
    /// Вызывать один раз при инициализации MainWindow.
    /// </summary>
    public static void Attach(Visual root)
    {
        if (root is InputElement input)
            input.PointerMoved += (_, e) => _position = e.GetPosition(root);
    }
}