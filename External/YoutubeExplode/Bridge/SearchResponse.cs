using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal partial class SearchResponse(JsonElement content)
{
    // Optimized traversal without EnumerateDescendantProperties

    public IEnumerable<VideoData> Videos =>
        EnumerateItems().Select(i => i.GetPropertyOrNull("videoRenderer")).WhereNotNull().Select(j => new VideoData(j));

    public IEnumerable<PlaylistData> Playlists =>
        EnumerateItems()
        .Select(i =>
            i.GetPropertyOrNull("lockupViewModel") ??
            i.GetPropertyOrNull("playlistRenderer"))
        .WhereNotNull()
        .Select(j => new PlaylistData(j));

    public IEnumerable<ChannelData> Channels =>
        EnumerateItems().Select(i => i.GetPropertyOrNull("channelRenderer")).WhereNotNull().Select(j => new ChannelData(j));

    public string? ContinuationToken =>
        // Try specific path first
        content.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnSearchResultsRenderer")
            ?.GetPropertyOrNull("primaryContents")
            ?.GetPropertyOrNull("sectionListRenderer")
            ?.GetPropertyOrNull("contents")?.EnumerateArrayOrNull()?.LastOrDefault()
            .GetPropertyOrNull("continuationItemRenderer")
            ?.GetPropertyOrNull("continuationEndpoint")
            ?.GetPropertyOrNull("continuationCommand")
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull()
        ??
        // Try continuation path
        content.GetPropertyOrNull("onResponseReceivedCommands")?.EnumerateArrayOrNull()?.FirstOrNull()
            ?.GetPropertyOrNull("appendContinuationItemsAction")
            ?.GetPropertyOrNull("continuationItems")?.EnumerateArrayOrNull()?.LastOrDefault()
            .GetPropertyOrNull("continuationItemRenderer")
            ?.GetPropertyOrNull("continuationEndpoint")
            ?.GetPropertyOrNull("continuationCommand")
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull();

    private IEnumerable<JsonElement> EnumerateItems()
    {
        // 1. Initial Search Response
        var primaryContents = content.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("twoColumnSearchResultsRenderer")
            ?.GetPropertyOrNull("primaryContents");

        if (primaryContents != null)
        {
            var sectionListContents = primaryContents.Value.GetPropertyOrNull("sectionListRenderer")
                ?.GetPropertyOrNull("contents");

            if (sectionListContents != null)
            {
                foreach (var section in sectionListContents.Value.EnumerateArrayOrEmpty())
                {
                    var items = section.GetPropertyOrNull("itemSectionRenderer")?.GetPropertyOrNull("contents");
                    if (items != null)
                    {
                        foreach (var item in items.Value.EnumerateArrayOrEmpty())
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        // 2. Continuation Response
        var commands = content.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands != null)
        {
            foreach (var cmd in commands.Value.EnumerateArrayOrEmpty())
            {
                var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");

                if (items != null)
                {
                    foreach (var item in items.Value.EnumerateArrayOrEmpty())
                    {
                        var itemSection = item.GetPropertyOrNull("itemSectionRenderer")?.GetPropertyOrNull("contents");
                        if (itemSection != null)
                        {
                            foreach (var innerItem in itemSection.Value.EnumerateArrayOrEmpty())
                                yield return innerItem;
                        }
                        else
                        {
                            yield return item;
                        }
                    }
                }
            }
        }
    }
}

internal partial class SearchResponse
{
    internal class VideoData(JsonElement content)
    {
        public string? Id => content.GetPropertyOrNull("videoId")?.GetStringOrNull();

        public string? Title =>
            content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("title")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetPropertyOrNull("text")?.GetStringOrNull())
                .WhereNotNull()
                .Pipe(string.Concat);

        private JsonElement? AuthorDetails =>
            content
                .GetPropertyOrNull("longBylineText")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.ElementAtOrNull(0)
            ?? content
                .GetPropertyOrNull("shortBylineText")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.ElementAtOrNull(0);

        public bool IsOfficialArtist =>
            content.GetPropertyOrNull("ownerBadges")
                ?.EnumerateArrayOrNull()
                ?.Any(b =>
                    b.GetPropertyOrNull("metadataBadgeRenderer")
                        ?.GetPropertyOrNull("style")
                        ?.GetStringOrNull() == "BADGE_STYLE_TYPE_VERIFIED_ARTIST"
                    ||
                    b.GetPropertyOrNull("metadataBadgeRenderer")
                        ?.GetPropertyOrNull("tooltip")
                        ?.GetStringOrNull() == "Official Artist Channel"
                ) ?? false;

        public string? Author => AuthorDetails?.GetPropertyOrNull("text")?.GetStringOrNull();

        public string? ChannelId =>
            AuthorDetails
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("channelThumbnailSupportedRenderers")
                ?.GetPropertyOrNull("channelThumbnailWithLinkRenderer")
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull();

        public TimeSpan? Duration =>
            content
                .GetPropertyOrNull("lengthText")
                ?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                ?.Pipe(s =>
                    TimeSpan.TryParseExact(
                        s,
                        [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"],
                        CultureInfo.InvariantCulture,
                        out var result
                    )
                        ? result
                        : (TimeSpan?)null
                )
            ?? content
                .GetPropertyOrNull("lengthText")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetPropertyOrNull("text")?.GetStringOrNull())
                .WhereNotNull()
                .Pipe(string.Concat)
                ?.Pipe(s =>
                    TimeSpan.TryParseExact(
                        s,
                        [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"],
                        CultureInfo.InvariantCulture,
                        out var result
                    )
                        ? result
                        : (TimeSpan?)null
                );

        public IReadOnlyList<ThumbnailData> Thumbnails =>
            content
                .GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails")
                ?.EnumerateArrayOrNull()
                ?.Select(j => new ThumbnailData(j))
                .ToArray() ?? [];
    }
}

internal partial class SearchResponse
{
    public class PlaylistData(JsonElement content)
    {
        public string? Id =>
            content.GetPropertyOrNull("contentId")?.GetStringOrNull()
            ?? content.GetPropertyOrNull("playlistId")?.GetStringOrNull();

        private JsonElement? Metadata =>
            content.GetPropertyOrNull("metadata")?.GetPropertyOrNull("lockupMetadataViewModel");

        public string? Title =>
            Metadata?.GetPropertyOrNull("title")?.GetPropertyOrNull("content")?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("title")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("title")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetPropertyOrNull("text")?.GetStringOrNull())
                .WhereNotNull()
                .Pipe(string.Concat);

        private JsonElement? AuthorDetails =>
            Metadata
                ?.EnumerateDescendantProperties("metadataParts")
                ?.ElementAtOrNull(0)
                ?.EnumerateArrayOrNull()
                ?.ElementAtOrNull(0)
                ?.GetPropertyOrNull("text")
            ?? content
                .GetPropertyOrNull("longBylineText")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.ElementAtOrNull(0);

        public string? Author =>
            AuthorDetails?.GetPropertyOrNull("content")?.GetStringOrNull()
            ?? AuthorDetails?.GetPropertyOrNull("text")?.GetStringOrNull();

        public string? ChannelId =>
            AuthorDetails
                ?.GetPropertyOrNull("commandRuns")
                ?.EnumerateArrayOrNull()
                ?.ElementAtOrNull(0)
                ?.GetPropertyOrNull("onTap")
                ?.GetPropertyOrNull("innertubeCommand")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull()
            ?? AuthorDetails
                ?.GetPropertyOrNull("navigationEndpoint")
                ?.GetPropertyOrNull("browseEndpoint")
                ?.GetPropertyOrNull("browseId")
                ?.GetStringOrNull();

        public IReadOnlyList<ThumbnailData> Thumbnails =>
            content
                .GetPropertyOrNull("contentImage")
                ?.GetPropertyOrNull("collectionThumbnailViewModel")
                ?.GetPropertyOrNull("primaryThumbnail")
                ?.GetPropertyOrNull("thumbnailViewModel")
                ?.GetPropertyOrNull("image")
                ?.GetPropertyOrNull("sources")
                ?.EnumerateArrayOrEmpty()
                .Select(j => new ThumbnailData(j))
                .ToArray()
            ?? content
                .GetPropertyOrNull("thumbnails")
                ?.EnumerateDescendantProperties("thumbnails")
                .SelectMany(j => j.EnumerateArrayOrEmpty())
                .Select(j => new ThumbnailData(j))
                .ToArray()
            ?? [];
    }
}

internal partial class SearchResponse
{
    public class ChannelData(JsonElement content)
    {
        public string? Id => content.GetPropertyOrNull("channelId")?.GetStringOrNull();

        public string? Title =>
            content.GetPropertyOrNull("title")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("title")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetPropertyOrNull("text")?.GetStringOrNull())
                .WhereNotNull()
                .Pipe(string.Concat);

        public IReadOnlyList<ThumbnailData> Thumbnails =>
            content
                .GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails")
                ?.EnumerateArrayOrNull()
                ?.Select(j => new ThumbnailData(j))
                .ToArray() ?? [];
    }
}

internal partial class SearchResponse
{
    public static SearchResponse Parse(string raw) => new(Json.Parse(raw));
}