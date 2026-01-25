using System.Text.Json;
using YoutubeExplode.Bridge;
using YoutubeExplode.Common;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Music;

public class MusicShelf
{
    public string Title { get; }
    public List<MusicItem> Items { get; }

    public MusicShelf(string title, List<MusicItem> items)
    {
        Title = title;
        Items = items;
    }
}

public class MusicItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Album { get; set; }
    public TimeSpan? Duration { get; set; }
    public IReadOnlyList<Thumbnail> Thumbnails { get; set; } = [];
    public string Type { get; set; } = "Song";
}

internal class MusicBrowseResponse
{
    public List<MusicShelf> Shelves { get; } = [];
    public string? Title { get; private set; }
    public string? ContinuationToken { get; private set; }

    public MusicBrowseResponse(JsonElement root)
    {
        // 1. Заголовок
        var header = root.GetPropertyOrNull("header")?.GetPropertyOrNull("musicDetailHeaderRenderer")
            ?? root.GetPropertyOrNull("header")?.GetPropertyOrNull("musicResponsiveHeaderRenderer");

        Title = header?.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull()
             ?? header?.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();

        // 2. Сначала ищем Continuation (Пагинацию)
        // Если это ответ на подгрузку, в нем структура отличается
        if (TryParseContinuation(root))
            return;

        // 3. Если это не пагинация, парсим как обычную страницу (Browse)
        var sectionList = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("secondaryContents")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sectionList == null)
        {
            sectionList = root.GetPropertyOrNull("contents")
                ?.GetPropertyOrNull("singleColumnBrowseResultsRenderer")
                ?.GetPropertyOrNull("tabs")?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("tabRenderer")?.GetPropertyOrNull("content")
                ?.GetPropertyOrNull("sectionListRenderer")?.GetPropertyOrNull("contents");
        }

        if (sectionList != null)
        {
            foreach (var section in sectionList.Value.EnumerateArrayOrEmpty())
            {
                if (section.TryGetProperty("musicPlaylistShelfRenderer", out var playlistShelf))
                {
                    ParseShelfContent(playlistShelf, "Tracks"); // Парсим треки

                    // Токен первой страницы (100+)
                    ContinuationToken ??= playlistShelf.GetPropertyOrNull("continuations")?.EnumerateArrayOrNull()?.FirstOrDefault()
                        .GetPropertyOrNull("nextContinuationData")?.GetPropertyOrNull("continuation")?.GetStringOrNull();
                }
                else if (section.TryGetProperty("gridRenderer", out var grid))
                {
                    ParseGridContent(grid, "Library");
                }
                else if (section.TryGetProperty("musicCarouselShelfRenderer", out var carousel))
                {
                    ParseShelfContent(carousel, null);
                }
            }
        }
    }

    private bool TryParseContinuation(JsonElement root)
    {
        bool foundAny = false;

        // Вариант А: onResponseReceivedActions (Используется для подгрузки лайков VLLM)
        var actions = root.GetPropertyOrNull("onResponseReceivedActions");
        if (actions != null)
        {
            foreach (var action in actions.Value.EnumerateArrayOrEmpty())
            {
                var continuationItems = action.GetPropertyOrNull("appendContinuationItemsAction")?.GetPropertyOrNull("continuationItems");
                if (continuationItems != null)
                {
                    // ВАЖНО: Передаем весь массив items, метод сам найдет там и треки, и токен
                    ParseMixedContent(continuationItems.Value, "Continuation");
                    foundAny = true;
                }
            }
        }

        // Вариант Б: continuationContents (Старый формат)
        var contContents = root.GetPropertyOrNull("continuationContents")?.GetPropertyOrNull("musicPlaylistShelfContinuation");
        if (contContents != null)
        {
            var contents = contContents.Value.GetPropertyOrNull("contents");
            if (contents != null)
                ParseMixedContent(contents.Value, "Continuation");

            // В этом формате токен лежит отдельно
            ContinuationToken ??= contContents.Value.GetPropertyOrNull("continuations")?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("nextContinuationData")?.GetPropertyOrNull("continuation")?.GetStringOrNull();

            foundAny = true;
        }

        return foundAny;
    }

    // Этот метод бежит по массиву и выдергивает всё полезное: и треки, и токен
    private void ParseMixedContent(JsonElement itemsArray, string? shelfTitle)
    {
        var tracks = new List<MusicItem>();

        foreach (var item in itemsArray.EnumerateArrayOrEmpty())
        {
            // 1. Это Трек?
            if (item.TryGetProperty("musicResponsiveListItemRenderer", out var trackJson))
            {
                var musicItem = ParseMusicItem(trackJson);
                if (musicItem != null) tracks.Add(musicItem);
                continue;
            }

            // 2. Это Трек (в сетке)?
            if (item.TryGetProperty("musicTwoRowItemRenderer", out var twoRowJson))
            {
                var musicItem = ParseTwoRowItem(twoRowJson);
                if (musicItem != null) tracks.Add(musicItem);
                continue;
            }

            // 3. ЭТО ТОКЕН? (Вот он, родимый, лежит прямо в списке элементов)
            if (item.TryGetProperty("continuationItemRenderer", out var contItem))
            {
                var token = contItem.GetPropertyOrNull("continuationEndpoint")
                    ?.GetPropertyOrNull("continuationCommand")?.GetPropertyOrNull("token")?.GetStringOrNull();

                if (!string.IsNullOrEmpty(token))
                {
                    ContinuationToken = token;
                }
            }
        }

        if (tracks.Count > 0)
        {
            Shelves.Add(new MusicShelf(shelfTitle ?? "Music", tracks));
        }
    }

    // Обертка для старого метода
    private void ParseShelfContent(JsonElement shelf, string? title)
    {
        var displayTitle = title
            ?? shelf.GetPropertyOrNull("header")?.GetPropertyOrNull("musicCarouselShelfBasicHeaderRenderer")?.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull()
            ?? "Tracks";

        var contents = shelf.GetPropertyOrNull("contents");
        if (contents != null)
        {
            ParseMixedContent(contents.Value, displayTitle);
        }
    }

    private void ParseGridContent(JsonElement grid, string title)
    {
        var items = grid.GetPropertyOrNull("items");
        if (items != null) ParseMixedContent(items.Value, title);
    }

    private MusicItem? ParseMusicItem(JsonElement json)
    {
        var id = json.GetPropertyOrNull("playlistItemData")?.GetPropertyOrNull("videoId")?.GetStringOrNull()
              ?? json.EnumerateDescendantProperties("videoId").FirstOrDefault().GetStringOrNull();

        if (id == null) return null;

        var title = json.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(0)
            ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("text")?.GetStringOrNull() ?? "";

        var metaRuns = json.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(1)
            ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs");

        string? author = null;
        string? album = null;

        if (metaRuns != null)
        {
            foreach (var run in metaRuns.Value.EnumerateArrayOrEmpty())
            {
                var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                if (text == null) continue;
                var nav = run.GetPropertyOrNull("navigationEndpoint");
                if (nav != null)
                {
                    var pageType = nav.Value.GetPropertyOrNull("browseEndpoint")
                        ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                        ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                        ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

                    if (pageType == "MUSIC_PAGE_TYPE_ARTIST") author = text;
                    else if (pageType == "MUSIC_PAGE_TYPE_ALBUM") album = text;
                }
                else if (author == null && !text.Contains("views") && !text.Contains("Song") && !text.Contains("Video") && !text.Contains(":"))
                {
                    author = text;
                }
            }
        }

        var durationText = json.GetPropertyOrNull("fixedColumns")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("musicResponsiveListItemFixedColumnRenderer")?.GetPropertyOrNull("text")
            ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("text")?.GetStringOrNull();

        TimeSpan? duration = null;
        if (durationText != null)
        {
            var parts = durationText.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s))
                duration = new TimeSpan(0, m, s);
            else if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m2) && int.TryParse(parts[2], out var s2))
                duration = new TimeSpan(h, m2, s2);
        }

        var thumbs = json.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("musicThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()
            ?.Select(j => new ThumbnailData(j))
            .Select(d => new Thumbnail(d.Url!, new Resolution(d.Width ?? 0, d.Height ?? 0)))
            .ToArray() ?? [];

        return new MusicItem { Id = id, Title = title, Author = author, Album = album, Duration = duration, Thumbnails = thumbs, Type = "Song" };
    }

    private MusicItem? ParseTwoRowItem(JsonElement json)
    {
        var title = json.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("text")?.GetStringOrNull() ?? "";

        var subtitle = json.GetPropertyOrNull("subtitle")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.Select(r => r.GetPropertyOrNull("text")?.GetStringOrNull()).FirstOrDefault();

        var nav = json.GetPropertyOrNull("navigationEndpoint");
        var browseId = nav?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull();
        var watchId = nav?.GetPropertyOrNull("watchEndpoint")?.GetPropertyOrNull("videoId")?.GetStringOrNull();

        string id = "";
        string type = "Video";

        if (browseId != null)
        {
            id = browseId;
            if (id.StartsWith("VL")) id = id[2..];
            if (id.StartsWith("PL") || id.StartsWith("RD") || id == "LM") type = "Playlist";
            else if (id.StartsWith("MPRE") || id.StartsWith("OLAK")) type = "Album";
            else if (id.StartsWith("UC")) type = "Artist";
        }
        else if (watchId != null)
        {
            id = watchId;
            type = "Song";
        }

        if (string.IsNullOrEmpty(id)) return null;

        var thumbs = json.GetPropertyOrNull("thumbnailRenderer")?.GetPropertyOrNull("musicThumbnailRenderer")
            ?.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()
            ?.Select(j => new ThumbnailData(j))
            .Select(d => new Thumbnail(d.Url!, new Resolution(d.Width ?? 0, d.Height ?? 0)))
            .ToArray() ?? [];

        return new MusicItem
        {
            Id = id,
            Title = title,
            Author = subtitle,
            Thumbnails = thumbs,
            Type = type
        };
    }
}