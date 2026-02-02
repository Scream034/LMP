using LMP.Core.Youtube.Search;

namespace LMP.Core.Models;

/// <summary>
/// Источник поиска контента.
/// </summary>
public enum SearchSource
{
    /// <summary>
    /// Стандартный YouTube (видео, все типы контента).
    /// </summary>
    YouTube,
    
    /// <summary>
    /// YouTube Music (песни, альбомы, музыкальный контент).
    /// </summary>
    YouTubeMusic,
    
    /// <summary>
    /// Только плейлисты.
    /// </summary>
    Playlists
}

public static class SearchSourceExtensions
{
    /// <summary>
    /// Конвертирует источник в API фильтр.
    /// </summary>
    public static SearchFilter ToSearchFilter(this SearchSource source) => source switch
    {
        SearchSource.YouTube => SearchFilter.Video,
        SearchSource.YouTubeMusic => SearchFilter.MusicSong,
        SearchSource.Playlists => SearchFilter.Playlist,
        _ => SearchFilter.Video
    };

    /// <summary>
    /// Название для UI.
    /// </summary>
    public static string GetDisplayName(this SearchSource source) => source switch
    {
        SearchSource.YouTube => "YouTube",
        SearchSource.YouTubeMusic => "YouTube Music",
        SearchSource.Playlists => "Playlists",
        _ => "YouTube"
    };

    /// <summary>
    /// Ключ для кэша.
    /// </summary>
    public static string ToCacheKey(this SearchSource source) => source switch
    {
        SearchSource.YouTube => "yt",
        SearchSource.YouTubeMusic => "ytm",
        SearchSource.Playlists => "pl",
        _ => "yt"
    };

    /// <summary>
    /// Контекст YouTube Music (WEB_REMIX)?
    /// </summary>
    public static bool IsMusicContext(this SearchSource source) =>
        source == SearchSource.YouTubeMusic;
}