using System.Globalization;
using System.Text.Json;
using YoutubeExplode.Utils;
using YoutubeExplode.Utils.Extensions;

namespace YoutubeExplode.Bridge;

internal partial class SearchResponse(JsonElement content)
{
    // Search response is incredibly inconsistent (with at least 5 variations),
    // so we employ descendant searching, which is inefficient but resilient.

    private JsonElement? ContentRoot =>
        content.GetPropertyOrNull("contents")
        ?? content.GetPropertyOrNull("onResponseReceivedCommands");

    public IReadOnlyList<VideoData> Videos =>
        ContentRoot
            ?.EnumerateDescendantProperties("videoRenderer")
            .Select(j => new VideoData(j))
            .ToArray() ?? [];

    public IReadOnlyList<PlaylistData> Playlists =>
        ContentRoot
            ?.EnumerateDescendantProperties("lockupViewModel")
            .Select(j => new PlaylistData(j))
            .ToArray()
        ?? ContentRoot
            ?.EnumerateDescendantProperties("playlistRenderer")
            .Select(j => new PlaylistData(j))
            .ToArray()
        ?? [];

    public IReadOnlyList<ChannelData> Channels =>
        ContentRoot
            ?.EnumerateDescendantProperties("channelRenderer")
            .Select(j => new ChannelData(j))
            .ToArray() ?? [];

    public string? ContinuationToken =>
        ContentRoot
            ?.EnumerateDescendantProperties("continuationCommand")
            .FirstOrNull()
            ?.GetPropertyOrNull("token")
            ?.GetStringOrNull();
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
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull()
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
