using Avalonia;

namespace LMP.UI.Services;

/// <summary>
/// Singleton-сервис для отображения глобального toast-уведомления.
/// Один overlay на всё приложение — MainWindow подписывается и управляет единственным Border.
/// Любой VM или контрол вызывает Show() без знания об UI-дереве.
/// </summary>
public sealed class CopyHintService
{
    public static readonly CopyHintService Instance = new();

    private CopyHintService() { }

    /// <summary>
    /// Поднимается когда нужно показать hint.
    /// <paramref name="screenPosition"/> — позиция курсора в координатах окна,
    /// null = нижний центр окна.
    /// </summary>
    public event Action<string, CopyHintKind, Point?>? HintRequested;

    /// <summary>
    /// Показать toast-уведомление.
    /// </summary>
    /// <param name="text">Текст hint-а.</param>
    /// <param name="kind">Тип: Success / Warning / Error.</param>
    /// <param name="screenPosition">Позиция курсора в координатах окна. Null = нижний центр.</param>
    public void Show(string text, CopyHintKind kind = CopyHintKind.Success, Point? screenPosition = null)
        => HintRequested?.Invoke(text, kind, screenPosition);
}

/// <summary>
/// Тип визуального hint-а — определяет иконку и цвет акцента.
/// </summary>
public enum CopyHintKind
{
    /// <summary>Ссылка скопирована — зелёная галочка.</summary>
    Success,
    /// <summary>Нет ссылки / не привязан к YouTube — оранжевый треугольник.</summary>
    Warning,
    /// <summary>Ошибка — красный крест.</summary>
    Error
}