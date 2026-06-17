namespace LMP.Core.ViewModels;

/// <summary>
/// Интерфейс для ViewModels с поддержкой текстовой фильтрации.
/// </summary>
public interface IFilterable
{
    /// <summary>
    /// Текстовый запрос для локальной фильтрации.
    /// </summary>
    string FilterQuery { get; set; }

    /// <summary>
    /// Сервис локализации для UI.
    /// </summary>
    LocalizationService L { get; }
}