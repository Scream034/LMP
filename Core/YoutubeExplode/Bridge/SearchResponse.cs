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
        
        var videos = new List<VideoData>();
        var playlists = new List<PlaylistData>();
        var channels = new List<ChannelData>();

        foreach (var item in items)
        {
            // 1. YouTube Music Item (WEB_REMIX)
            if (item.TryGetProperty("musicResponsiveListItemRenderer", out var musicItemJson))
            {
                // Проверяем, является ли это песней/видео
                var isVideoOrSong = 
                    musicItemJson.EnumerateDescendantProperties("watchEndpoint").Any() ||
                    musicItemJson.EnumerateDescendantProperties("videoId").Any() ||
                    musicItemJson.GetPropertyOrNull("playlistItemData")?.GetPropertyOrNull("videoId") != null;

                if (isVideoOrSong)
                {
                    videos.Add(new VideoData(musicItemJson));
                }
                continue;
            }

            // 2. Standard Video (WEB)
            if (item.TryGetProperty("videoRenderer", out var videoJson))
            {
                videos.Add(new VideoData(videoJson));
                continue;
            }
            
            // 3. Shorts
            if (item.TryGetProperty("shortsLockupViewModel", out var shortJson))
            {
                videos.Add(new VideoData(shortJson));
                continue;
            }

            // 4. Playlists
            if (item.TryGetProperty("playlistRenderer", out var playlistJson))
            {
                playlists.Add(new PlaylistData(playlistJson));
                continue;
            }
            if (item.TryGetProperty("lockupViewModel", out var lockupJson))
            {
                 var contentId = lockupJson.GetPropertyOrNull("contentId")?.GetStringOrNull();
                 if (contentId != null && contentId.StartsWith("PL"))
                        playlists.Add(new PlaylistData(lockupJson));
                 continue;
            }

            // 5. Channels
            if (item.TryGetProperty("channelRenderer", out var channelJson))
            {
                channels.Add(new ChannelData(channelJson));
                continue;
            }
        }

        Videos = videos;
        Playlists = playlists;
        Channels = channels;

        // Поиск токена продолжения
        ContinuationToken = 
            content.GetPropertyOrNull("onResponseReceivedCommands")?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems")?.EnumerateArrayOrNull()?.LastOrDefault()
                .GetPropertyOrNull("continuationItemRenderer")
                ?.GetPropertyOrNull("continuationEndpoint")
                ?.GetPropertyOrNull("continuationCommand")
                ?.GetPropertyOrNull("token")
                ?.GetStringOrNull()
            ??
            content.EnumerateDescendantProperties("continuationCommand")
                .FirstOrDefault().GetPropertyOrNull("token")?.GetStringOrNull();
    }

    private IReadOnlyList<JsonElement> EnumerateItems(JsonElement content)
    {
        var results = new List<JsonElement>();

        // 1. Стандартный список
        var sectionListContents = content.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnSearchResultsRenderer")
            ?.GetPropertyOrNull("primaryContents")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sectionListContents.HasValue)
        {
            foreach (var section in sectionListContents.Value.EnumerateArrayOrEmpty())
            {
                // Музыкальная полка
                if (section.TryGetProperty("musicShelfRenderer", out var musicShelf))
                {
                    foreach(var item in musicShelf.GetPropertyOrNull("contents")?.EnumerateArrayOrEmpty() ?? [])
                         results.Add(item);
                    continue;
                }
                // Обычная секция
                var sectionItems = section.GetPropertyOrNull("itemSectionRenderer")?.GetPropertyOrNull("contents");
                if (sectionItems.HasValue)
                {
                    foreach (var item in sectionItems.Value.EnumerateArrayOrEmpty())
                        results.Add(item);
                }
            }
        }
        
        // 2. YT Music Tabs (иногда поиск возвращает табы: Top result, Songs, Videos...)
        var tabs = content.GetPropertyOrNull("contents")
             ?.GetPropertyOrNull("tabbedSearchResultsRenderer")
             ?.GetPropertyOrNull("tabs");
        
        if (tabs.HasValue)
        {
             foreach(var tab in tabs.Value.EnumerateArrayOrEmpty())
             {
                 var sectionList = tab.GetPropertyOrNull("tabRenderer")?.GetPropertyOrNull("content")?.GetPropertyOrNull("sectionListRenderer")?.GetPropertyOrNull("contents");
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

        // 3. Continuation
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
                         }
                         else
                         {
                             var innerItems = item.GetPropertyOrNull("itemSectionRenderer")?.GetPropertyOrNull("contents");
                             if(innerItems.HasValue)
                             {
                                foreach(var inner in innerItems.Value.EnumerateArrayOrEmpty()) results.Add(inner);
                             }
                             else
                             {
                                results.Add(item);
                             }
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
        public bool IsMusicItem { get; }

        public VideoData(JsonElement content)
        {
            _content = content;
            // В YT Music основной элемент - это musicResponsiveListItemRenderer
            IsMusicItem = content.ValueKind == JsonValueKind.Object && content.TryGetProperty("flexColumns", out _);
        }

        public string? Id
        {
            get
            {
                if (IsMusicItem)
                {
                    // 1. Ищем в navigationEndpoint (самый надежный)
                    var navId = _content.GetPropertyOrNull("playNavigationEndpoint")?.GetPropertyOrNull("watchEndpoint")?.GetPropertyOrNull("videoId")?.GetStringOrNull()
                             ?? _content.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("watchEndpoint")?.GetPropertyOrNull("videoId")?.GetStringOrNull();
                    
                    if (!string.IsNullOrEmpty(navId)) return navId;

                    // 2. Ищем в playlistItemData
                    var plId = _content.GetPropertyOrNull("playlistItemData")?.GetPropertyOrNull("videoId")?.GetStringOrNull();
                    if (!string.IsNullOrEmpty(plId)) return plId;

                    // 3. Fallback
                    return _content.EnumerateDescendantProperties("videoId").FirstOrDefault().GetStringOrNull();
                }

                // Standard YouTube
                return _content.GetPropertyOrNull("videoId")?.GetStringOrNull() ?? 
                       _content.GetPropertyOrNull("onTap")?.GetPropertyOrNull("innertubeCommand")?.GetPropertyOrNull("reelWatchEndpoint")?.GetPropertyOrNull("videoId")?.GetStringOrNull();
            }
        }

        public string? Title
        {
            get
            {
                if (IsMusicItem)
                {
                    // Первая колонка flexColumns
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

                // Standard
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

        public string? Author
        {
            get 
            {
                if (IsMusicItem)
                {
                    // Вторая колонка (Artist • Album • Time)
                    var metaRuns = _content.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(1)
                        ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")?.GetPropertyOrNull("runs");

                    if (metaRuns != null)
                    {
                        foreach (var run in metaRuns.Value.EnumerateArrayOrEmpty())
                        {
                            // Обычно у артиста есть navigationEndpoint с переходом на browse/artist
                            var nav = run.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint");
                            var pageType = nav?.GetPropertyOrNull("browseEndpointContextSupportedConfigs")
                                ?.GetPropertyOrNull("browseEndpointContextMusicConfig")?.GetPropertyOrNull("pageType")?.GetStringOrNull();

                            if (pageType == "MUSIC_PAGE_TYPE_ARTIST") 
                                return run.GetPropertyOrNull("text")?.GetStringOrNull();
                        }
                        // Если не нашли явно, берем первый элемент (часто это артист)
                        return metaRuns.Value.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull();
                    }
                    return null;
                }

                return _content.GetPropertyOrNull("longBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0)?.GetPropertyOrNull("text")?.GetStringOrNull() ??
                       _content.GetPropertyOrNull("shortBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0)?.GetPropertyOrNull("text")?.GetStringOrNull() ??
                       _content.GetPropertyOrNull("ownerText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0)?.GetPropertyOrNull("text")?.GetStringOrNull();
            }
        }

        public string? ChannelId => null; // Упрощено для музыки

        public bool IsOfficialArtist => false; 

        public bool IsShort => 
            _content.TryGetProperty("shortsLockupViewModel", out _) ||
            _content.GetPropertyOrNull("onTap")?.GetPropertyOrNull("innertubeCommand")?.GetPropertyOrNull("reelWatchEndpoint") != null;

        public TimeSpan? Duration
        {
            get
            {
                if (IsMusicItem)
                {
                     // Парсим время из текста во второй колонке
                     var metaRuns = _content.GetPropertyOrNull("flexColumns")?.EnumerateArrayOrNull()?.ElementAtOrNull(1)
                        ?.GetPropertyOrNull("musicResponsiveListItemFlexColumnRenderer")?.GetPropertyOrNull("text")?.GetPropertyOrNull("runs");
                    
                    if (metaRuns != null)
                    {
                        foreach (var run in metaRuns.Value.EnumerateArrayOrEmpty())
                        {
                            var text = run.GetPropertyOrNull("text")?.GetStringOrNull();
                            if (text != null && text.Contains(":")) 
                            {
                                if (TimeSpan.TryParseExact(text.AsSpan(), [@"m\:ss", @"mm\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var r))
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
            _content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("musicThumbnailRenderer")?.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ??
            _content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? 
            _content.GetPropertyOrNull("thumbnailViewModel")?.GetPropertyOrNull("image")?.GetPropertyOrNull("sources")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? 
            [];
    }
    
    public class PlaylistData(JsonElement content)
    {
        public string? Id => content.GetPropertyOrNull("playlistId")?.GetStringOrNull() ?? content.GetPropertyOrNull("contentId")?.GetStringOrNull();
        public string? Title => content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ?? content.GetPropertyOrNull("title")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault().GetPropertyOrNull("text")?.GetStringOrNull() ?? content.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel")?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull();
        public string? Author => null;
        public string? ChannelId => null;
        public IReadOnlyList<ThumbnailData> Thumbnails => content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? [];
    }

    public class ChannelData(JsonElement content)
    {
        public string? Id => content.GetPropertyOrNull("channelId")?.GetStringOrNull();
        public string? Title => content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull();
        public IReadOnlyList<ThumbnailData> Thumbnails => content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? [];
    }
}