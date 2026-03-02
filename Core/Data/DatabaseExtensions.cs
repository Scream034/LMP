using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data;

public static class DatabaseExtensions
{
    /// <summary>
    /// Applies incremental schema migrations for existing databases.
    /// EnsureCreatedAsync only creates tables that don't exist — it won't add new columns.
    /// This method safely adds missing columns using ALTER TABLE.
    /// </summary>
    public static async Task MigrateSchemaAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        // Each migration checks if column exists before adding (idempotent).
        // SQLite doesn't support IF NOT EXISTS for ALTER TABLE,
        // so we query PRAGMA table_info and check manually.

        await AddColumnIfNotExistsAsync(context,
            "Playlists", "CustomColor", "TEXT", ct);

        await AddColumnIfNotExistsAsync(context,
            "Playlists", "ComputedColor", "TEXT", ct);

        await AddColumnIfNotExistsAsync(context,
            "Playlists", "Description", "TEXT", ct);

        await AddColumnIfNotExistsAsync(context,
            "PlaylistTracks", "SetVideoId", "TEXT", ct);

        Log.Info("[DB] Schema migration complete");
    }

    /// <summary>
    /// Adds a column to a table if it doesn't already exist.
    /// Safe to call multiple times (idempotent).
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
    /// Creates FTS5 virtual table for full-text search on tracks.
    /// </summary>
    public static async Task EnsureFtsTablesAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        const string sql = """
            -- FTS5 Virtual Table for Track Search
            CREATE VIRTUAL TABLE IF NOT EXISTS TracksFts USING fts5(
                Title, 
                Author,
                content='Tracks',
                content_rowid='RowId',
                tokenize='unicode61 remove_diacritics 2'
            );
            
            -- Triggers to keep FTS synchronized with Tracks table
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
    /// Rebuilds FTS index (call after bulk import/migration).
    /// </summary>
    public static async Task RebuildFtsIndexAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync("DELETE FROM TracksFts;", ct);
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO TracksFts(rowid, Title, Author) SELECT RowId, Title, Author FROM Tracks;", ct);
    }

    /// <summary>
    /// Enables WAL mode for better concurrent performance.
    /// </summary>
    public static async Task OptimizeAsync(this LibraryDbContext context, CancellationToken ct = default)
    {
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA cache_size=-64000;", ct);
        await context.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY;", ct);
    }
}