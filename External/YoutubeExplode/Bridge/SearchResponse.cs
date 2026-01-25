using System.Globalization;
using System.Text;
using System.Text.Json;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal partial class SearchResponse
{
    // Результаты теперь хранятся в готовых списках, а не вычисляются каждый раз.
    public IReadOnlyList<VideoData> Videos { get; }
    public IReadOnlyList<PlaylistData> Playlists { get; }
    public IReadOnlyList<ChannelData> Channels { get; }
    public string? ContinuationToken { get; }

    // Конструктор выполняет всю работу по парсингу ОДИН РАЗ.
    private SearchResponse(JsonElement content)
    {
        var items = EnumerateItems(content);
        
        var videos = new List<VideoData>(items.Count);
        var playlists = new List<PlaylistData>(items.Count / 4);
        var channels = new List<ChannelData>(items.Count / 4);

        // Прямой цикл вместо LINQ для максимальной производительности
        foreach (var item in items)
        {
            if (item.TryGetProperty("videoRenderer", out var videoJson))
            {
                videos.Add(new VideoData(videoJson));
                continue;
            }
            
            if (item.TryGetProperty("shortsLockupViewModel", out var shortJson))
            {
                videos.Add(new VideoData(shortJson));
                continue;
            }

            if (item.TryGetProperty("playlistRenderer", out var playlistJson))
            {
                playlists.Add(new PlaylistData(playlistJson));
                continue;
            }
            
            if (item.TryGetProperty("lockupViewModel", out var lockupJson) &&
                (lockupJson.GetPropertyOrNull("contentId")?.GetStringOrNull()?.StartsWith("PL") ?? false))
            {
                playlists.Add(new PlaylistData(lockupJson));
                continue;
            }

            if (item.TryGetProperty("channelRenderer", out var channelJson))
            {
                channels.Add(new ChannelData(channelJson));
                continue;
            }
        }

        Videos = videos;
        Playlists = playlists;
        Channels = channels;

        // Токен для загрузки следующей страницы результатов
        ContinuationToken = 
            content.GetPropertyOrNull("onResponseReceivedCommands")?.EnumerateArrayOrNull()?.FirstOrDefault()
                .GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems")?.EnumerateArrayOrNull()?.LastOrDefault()
                .GetPropertyOrNull("continuationItemRenderer")
                ?.GetPropertyOrNull("continuationEndpoint")
                ?.GetPropertyOrNull("continuationCommand")
                ?.GetPropertyOrNull("token")
                ?.GetStringOrNull();
    }

    private IReadOnlyList<JsonElement> EnumerateItems(JsonElement content)
    {
        var results = new List<JsonElement>();

        var sectionListContents = content.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnSearchResultsRenderer")
            ?.GetPropertyOrNull("primaryContents")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents");

        if (sectionListContents.HasValue)
        {
            foreach (var section in sectionListContents.Value.EnumerateArrayOrEmpty())
            {
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
                        else
                        {
                            results.Add(item);
                        }
                    }
                }
            }
        }
        
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
    internal class VideoData(JsonElement content)
    {
        public string? Id => 
            content.GetPropertyOrNull("videoId")?.GetStringOrNull() ?? 
            content.GetPropertyOrNull("onTap")?.GetPropertyOrNull("innertubeCommand")?.GetPropertyOrNull("reelWatchEndpoint")?.GetPropertyOrNull("videoId")?.GetStringOrNull();

        public string? Title
        {
            get
            {
                var runs = content.GetPropertyOrNull("title")?.GetPropertyOrNull("runs");
                if (runs != null)
                {
                    var sb = new StringBuilder();
                    foreach (var run in runs.Value.EnumerateArrayOrEmpty())
                        sb.Append(run.GetPropertyOrNull("text")?.GetStringOrNull());
                    return sb.ToString();
                }
                return content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull() ??
                       content.GetPropertyOrNull("overlayMetadata")?.GetPropertyOrNull("primaryText")?.GetPropertyOrNull("content")?.GetStringOrNull();
            }
        }

        private JsonElement? GetAuthorRun() =>
            content.GetPropertyOrNull("longBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0) ??
            content.GetPropertyOrNull("shortBylineText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0) ??
            content.GetPropertyOrNull("ownerText")?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.ElementAtOrNull(0);

        public string? Author => GetAuthorRun()?.GetPropertyOrNull("text")?.GetStringOrNull();

        public string? ChannelId =>
            GetAuthorRun()?.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull() ??
            content.GetPropertyOrNull("channelThumbnailSupportedRenderers")?.GetPropertyOrNull("channelThumbnailWithLinkRenderer")?.GetPropertyOrNull("navigationEndpoint")?.GetPropertyOrNull("browseEndpoint")?.GetPropertyOrNull("browseId")?.GetStringOrNull();

        public bool IsOfficialArtist =>
            content.GetPropertyOrNull("ownerBadges")?.EnumerateArrayOrNull()?.Any(b =>
                b.GetPropertyOrNull("metadataBadgeRenderer")?.GetPropertyOrNull("style")?.GetStringOrNull() == "BADGE_STYLE_TYPE_VERIFIED_ARTIST"
            ) ?? false;

        public bool IsShort => 
            content.TryGetProperty("shortsLockupViewModel", out _) ||
            content.GetPropertyOrNull("onTap")?.GetPropertyOrNull("innertubeCommand")?.GetPropertyOrNull("reelWatchEndpoint") != null;

        public TimeSpan? Duration =>
            content.GetPropertyOrNull("lengthText")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                ?.Pipe<string?, TimeSpan?>(s => TimeSpan.TryParseExact(s.AsSpan(), [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var r) ? r : null);

        public IReadOnlyList<ThumbnailData> Thumbnails =>
            content.GetPropertyOrNull("thumbnail")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? 
            content.GetPropertyOrNull("thumbnailViewModel")?.GetPropertyOrNull("image")?.GetPropertyOrNull("sources")?.EnumerateArrayOrNull()?.Select(j => new ThumbnailData(j)).ToArray() ?? 
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