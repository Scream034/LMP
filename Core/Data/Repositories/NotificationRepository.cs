using LMP.Core.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _dbFactory;

    public NotificationRepository(IDbContextFactory<LibraryDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<NotificationEntity>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Notifications
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task AddAsync(NotificationEntity entity, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Notifications.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllAsReadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Notifications
            .Where(n => !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Notifications.ExecuteDeleteAsync(ct);
    }

    public async Task PruneAsync(int keepCount = 100, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Находим дату отсечки
        var cutoffDate = await db.Notifications
            .OrderByDescending(n => n.CreatedAt)
            .Skip(keepCount)
            .Select(n => n.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (cutoffDate != default)
        {
            await db.Notifications
                .Where(n => n.CreatedAt < cutoffDate)
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task DeleteOlderThanAsync(DateTime threshold, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Notifications
            .Where(n => n.CreatedAt < threshold)
            .ExecuteDeleteAsync(ct);
    }
}