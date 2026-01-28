// Core/Data/DatabaseExtensions.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace LMP.Core.Data;

public static class DatabaseExtensions
{
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
        // Delete and repopulate
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
        await context.Database.ExecuteSqlRawAsync("PRAGMA cache_size=-64000;", ct); // 64MB cache
        await context.Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY;", ct);
    }
}