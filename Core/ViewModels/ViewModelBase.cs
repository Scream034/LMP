using System.Reactive.Disposables;
using LMP.Core.Services;
using ReactiveUI;

namespace LMP.Core.ViewModels;

public abstract class ViewModelBase : ReactiveObject, IDisposable
{
    #region Static Lifecycle

    private static readonly Lock _lifecycleLock = new();
    private static readonly List<WeakReference<ViewModelBase>> _instances = [];
    private static volatile bool _isSuspended;

    /// <summary>
    /// Приложение в режиме suspend (окно свёрнуто или неактивно долго).
    /// </summary>
    public static bool IsSuspended => _isSuspended;

    /// <summary>
    /// Вызывается из MainWindow при сворачивании.
    /// Уведомляет все живые ViewModel-и через OnSuspend().
    /// ВАЖНО: вызывать на UI-потоке (DispatcherTimer.Stop требует UI).
    /// </summary>
    public static void BroadcastSuspend()
    {
        if (_isSuspended) return;
        _isSuspended = true;

        var alive = CollectAlive();

        Log.Debug($"[Lifecycle] Suspend → {alive.Count} VMs");

        foreach (var vm in alive)
        {
            try { vm.OnSuspend(); }
            catch (Exception ex)
            {
                Log.Warn($"[Lifecycle] OnSuspend error in {vm.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Вызывается из MainWindow при разворачивании.
    /// Уведомляет все живые ViewModel-и через OnResume().
    /// ВАЖНО: вызывать на UI-потоке.
    /// </summary>
    public static void BroadcastResume()
    {
        if (!_isSuspended) return;
        _isSuspended = false;

        var alive = CollectAlive();

        Log.Debug($"[Lifecycle] Resume → {alive.Count} VMs");

        foreach (var vm in alive)
        {
            try { vm.OnResume(); }
            catch (Exception ex)
            {
                Log.Warn($"[Lifecycle] OnResume error in {vm.GetType().Name}: {ex.Message}");
            }
        }
    }

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
    public LocalizationService L => LocalizationService.Instance;

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
        // Регистрируемся для lifecycle-уведомлений через WeakReference.
        // Если VM собран GC без Dispose — ничего страшного, WeakRef умрёт.
        lock (_lifecycleLock)
        {
            _instances.Add(new WeakReference<ViewModelBase>(this));
        }
    }

    #endregion

    #region Lifecycle — переопределяйте в наследниках

    /// <summary>
    /// Окно свёрнуто или неактивно.
    /// Остановите таймеры, пропускайте обновления UI,
    /// освободите некритичные кэши.
    /// Вызывается на UI-потоке.
    /// </summary>
    protected virtual void OnSuspend() { }

    /// <summary>
    /// Окно развёрнуто и активно.
    /// Запустите таймеры, обновите UI.
    /// Вызывается на UI-потоке.
    /// </summary>
    protected virtual void OnResume() { }

    #endregion

    #region Command Helper

    /// <summary>
    /// Универсальный helper для ReactiveCommand.
    /// Гарантирует подписку на ThrownExceptions (предотвращает утечку памяти)
    /// и регистрирует команду для автоматической очистки.
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