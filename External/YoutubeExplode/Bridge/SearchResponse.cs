using System.Globalization;
using System.Text;
using System.Text.Json;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal partial class SearchResponse
{
    public IReadOnlyList<VideoData> Videos { get; }
    public IReadOnlyList<PlaylistData> Playlists { get; }
    public IReadOnlyList<ChannelData> Channels { get; }
    public string? ContinuationToken { get; }

    private SearchResponse(JsonElement content)
    {
        var items = EnumerateItems(content);
        
        var videos = new List<VideoData>(items.Count);
        var playlists = new List<PlaylistData>(items.Count / 4);
        var channels = new List<ChannelData>(items.Count / 4);

        foreach (var item in items)
        {
            // 1. Стандартный VideoRenderer (YouTube WEB)
            if (item.TryGetProperty("videoRenderer", out var videoJson))
            {
                videos.Add(new VideoData(videoJson));
                continue;
            }
            
            // 2. Shorts (YouTube WEB)
            if (item.TryGetProperty("shortsLockupViewModel", out var shortJson))
            {
                videos.Add(new VideoData(shortJson));
                continue;
            }

            // 3. YouTube Music Item Renderer (WEB_REMIX)
            // Это основной элемент выдачи YT Music. Может быть песней или видео.
            if (item.TryGetProperty("musicResponsiveListItemRenderer", out var musicItemJson))
            {
                // Проверяем, является ли это песней/видео.
                // Обычно определяем по navigationEndpoint -> watchEndpoint.
                // Если это browseEndpoint (например, артист или альбом), то это не видео.
                var isVideoOrSong = 
                    musicItemJson.EnumerateDescendantProperties("watchEndpoint").Any() ||
                    musicItemJson.EnumerateDescendantProperties("videoId").Any();

                if (isVideoOrSong)
                {
                    videos.Add(new VideoData(musicItemJson));
                }
                else
                {
                    // Логика для альбомов/плейлистов YT Music, если нужно
                    // playlists.Add(new PlaylistData(musicItemJson)); 
                }
                continue;
            }

            // 4. Стандартный PlaylistRenderer (YouTube WEB)
            if (item.TryGetProperty("playlistRenderer", out var playlistJson))
            {
                playlists.Add(new PlaylistData(playlistJson));
                continue;
            }
            
            // 5. LockupViewModel (YouTube WEB новый дизайн)
            if (item.TryGetProperty("lockupViewModel", out var lockupJson))
            {
                var contentId = lockupJson.GetPropertyOrNull("contentId")?.GetStringOrNull();
                if (contentId != null)
                {
                    if (contentId.StartsWith("PL"))
                        playlists.Add(new PlaylistData(lockupJson));
                    // Можно добавить обработку видео для LockupViewModel, если понадобится
                }
                continue;
            }

            // 6. ChannelRenderer (YouTube WEB)
            if (item.TryGetProperty("channelRenderer", out var channelJson))
            {
                channels.Add(new ChannelData(channelJson));
                continue;
            }
        }

        Videos = videos;
        Playlists = playlists;
        Channels = channels;

        // Поиск токена продолжения (ContinuationToken)
        ContinuationToken = 
            // Стандартный путь
            content.GetPropertyOrNull("onResponseReceivedCommands")?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems")?.EnumerateArrayOrNull()?.LastOrDefault()
                .GetPropertyOrNull("continuationItemRenderer")
                ?.GetPropertyOrNull("continuationEndpoint")
                ?.GetPropertyOrNull("continuationCommand")
                ?.GetPropertyOrNull("token")
                ?.GetStringOrNull()
            ??
            // Путь для YouTube Music (иногда отличается в shelf)
            content.EnumerateDescendantProperties("continuationCommand")
                .FirstOrDefault().GetPropertyOrNull("token")?.GetStringOrNull();
    }

    private IReadOnlyList<JsonElement> EnumerateItems(JsonElement content)
    {
        var results = new List<JsonElement>();

        // 1. Обработка sectionListRenderer (Классический YouTube)
        var sectionListContents = content.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnSearchResultsRenderer")
            ?.GetPropertyOrNull("primaryContents")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sectionListContents.HasValue)
        {
            foreach (var section in sectionListContents.Value.EnumerateArrayOrEmpty())
            {
                // YT Music часто возвращает musicShelfRenderer внутри sectionList
                if (section.TryGetProperty("musicShelfRenderer", out var musicShelf))
                {
                    foreach(var item in musicShelf.GetPropertyOrNull("contents")?.EnumerateArrayOrEmpty() ?? [])
                    {
                        results.Add(item);
                    }
                    continue;
                }

                var sectionItems = section.GetPropertyOrNull("itemSectionRenderer")?.GetPropertyOrNull("contents");
                if (sectionItems.HasValue)
                {
                    foreach (var item in sectionItems.Value.EnumerateArrayOrEmpty())
                    {
                        if (item.TryGetProperty("gridShelfViewModel", out var gridShelf))
                        {
                            foreach (var shelfItem in gridShelf.GetPropertyOrNull("contents")?.EnumerateArrayOrEmpty() ?? [])
                                results.Add(shelfItem);
                        }
                        else if (item.TryGetProperty("shelfRenderer", out var shelf))
                        {
                             // Обработка обычных полок
                             foreach (var shelfItem in shelf.GetPropertyOrNull("content")?.GetPropertyOrNull("verticalListRenderer")?.GetPropertyOrNull("items")?.EnumerateArrayOrEmpty() ?? [])
                                results.Add(shelfItem);
                        }
                        else
                        {
                            results.Add(item);
                        }
                    }
                }
            }
        }
        
        // 2. Обработка tabRenderer (YT Music иногда возвращает структуру табов)
        var tabs = content.GetPropertyOrNull("contents")
             ?.GetPropertyOrNull("tabbedSearchResultsRenderer")
             ?.GetPropertyOrNull("tabs");
        
        if (tabs.HasValue)
        {
             foreach(var tab in tabs.Value.EnumerateArrayOrEmpty())
             {
                 var tabContent = tab.GetPropertyOrNull("tabRenderer")?.GetPropertyOrNull("content");
                 var sectionList = tabContent?.GetPropertyOrNull("sectionListRenderer")?.GetPropertyOrNull("contents");
                 
                 if (sectionList.HasValue)
                 {
                      foreach (var section in sectionList.Value.EnumerateArrayOrEmpty())
                      {
                           if (section.TryGetProperty("musicShelfRenderer", out var musicShelf))
                           {
                                foreach(var item in musicShelf.GetPropertyOrNull("contents")?.EnumerateArrayOrEmpty() ?? [])
                                     results.Add(item);
                           }
                      }
                 }
             }
        }

        // 3. Обработка команд продолжения (Continuation)
        var continuationCommands = content.GetPropertyOrNull("onResponseReceivedCommands");
        if (continuationCommands.HasValue)
        {
            foreach (var cmd in continuationCommands.Value.EnumerateArrayOrEmpty())
            {
                var continuationItems = cmd.GetPropertyOrNull("appendContinuationItemsAction")?.GetPropertyOrNull("continuationItems");
                if (continuationItems.HasValue)
                {
                    foreach (var item in continuationItems.Value.EnumerateArrayOrEmpty())
                    {
                         if (item.TryGetProperty("musicResponsiveListItemRenderer", out _))
                         {
                             results.Add(item);
                             continue;
                         }

                         var innerItems = item.GetPropertyOrNull("itemSectionRenderer")?.GetPropertyOrNull("contents");
                         if(innerItems.HasValue)
                         {
                            foreach(var inner in innerItems.Value.EnumerateArrayOrEmpty())
                                results.Add(inner);
                         }
                         else
                         {
                            results.Add(item);
                         }
                    }
                }
            }
        }
        
        return results;
    }

    public static SearchResponse Parse(string raw) => new(Json.Parse(raw));
}

internal partial class SearchResponse
{
    internal class VideoData
    {
        private readonly JsonElement _content;
        private readonly bool _isMusicItem;

        public VideoData(JsonElement content)
        {
            _content = content;
            _isMusicItem = content.ValueKind == JsonValueKind.Object && content.TryGetProperty("flexColumns", out _);
        }

        public string? Id
        {
            get
            {
                if (_isMusicItem)
                {
                    return _content.GetPropertyOrNull("playlistItemData")?.GetPropertyOrNull("videoId")?.GetStringOrNull()
                           ?? _content.EnumerateDescendantProperties("videoId").FirstOrDefault().GetStringOrNull();
                }

                return _content.GetPropertyOrNull("videoId")?.GetStringOrNull() ?? 
                       _content.GetPropertyOrNull("onTap")?.GetPropertyOrNull("innertubeCommand")?.GetPropertyOrNull("reelWatchEndpoint")?.GetPropertyOrNull("videoId")?.GetStringOrNull();
            }
        }

        public string? Title
        {
            get
            {
                if (_isMusicItem)
                {
                    // В YT Music заголовок обычно в первой flexColumn
                    var titleRuns = _content.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(0)
                        ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")?.GetPropertyOrNull("runs");
                    
                    if (titleRuns != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var run in titleRuns.Value.EnumerateArrayOrEmpty())
                            sb.Append(run.GetPropertyOrNull("text")?.GetStringOrNull());
                        return sb.ToString();
                    }
                }

                var runs = _content.GetPropertyOrNull("title")?.GetPropertyOrNull("runs");
                if (runs != null)
                {
                    var sb = new StringBuilder();
                    foreach (var run in runs.Value.EnumerateArrayOrEmpty())
                        sb.Append(run.GetPropertyOrNull("text")?.GetStringOrNull());
                    return sb.ToString();
                }
                return _content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ??
                       _content.GetPropertyOrNull("overlayMetadata")?.GetPropertyOrNull("primaryText")?.GetPropertyOrNull("content")?.GetStringOrNull();
            }
        }

        // Вспомогательный метод для поиска блока runs, содержащего автора
        private JsonElement? GetAuthorRun()
        {
            if (_isMusicItem)
            {
                // В YT Music метаданные (Артист, Альбом, Время) во второй flexColumn
                var metaRuns = _content.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(1)
                    ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")?.GetPropertyOrNull("runs");

                // Ищем run, у которого есть navigationEndpoint с переходом на канал/артиста
                if (metaRuns != null)
                {
                    foreach (var run in metaRuns.Value.EnumerateArrayOrEmpty())
                    {
                        var pageType = run.GetPropertyOrNull("navigationEndpoint")
                            ?.GetPropertyOrNull("browseEndpoint")
                            ?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                            ?.GetPropertyOrNull("browseEndpointContextMusicConfig")
                            ?.GetPropertyOrNull("pageType")?.GetStringOrNull();

                        // MUSIC_PAGE_TYPE_ARTIST или просто наличие browseId, если это не альбом
                        if (pageType == "MUSIC_PAGE_TYPE_ARTIST" || 
                            (run.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull()?.StartsWith("UC") == true))
                        {
                            return run;
                        }
                    }
                    // Если не нашли явно артиста, берем первый элемент (часто это артист)
                    return metaRuns.Value.EnumerateArrayOrNull()?.FirstOrDefault();
                }
                return null;
            }

            return _content.GetPropertyOrNull("longBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0) ??
                   _content.GetPropertyOrNull("shortBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0) ??
                   _content.GetPropertyOrNull("ownerText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0);
        }

        public string? Author => GetAuthorRun()?.GetPropertyOrNull("text")?.GetStringOrNull();

        public string? ChannelId =>
            GetAuthorRun()?.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull() ??
            _content.GetPropertyOrNull("channelThumbnailSupportedRenderers")?.GetPropertyOrNull("channelThumbnailWithLinkRenderer")?.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull();

        public bool IsOfficialArtist =>
            _content.GetPropertyOrNull("ownerBadges")?.EnumerateArrayOrNull()?.Any(b =>
                b.GetPropertyOrNull("metadataBadgeRenderer")?.GetPropertyOrNull("style")?.GetStringOrNull() == "BADGE_STYLE_TYPE_VERIFIED_ARTIST"
            ) ?? false;

        public bool IsShort => 
            _content.TryGetProperty("shortsLockupViewModel", out _) ||
            _content.GetPropertyOrNull("onTap")?.GetPropertyOrNull("innertubeCommand")?.GetPropertyOrNull("reelWatchEndpoint") != null;

        public TimeSpan? Duration
        {
            get
            {
                if (_isMusicItem)
                {
                    // В YT Music длительность обычно находится в конце списка runs во второй колонке
                     var metaRuns = _content.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(1)
                        ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")?.GetPropertyOrNull("runs");
                    
                    if (metaRuns != null)
                    {
                        foreach (var run in metaRuns.Value.EnumerateArrayOrEmpty())
                        {
                            var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                            if (text != null && text.Contains(":")) // Примитивная эвристика времени
                            {
                                if (TimeSpan.TryParseExact(text.AsSpan(), [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var r))
                                    return r;
                            }
                        }
                    }
                    return null;
                }

                return _content.GetPropertyOrNull("lengthText")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                    ?.Pipe<string?, TimeSpan?>(s => TimeSpan.TryParseExact(s.AsSpan(), [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var r) ? r : null);
            }
        }

        public IReadOnlyList<ThumbnailData> Thumbnails =>
            // YT Music Thumbnail
            _content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("musicThumbnailRenderer")?.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ??
            // Standard YouTube Thumbnail
            _content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? 
            _content.GetPropertyOrNull("thumbnailViewModel")?.GetPropertyOrNull("image")?.GetPropertyOrNull("sources")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? 
            [];
    }
    
    public class PlaylistData(JsonElement content)
    {
        public string? Id =>
            content.GetPropertyOrNull("playlistId")?.GetStringOrNull() ??
            content.GetPropertyOrNull("contentId")?.GetStringOrNull();

        public string? Title =>
            content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ??
            content.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull() ??
            content.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel")?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull();

        private JsonElement? GetAuthorRun() =>
            content.GetPropertyOrNull("longBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault();

        public string? Author => GetAuthorRun()?.GetPropertyOrNull("text")?.GetStringOrNull();
        public string? ChannelId => GetAuthorRun()?.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull();
        public IReadOnlyList<ThumbnailData> Thumbnails => content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? [];
    }

    public class ChannelData(JsonElement content)
    {
        public string? Id => content.GetPropertyOrNull("channelId")?.GetStringOrNull();
        public string? Title => content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();
        public IReadOnlyList<ThumbnailData> Thumbnails => content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? [];
    }
}