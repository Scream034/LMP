using System.Runtime.CompilerServices;
using LMP.Core.Models;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Общий хелпер для работы с thumbnails.
/// Убирает дублирование GetBestThumbnailUrl из MusicClient, YoutubeProvider, MusicBrowseResponse.
/// </summary>
public static class ThumbnailUtils
{
    /// <summary>
    /// Находит thumbnail с максимальным разрешением.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? GetBestUrl(IReadOnlyList<Thumbnail> thumbnails)
    {
        if (thumbnails.Count == 0) return null;

        string? bestUrl = null;
        int bestArea = -1;

        for (int i = 0; i < thumbnails.Count; i++)
        {
            var area = thumbnails[i].Resolution.Area;
            if (area > bestArea)
            {
                bestArea = area;
                bestUrl = thumbnails[i].Url;
            }
        }

        return bestUrl;
    }

    /// <summary>
    /// Находит thumbnail с максимальным разрешением,
    /// с fallback на YouTube video thumbnail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetBestUrlOrDefault(
        IReadOnlyList<Thumbnail> thumbnails, string videoId)
    {
        return GetBestUrl(thumbnails)
            ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
    }
}