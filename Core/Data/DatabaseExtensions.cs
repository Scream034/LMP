using Microsoft.EntityFrameworkCore;
using LMP.Core.Data.Entities;

namespace LMP.Core.Data;

public static class DatabaseExtensions
{
    /// <summary>
    /// Текущая версия схемы базы данных.
    /// </summary>
    public const int CurrentDbVersion = 4;

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

        await AddColumnIfNotExistsAsync(context, "Playlists", "OwnerChannelId", "TEXT", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "Ownership", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "Visibility", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "CloudTrackCount", "INTEGER", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "LastSyncedAtUtc", "TEXT", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "IsCloudUnavailable", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "ViewCount", "INTEGER", ct);
        await AddColumnIfNotExistsAsync(context, "Playlists", "ReleaseDate", "TEXT", ct);

        await AddColumnIfNotExistsAsync(context, "Tracks", "IntegratedLufs", "REAL", ct);
        await AddColumnIfNotExistsAsync(context, "Tracks", "IntegratedLufsSource", "INTEGER NOT NULL DEFAULT 0", ct);

        await context.Database.ExecuteSqlRawAsync(
            "UPDATE Playlists SET ReleaseDate = NULL WHERE ReleaseDate IS NOT NULL AND ReleaseDate NOT GLOB '????-??-??'",
            ct).ConfigureAwait(false);

        await MigrateCloudPublicOwnershipAsync(context, ct);
    }

    private static async Task MigrateCloudPublicOwnershipAsync(
        LibraryDbContext context, CancellationToken ct)
    {
        try
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
                cmd.CommandText =
                    "UPDATE Playlists SET Ownership = 2, Visibility = 3 WHERE SyncMode = 2 AND Ownership = 0";

                int affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected > 0)
                    Log.Info($"[DB] Migrated {affected} CloudPublic playlist(s) → Foreign + Public");
            }
            finally
            {
                if (closeOnExit && connection.State == System.Data.ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[DB] CloudPublic ownership migration warning: {ex.Message}");
        }
    }

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
                            SaveSuccessMigrationNotification(context);
                        }
                    }

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

    private static void SaveSuccessMigrationNotification(LibraryDbContext context)
    {
        try
        {
            var notification = new NotificationEntity
            {
                Id = Guid.NewGuid().ToString(),
                TitleKey = "Playlist_SyncComplete_Toast_Title",
                MessageKey = "Sync_Success_Msg_LikedOnly",
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

    public static async Task RebuildFtsIndexAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync("DELETE FROM TracksFts;", ct);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO TracksFts(rowid, Title, Author) SELECT RowId, Title, Author FROM Tracks;", ct);
    }

    public static async Task OptimizeAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA cache_size=-64000;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY;", ct);
    }
}