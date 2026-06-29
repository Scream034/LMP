namespace LMP.Core.Helpers;

/// <summary>
/// Общие методы фильтрации треков для устранения дублирования.
/// </summary>
public static class TrackFilters
{
    /// <summary>
    /// Фильтрует трек по названию и автору.
    /// </summary>
    public static bool MatchesTitleOrAuthor(TrackInfo track, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        return track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               track.Author.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}