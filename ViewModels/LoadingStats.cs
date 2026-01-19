// ViewModels/LoadingStats.cs
namespace MyLiteMusicPlayer.ViewModels;

// Используем record для поддержки with-expressions
public record LoadingStats
{
    public int TotalTracks { get; init; }
    public int DisplayedTracks { get; init; }
    public int CachedTracks { get; init; }
    public long LastBatchTimeMs { get; init; }
    public string Source { get; init; } = "";
    public string MemoryUsage { get; init; } = "";
}