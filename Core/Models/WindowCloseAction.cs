namespace LMP.Core.Models;

/// <summary>
/// Определяет поведение при нажатии кнопки закрытия окна.
/// </summary>
public enum CloseAction
{
    /// <summary>Закрыть приложение полностью.</summary>
    Exit = 0,

    /// <summary>Свернуть в системный трей, приложение продолжает работать.</summary>
    MinimizeToTray = 1,

    /// <summary>Спрашивать каждый раз.</summary>
    Ask = 2
}