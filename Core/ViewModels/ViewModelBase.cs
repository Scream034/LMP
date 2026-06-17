using System.Reactive.Disposables;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.ViewModels;

/// <summary>
/// Базовый класс для всех моделей представления (ViewModels) приложения.
/// Обеспечивает поддержку реактивного изменения свойств и управление жизненным циклом ресурсов.
/// </summary>
public abstract class ViewModelBase : ReactiveObject, IDisposable, ISuspendable
{
    #region Properties

    /// <summary>
    /// Контейнер для автоматической утилизации реактивных подписок и ресурсов.
    /// </summary>
    protected CompositeDisposable Disposables { get; } = [];

    /// <summary>
    /// Текущий глобальный уровень приостановки активности приложения.
    /// </summary>
    public static SuspendLevel CurrentSuspendLevel { get; private set; } = SuspendLevel.None;

    /// <summary>
    /// Указывает, находится ли текущий компонент в состоянии приостановки (фоновом режиме).
    /// </summary>
    [Reactive] public bool IsSuspended { get; private set; }

    /// <summary>
    /// Предоставляет доступ к службе локализации для одноуровневого биндинга (требование IFilterable).
    /// </summary>
#pragma warning disable CA1822 // Пометьте члены как статические
    public LocalizationService L => LocalizationService.Instance;
#pragma warning restore CA1822 // Пометьте члены как статические

    /// <summary>
    /// Предоставляет статический доступ к службе локализации.
    /// </summary>
    public static LocalizationService SL => LocalizationService.Instance;

    #endregion

    #region Constructor

    /// <summary>
    /// Инициализирует базовый класс. Выполняет автоматическую точечную регистрацию
    /// в реестре жизненного цикла, если наследник реализует интерфейс <see cref="IAccountAware"/>.
    /// </summary>
    protected ViewModelBase()
    {
        if (this is IAccountAware accountAware)
        {
            LifecycleRegistry.Instance?.RegisterAccountAware(accountAware);
        }
    }

    #endregion

    #region Navigation Lifecycle

    /// <summary>
    /// Асинхронно вызывается при переходе на данную страницу навигации.
    /// </summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    #endregion

    #region ISuspendable Explicit Implementation

    /// <inheritdoc />
    void ISuspendable.OnSuspend(SuspendLevel level)
    {
        IsSuspended = true; // Фиксируем состояние приостановки на уровне VM
        OnSuspend(level);
    }

    /// <inheritdoc />
    void ISuspendable.OnResume(SuspendLevel previousLevel)
    {
        IsSuspended = false; // Возвращаем в активное состояние
        OnResume(previousLevel);
    }

    #endregion

    #region Protected Virtual Lifecycle

    /// <summary>
    /// Вызывается при переходе компонента в фоновый режим без параметров.
    /// </summary>
    protected virtual void OnSuspend() { }

    /// <summary>
    /// Вызывается при возвращении компонента в активный режим без параметров.
    /// </summary>
    protected virtual void OnResume() { }

    /// <summary>
    /// Вызывается при переходе компонента в фоновый режим с указанием уровня приостановки.
    /// Перенаправляет вызов в беспараметрический метод для совместимости.
    /// </summary>
    protected virtual void OnSuspend(SuspendLevel level) => OnSuspend();

    /// <summary>
    /// Вызывается при возвращении компонента в активный режим с указанием предыдущего уровня приостановки.
    /// Перенаправляет вызов в беспараметрический метод для совместимости.
    /// </summary>
    protected virtual void OnResume(SuspendLevel previousLevel) => OnResume();

    /// <summary>
    /// Вызывается при изменении профиля пользователя.
    /// </summary>
    protected virtual void OnAccountChanged() { }

    #endregion

    #region Helpers for Subclasses

    /// <summary>
    /// Регистрирует команду в контейнере утилизации и настраивает отслеживание ошибок.
    /// </summary>
    protected ReactiveCommand<TIn, TOut> CreateCommand<TSource, TIn, TOut>(
        ReactiveCommand<TIn, TOut> command)
        where TSource : notnull
    {
        command.DisposeWith(Disposables);
        return command;
    }

    /// <summary>
    /// Регистрирует команду в контейнере утилизации ресурсов.
    /// </summary>
    protected TCommand CreateCommand<TCommand>(TCommand command) where TCommand : IDisposable
    {
        command.DisposeWith(Disposables);
        return command;
    }

    #endregion

    #region Static Lifecycle Broadcasts

    /// <summary>
    /// Распространяет уровень приостановки по системе. Применяет каскадную модель навигации 
    /// и точечно оповещает фоновые службы .
    /// </summary>
    public static void BroadcastSuspendLevel(SuspendLevel level, bool forceOptimize = false)
    {
        var previousLevel = CurrentSuspendLevel;
        if (previousLevel == level && !forceOptimize) return;

        CurrentSuspendLevel = level;

        // 1. Оповещаем зарегистрированные фоновые службы (например, AudioEngine)
        if (LifecycleRegistry.Instance != null)
        {
            if (level != SuspendLevel.None)
                LifecycleRegistry.Instance.BroadcastBackgroundSuspend(level);
            else
                LifecycleRegistry.Instance.BroadcastBackgroundResume(previousLevel);
        }

        // 2. Иерархическое распространение: оповещаем только активную UI-страницу 
        var activePage = LifecycleRegistry.ActiveUiPageResolver?.Invoke();
        if (activePage != null)
        {
            if (level != SuspendLevel.None)
                activePage.OnSuspend(level);
            else
                activePage.OnResume(previousLevel);
        }
    }

    /// <summary>
    /// Рассылает событие изменения аккаунта через точечный реестр.
    /// </summary>
    public static void BroadcastAccountChanged()
    {
        LifecycleRegistry.Instance?.BroadcastAccountChanged();
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Выполняет утилизацию ресурсов компонента.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Выполняет утилизацию управляемых и неуправляемых ресурсов.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disposables.Dispose();
        }
    }

    #endregion
}