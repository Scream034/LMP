namespace LMP.Core.Services;

/// <summary>
/// Реестр жизненного цикла приложения.
/// Управляет регистрацией и безопасным оповещением компонентов о смене аккаунтов и состояниях активности.
/// </summary>
/// <remarks>
/// <para>Использует слабые ссылки (<see cref="WeakReference{T}"/>) для исключения утечек памяти
/// и заменяет собой устаревшее плоское сканирование всех живых объектов в куче.</para>
/// </remarks>
public sealed class LifecycleRegistry
{
    private readonly List<WeakReference<IAccountAware>> _accountAwareItems = [];
    private readonly List<WeakReference<ISuspendable>> _backgroundSuspendableItems = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Глобальный доступ к синглтону реестра для предотвращения круговых зависимостей с UI-проектом
    /// </summary>
    public static LifecycleRegistry? Instance { get; internal set; }

    /// <summary>
    /// Делегат для получения активной страницы UI без жесткой связи Core-проекта с UI-проектом (Dependency Inversion)
    /// </summary>
    public static Func<ISuspendable?>? ActiveUiPageResolver { get; set; }

    /// <summary>
    /// Регистрирует компонент для отслеживания событий смены учетных записей.
    /// </summary>
    /// <param name="item">Компонент, реализующий <see cref="IAccountAware"/>.</param>
    public void RegisterAccountAware(IAccountAware item)
    {
        lock (_lock)
        {
            _accountAwareItems.Add(new WeakReference<IAccountAware>(item));
        }
    }

    /// <summary>
    /// Регистрирует фоновую службу для отслеживания событий приостановки.
    /// </summary>
    /// <param name="item">Служба, реализующая <see cref="ISuspendable"/>.</param>
    public void RegisterBackgroundSuspendable(ISuspendable item)
    {
        lock (_lock)
        {
            _backgroundSuspendableItems.Add(new WeakReference<ISuspendable>(item));
        }
    }

    /// <summary>
    /// Рассылает уведомление о смене учетной записи всем зарегистрированным компонентам.
    /// </summary>
    public void BroadcastAccountChanged()
    {
        lock (_lock)
        {
            for (int i = _accountAwareItems.Count - 1; i >= 0; i--)
            {
                if (_accountAwareItems[i].TryGetTarget(out var item))
                {
                    try
                    {
                        item.OnAccountChanged();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Lifecycle] Error broadcasting AccountChanged: {ex.Message}");
                    }
                }
                else
                {
                    _accountAwareItems.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Переводит зарегистрированные фоновые службы в режим приостановки.
    /// </summary>
    /// <param name="level">Уровень приостановки.</param>
    public void BroadcastBackgroundSuspend(SuspendLevel level)
    {
        lock (_lock)
        {
            for (int i = _backgroundSuspendableItems.Count - 1; i >= 0; i--)
            {
                if (_backgroundSuspendableItems[i].TryGetTarget(out var item))
                {
                    try
                    {
                        item.OnSuspend(level);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Lifecycle] Error broadcasting Suspend to background service: {ex.Message}");
                    }
                }
                else
                {
                    _backgroundSuspendableItems.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Возвращает зарегистрированные фоновые службы в активный режим.
    /// </summary>
    /// <param name="previousLevel">Предыдущий уровень приостановки.</param>
    public void BroadcastBackgroundResume(SuspendLevel previousLevel)
    {
        lock (_lock)
        {
            for (int i = _backgroundSuspendableItems.Count - 1; i >= 0; i--)
            {
                if (_backgroundSuspendableItems[i].TryGetTarget(out var item))
                {
                    try
                    {
                        item.OnResume(previousLevel);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Lifecycle] Error broadcasting Resume to background service: {ex.Message}");
                    }
                }
                else
                {
                    _backgroundSuspendableItems.RemoveAt(i);
                }
            }
        }
    }
}