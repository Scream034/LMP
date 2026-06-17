using Microsoft.EntityFrameworkCore;
using LMP.Core.Data.Entities;
using LMP.Core.Models;

namespace LMP.Core.Data;

/// <summary>
/// Расширения контекста базы данных для выполнения безопасных миграций и оптимизаций.
/// </summary>
public static class DatabaseExtensions
{
    /// <summary>
    /// Константный номер текущей версии структуры базы данных LMP.
    /// При его изменении старая схема будет пересоздана в чистое состояние с предварительным бэкапом.
    /// </summary>
    public const int CurrentDbVersion = 2;

    /// <summary>
    /// Извлекает текущую версию схемы базы данных с использованием PRAGMA user_version.
    /// </summary>
    public static async Task<int> GetDatabaseVersionAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        var connection = context.Database.GetDbConnection();
        bool closeOnExit = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            closeOnExit = true;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null ? Convert.ToInt32(result) : 0;
        }
        finally
        {
            if (closeOnExit)
                await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Сохраняет новую версию схемы базы данных с использованием PRAGMA user_version.
    /// </summary>
    public static async Task SetDatabaseVersionAsync(this LibraryDbContext context, int version, CancellationToken ct = default)
    {
        var connection = context.Database.GetDbConnection();
        bool closeOnExit = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            closeOnExit = true;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {version};";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeOnExit)
                await connection.CloseAsync();
        }
    }

    /// <summary>
    /// Применяет инкрементные миграции схемы для существующих баз данных.
    /// </summary>
    public static async Task MigrateSchemaAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await AddColumnIfNotExistsAsync(context, "Playlists", "OwnerId", "TEXT NOT NULL DEFAULT ''", ct);
        await AddColumnIfNotExistsAsync(context, "RecentlyPlayed", "OwnerId", "TEXT NOT NULL DEFAULT ''", ct);

        await AddColumnIfNotExistsAsync(context, "Playlists", "CustomColor", "TEXT", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "ComputedColor", "TEXT", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "Description", "TEXT", ct);
        await AddColumnIfNotExistsAsync(context, "PlaylistTracks", "SetVideoId", "TEXT", ct);

        await EnsureLikedTracksTableAsync(context, ct);
        await MigrateLegacyLikesAsync(context, ct);
    }

    /// <summary>
    /// Безопасно добавляет новую колонку в таблицу SQLite.
    /// </summary>
    private static async Task AddColumnIfNotExistsAsync(
        LibraryDbContext context,
        string tableName,
        string columnName,
        string columnType,
        CancellationToken ct)
    {
        try
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(ct);

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(" + tableName + ")";

                using var reader = await cmd.ExecuteReaderAsync(ct);
                bool columnExists = false;

                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }

                if (!columnExists)
                {
                    var alterSql = string.Concat(
                        "ALTER TABLE ", tableName,
                        " ADD COLUMN ", columnName,
                        " ", columnType);

                    using var alterCmd = connection.CreateCommand();
                    alterCmd.CommandText = alterSql;
                    await alterCmd.ExecuteNonQueryAsync(ct);

                    Log.Info(string.Concat("[DB] Added column ", tableName, ".", columnName, " (", columnType, ")"));
                }
            }
            finally
            {
                if (connection.State == System.Data.ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(string.Concat("[DB] Migration failed for ", tableName, ".", columnName, ": ", ex.Message));
        }
    }

    /// <summary>
    /// Создает таблицу LikedTracks для многопользовательского разделения лайков.
    /// </summary>
    private static async Task EnsureLikedTracksTableAsync(LibraryDbContext context, CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS LikedTracks (
                OwnerId TEXT NOT NULL,
                TrackId TEXT NOT NULL,
                LikedAt TEXT NOT NULL,
                PRIMARY KEY (OwnerId, TrackId),
                FOREIGN KEY (TrackId) REFERENCES Tracks (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_LikedTracks_OwnerId ON LikedTracks (OwnerId);
            """;
        await context.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>
    /// Переносит лайки старой схемы во вновь созданную связующую таблицу LikedTracks.
    /// </summary>
    private static async Task MigrateLegacyLikesAsync(LibraryDbContext context, CancellationToken ct)
    {
        try
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(ct);

            try
            {
                bool hasIsLiked = false;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(Tracks)";
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        if (string.Equals(reader.GetString(1), "IsLiked", StringComparison.OrdinalIgnoreCase))
                        {
                            hasIsLiked = true;
                            break;
                        }
                    }
                }

                if (hasIsLiked)
                {
                    Log.Info("[DB] Migrating legacy likes from Tracks.IsLiked to LikedTracks...");

                    int migrated = 0;
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = """
                            INSERT OR IGNORE INTO LikedTracks (OwnerId, TrackId, LikedAt)
                            SELECT '', Id, datetime('now') FROM Tracks WHERE IsLiked = 1
                            """;
                        migrated = await cmd.ExecuteNonQueryAsync(ct);
                        if (migrated > 0)
                        {
                            Log.Info($"[DB] Successfully migrated {migrated} legacy likes.");

                            // Сохраняем красивое локализованное уведомление об успешном переносе старых лайков [1]
                            SaveSuccessMigrationNotification(context);
                        }
                    }

                    // КРИТИЧЕСКИЙ ПАТЧ: сначала полностью удаляем индекс, блокирующий DROP COLUMN
                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "DROP INDEX IF EXISTS IX_Tracks_IsLiked";
                        await cmd.ExecuteNonQueryAsync(ct);
                        Log.Info("[DB] Index IX_Tracks_IsLiked dropped to prevent schema lock during migration.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[DB] Failed to drop index IX_Tracks_IsLiked: {ex.Message}");
                    }

                    try
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.CommandText = "ALTER TABLE Tracks DROP COLUMN IsLiked";
                        await cmd.ExecuteNonQueryAsync(ct);
                        Log.Info("[DB] Legacy column Tracks.IsLiked dropped successfully.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[DB] Could not drop legacy column Tracks.IsLiked: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (connection.State == System.Data.ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DB] Legacy likes migration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Сохраняет запись об успешном импорте любимых треков с использованием JSON-ключей [1].
    /// </summary>
    private static void SaveSuccessMigrationNotification(LibraryDbContext context)
    {
        try
        {
            var notification = new NotificationEntity
            {
                Id = Guid.NewGuid().ToString(),
                TitleKey = "Playlist_SyncComplete_Toast_Title", // "Синхронизация завершена"
                MessageKey = "Sync_Success_Msg_LikedOnly", // "Понравившиеся песни успешно синхронизированы."
                Severity = (int)NotificationSeverity.Success,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            };
            context.Notifications.Add(notification);
            context.SaveChanges();
            Log.Info("[DB] Success migration notification saved to database");
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] Failed to save success migration notification: {ex.Message}");
        }
    }

    /// <summary>
    /// Создает FTS5 виртуальную таблицу для мгновенного поиска по трекам.
    /// </summary>
    /// <remarks>
    /// <para>Удалено ручное создание служебной таблицы <c>TracksFts_config</c>, 
    /// так как движок SQLite FTS5 генерирует и обслуживает её теневой аналог автоматически.</para>
    /// </remarks>
    /// <param name="context">Контекст базы данных.</param>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    public static async Task EnsureFtsTablesAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        const string sql = """
            CREATE VIRTUAL TABLE IF NOT EXISTS TracksFts USING fts5(
                Title, 
                Author,
                content='Tracks',
                content_rowid='RowId',
                tokenize='unicode61 remove_diacritics 2'
            );
            
            CREATE TRIGGER IF NOT EXISTS Tracks_ai AFTER INSERT ON Tracks BEGIN
                INSERT INTO TracksFts(rowid, Title, Author) 
                VALUES (NEW.RowId, NEW.Title, NEW.Author);
            END;
            
            CREATE TRIGGER IF NOT EXISTS Tracks_ad AFTER DELETE ON Tracks BEGIN
                INSERT INTO TracksFts(TracksFts, rowid, Title, Author) 
                VALUES('delete', OLD.RowId, OLD.Title, OLD.Author);
            END;
            
            CREATE TRIGGER IF NOT EXISTS Tracks_au AFTER UPDATE ON Tracks BEGIN
                INSERT INTO TracksFts(TracksFts, rowid, Title, Author) 
                VALUES('delete', OLD.RowId, OLD.Title, OLD.Author);
                INSERT INTO TracksFts(rowid, Title, Author) 
                VALUES (NEW.RowId, NEW.Title, NEW.Author);
            END;
            """;

        await context.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>
    /// Полностью пересоздает FTS-индекс.
    /// </summary>
    public static async Task RebuildFtsIndexAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync("DELETE FROM TracksFts;", ct);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO TracksFts(rowid, Title, Author) SELECT RowId, Title, Author FROM Tracks;", ct);
    }

    /// <summary>
    /// Настраивает WAL-режим работы СУБД SQLite.
    /// </summary>
    public static async Task OptimizeAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA cache_size=-64000;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY;", ct);
    }
}