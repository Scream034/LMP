using System.Reactive.Disposables;
using LMP.Core.Services;
using ReactiveUI;

namespace LMP.Core.ViewModels;

/// <summary>
/// Уровень приостановки UI-обновлений.
/// </summary>
public enum SuspendLevel
{
    /// <summary>
    /// Окно активно — полная работа всех подписок и анимаций.
    /// </summary>
    None = 0,

    /// <summary>
    /// Потеря фокуса или сворачивание (не в tray).
    /// Поведение зависит от настройки OptimizeWhenInactive:
    /// - true: аналогично Hard (dispose heavy subs)
    /// - false: работает штатно (для второго монитора)
    /// </summary>
    Soft = 1,

    /// <summary>
    /// Минимизация в tray — жёсткая остановка.
    /// Всегда dispose heavy подписок независимо от настроек.
    /// </summary>
    Hard = 2
}

public abstract class ViewModelBase : ReactiveObject, IDisposable
{
    #region Static Lifecycle

    private static readonly Lock _lifecycleLock = new();
    private static readonly List<WeakReference<ViewModelBase>> _instances = [];
    private static volatile SuspendLevel _currentLevel = SuspendLevel.None;

    /// <summary>
    /// Время последнего изменения уровня — для дебаунса.
    /// </summary>
    private static DateTime _lastLevelChangeTime = DateTime.MinValue;

    /// <summary>
    /// Минимальный интервал между broadcast (мс).
    /// </summary>
    private const int BroadcastDebounceMs = 300;

    /// <summary>
    /// Текущий уровень приостановки приложения.
    /// </summary>
    public static SuspendLevel CurrentSuspendLevel => _currentLevel;

    /// <summary>
    /// Приложение в режиме suspend (любой уровень кроме None).
    /// Для обратной совместимости.
    /// </summary>
    public static bool IsSuspended => _currentLevel != SuspendLevel.None;

    /// <summary>
    /// Приложение в жёстком suspend (tray).
    /// </summary>
    public static bool IsHardSuspended => _currentLevel == SuspendLevel.Hard;

    /// <summary>
    /// Вызывается из MainWindow при изменении состояния окна.
    /// 
    /// <para><b>Логика уровней:</b></para>
    /// <list type="bullet">
    ///   <item>Hard: tray — всегда suspend</item>
    ///   <item>Soft: потеря фокуса — suspend если OptimizeWhenInactive=true</item>
    ///   <item>None: активное окно — resume</item>
    /// </list>
    /// 
    /// <para><b>Дебаунс:</b> Игнорирует повторные вызовы в течение 300ms.</para>
    /// </summary>
    /// <param name="level">Новый уровень suspend</param>
    /// <param name="forceOptimize">
    /// Для Soft уровня: true = оптимизировать (пользователь включил настройку),
    /// false = работать штатно (второй монитор).
    /// Игнорируется для Hard и None.
    /// </param>
    public static void BroadcastSuspendLevel(SuspendLevel level, bool forceOptimize = true)
    {
        // Определяем эффективный уровень
        SuspendLevel effectiveLevel = level switch
        {
            SuspendLevel.Hard => SuspendLevel.Hard,
            SuspendLevel.Soft when forceOptimize => SuspendLevel.Soft,
            SuspendLevel.Soft => SuspendLevel.None, // Soft без оптимизации = работаем штатно
            _ => SuspendLevel.None
        };

        // ═══ РАННЯЯ ПРОВЕРКА: тот же уровень — выход ═══
        if (_currentLevel == effectiveLevel) return;

        // ═══ ДЕБАУНС: предотвращаем спам ═══
        var now = DateTime.UtcNow;
        if ((now - _lastLevelChangeTime).TotalMilliseconds < BroadcastDebounceMs)
        {
            // Исключение: Hard всегда применяется немедленно (tray)
            if (effectiveLevel != SuspendLevel.Hard)
            {
                Log.Debug($"[Lifecycle] Broadcast debounced: {_currentLevel} → {effectiveLevel}");
                return;
            }
        }
        _lastLevelChangeTime = now;

        var previousLevel = _currentLevel;
        _currentLevel = effectiveLevel;

        var alive = CollectAlive();

        Log.Debug($"[Lifecycle] Level change: {previousLevel} → {effectiveLevel} " +
                  $"(requested={level}, forceOptimize={forceOptimize}), VMs={alive.Count}");

        foreach (var vm in alive)
        {
            try
            {
                if (effectiveLevel == SuspendLevel.None)
                    vm.OnResume(previousLevel);
                else
                    vm.OnSuspend(effectiveLevel);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Lifecycle] Error in {vm.GetType().Name}: {ex.Message}");
            }
        }

        Log.Debug($"[Lifecycle] Level change complete. Current={_currentLevel}");
    }

    /// <summary>
    /// Упрощённый вызов для жёсткого suspend (tray).
    /// </summary>
    public static void BroadcastSuspend() => BroadcastSuspendLevel(SuspendLevel.Hard);

    /// <summary>
    /// Упрощённый вызов для resume.
    /// </summary>
    public static void BroadcastResume() => BroadcastSuspendLevel(SuspendLevel.None);

    /// <summary>
    /// Собирает живые экземпляры, очищая мёртвые WeakReference.
    /// </summary>
    private static List<ViewModelBase> CollectAlive()
    {
        lock (_lifecycleLock)
        {
            _instances.RemoveAll(static wr => !wr.TryGetTarget(out _));

            var result = new List<ViewModelBase>(_instances.Count);
            foreach (var wr in _instances)
            {
                if (wr.TryGetTarget(out var vm))
                    result.Add(vm);
            }
            return result;
        }
    }

    #endregion

    #region Localization Shortcuts

    /// <summary>Статическое для кода.</summary>
    public static LocalizationService SL => LocalizationService.Instance;

    /// <summary>Нестатическое для XAML биндинга (через DataContext).</summary>
#pragma warning disable CA1822 // Пометьте члены как статические
    public LocalizationService L => LocalizationService.Instance;
#pragma warning restore CA1822 // Пометьте члены как статические

    #endregion

    #region Instance

    /// <summary>
    /// Контейнер для всех подписок и IDisposable объектов.
    /// Автоматически очищается при вызове Dispose().
    /// </summary>
    protected CompositeDisposable Disposables { get; } = [];

    private bool _isDisposed;

    protected ViewModelBase()
    {
        lock (_lifecycleLock)
        {
            _instances.Add(new WeakReference<ViewModelBase>(this));
        }
    }

    #endregion

    #region Lifecycle — переопределяйте в наследниках

    /// <summary>
    /// Окно перешло в suspend состояние.
    /// 
    /// <para><b>Уровни:</b></para>
    /// <list type="bullet">
    ///   <item>Hard: tray — обязательно остановить всё тяжёлое</item>
    ///   <item>Soft: потеря фокуса с OptimizeWhenInactive — можно оптимизировать</item>
    /// </list>
    /// 
    /// Вызывается на UI-потоке.
    /// </summary>
    /// <param name="level">Уровень suspend</param>
    protected virtual void OnSuspend(SuspendLevel level) { }

    /// <summary>
    /// Окно вернулось в активное состояние.
    /// 
    /// <param name="previousLevel">Предыдущий уровень suspend (для понимания что восстанавливать)</param>
    /// Вызывается на UI-потоке.
    /// </summary>
    protected virtual void OnResume(SuspendLevel previousLevel) { }

    /// <summary>
    /// Обратная совместимость: вызывается если наследник не переопределил OnSuspend(level).
    /// </summary>
    protected virtual void OnSuspend() { }

    /// <summary>
    /// Обратная совместимость: вызывается если наследник не переопределил OnResume(level).
    /// </summary>
    protected virtual void OnResume() { }

    /// <summary>
    /// Вызывается из MainWindowViewModel после установки CurrentPage
    /// и завершения CrossFade-анимации (~180ms задержка).
    /// </summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    #endregion

    #region Command Helper

    /// <summary>
    /// Универсальный helper для ReactiveCommand.
    /// </summary>
    protected TCommand CreateCommand<TCommand>(TCommand command) where TCommand : IReactiveCommand
    {
        command.ThrownExceptions
            .Subscribe(ex => Log.Error($"[{GetType().Name}] Command error: {ex.Message}"))
            .DisposeWith(Disposables);

        if (command is IDisposable disposable)
        {
            disposable.DisposeWith(Disposables);
        }

        return command;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            Disposables.Dispose();
        }
        _isDisposed = true;
    }

    #endregion
}