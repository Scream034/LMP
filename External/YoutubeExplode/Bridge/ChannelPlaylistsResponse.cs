using System.Text.Json;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal class ChannelPlaylistsResponse(JsonElement content)
{
    // Ищем корень контента. В ответе browse он может быть глубоко.
    // Обычно это contents -> twoColumnBrowseResultsRenderer -> tabs ...
    // Но мы используем поиск потомков, чтобы не зависеть от верстки.

    public IReadOnlyList<Playlist> Playlists =>
        ParsePlaylists(content).ToArray();

    public string? ContinuationToken =>
        content
            .EnumerateDescendantProperties("continuationCommand")
            .FirstOrNull()
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull();

    private IEnumerable<Playlist> ParsePlaylists(JsonElement root)
    {
        // 1. Старый дизайн (Grid Renderer)
        var gridItems = root.EnumerateDescendantProperties("gridPlaylistRenderer");
        foreach (var item in gridItems)
        {
            var pl = ParseGridPlaylist(item);
            if (pl != null) yield return pl;
        }

        // 2. Старый дизайн (List Renderer)
        var listItems = root.EnumerateDescendantProperties("playlistRenderer");
        foreach (var item in listItems)
        {
            var pl = ParseGridPlaylist(item); // Структура похожа
            if (pl != null) yield return pl;
        }

        // 3. Новый дизайн (Lockup ViewModel)
        var lockupItems = root.EnumerateDescendantProperties("lockupViewModel");
        foreach (var item in lockupItems)
        {
            var pl = ParseLockupViewModel(item);
            if (pl != null) yield return pl;
        }
    }

    private Playlist? ParseGridPlaylist(JsonElement json)
    {
        var id = json.GetPropertyOrNull("playlistId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(id)) return null;

        var title = json.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?
            .EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull()
            ?? json.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

        // Количество видео
        var countText = json.GetPropertyOrNull("videoCountText")?.GetPropertyOrNull("runs")?
            .EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull();

        int? count = null;
        if (countText != null && int.TryParse(countText.Replace(",", "").Replace(" ", ""), out var c))
            count = c;

        var thumbnails = json.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?
            .EnumerateArrayOrNull()?.Select(x => new ThumbnailData(x))
            .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
            .ToArray() ?? [];

        // В этом контексте автор нам не всегда приходит полным объектом, но мы знаем ID канала снаружи
        // Поэтому Author здесь может быть null, мы его заполним в Client'е
        return new Playlist(id, title ?? "", null, "", count, thumbnails);
    }

    private Playlist? ParseLockupViewModel(JsonElement json)
    {
        // Проверяем, что это плейлист (contentType = "PLAYLIST")
        // Но в lockupViewModel это часто скрыто в contentId
        var id = json.GetPropertyOrNull("contentId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(id) || !id.StartsWith("PL")) return null; // ID плейлиста обычно начинается с PL (или VL, OL)

        var metadata = json.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel");
        var title = metadata?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull() ?? "";

        var image = json.GetPropertyOrNull("contentImage")?.GetPropertyOrNull("collectionThumbnailViewModel")?
            .GetPropertyOrNull("primaryThumbnail")?.GetPropertyOrNull("thumbnailViewModel")?.GetPropertyOrNull("image");

        var thumbnails = image?.GetPropertyOrNull("sources")?
             .EnumerateArrayOrNull()?.Select(x => new ThumbnailData(x))
             .Select(t => new Thumbnail(t.Url!, new Resolution(t.Width ?? 0, t.Height ?? 0)))
             .ToArray() ?? [];

        return new Playlist(id, title, null, "", null, thumbnails);
    }

    public static ChannelPlaylistsResponse Parse(string raw) => new(Json.Parse(raw));
}