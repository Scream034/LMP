namespace LMP.UI.Features.Shell;

/// <summary>
/// Контракт для страниц, поддерживающих оптимизированный плавный переход.
/// Позволяет временно скрыть тяжелую разметку на время анимации затухания.
/// </summary>
public interface ISmoothTransitionViewModel
{
    /// <summary>
    /// Скрывает тяжелые элементы интерфейса и показывает скелетон 
    /// перед началом анимации перехода для обеспечения стабильного FPS.
    /// </summary>
    void PrepareForTransition();
}