using LMP.Core.Data.Entities;

namespace LMP.Core.Data.Repositories;

/// <summary>
/// Репозиторий для персистентного хранения уведомлений.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Получить последние уведомления.
    /// </summary>
    Task<List<NotificationEntity>> GetRecentAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Добавить уведомление.
    /// </summary>
    Task AddAsync(NotificationEntity entity, CancellationToken ct = default);

    /// <summary>
    /// Пометить все как прочитанные.
    /// </summary>
    Task MarkAllAsReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Удалить все уведомления.
    /// </summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Количество непрочитанных.
    /// </summary>
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Удалить старые уведомления, оставив не более <paramref name="keepCount"/>.
    /// </summary>
    Task PruneAsync(int keepCount = 100, CancellationToken ct = default);
}