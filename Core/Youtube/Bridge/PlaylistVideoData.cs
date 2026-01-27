using System.Globalization;
using System.Text.Json;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal class PlaylistVideoData(JsonElement content)
{
    public int? Index =>
        content
            .GetPropertyOrNull("navigationEndpoint")
            ?.GetPropertyOrNull("watchEndpoint")
            ?.GetPropertyOrNull("index")
            ?.GetInt32OrNull();

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
        // Some videos have multiple authors. Our current data model does not support that, so we only
        // extract the first one, since it's the channel that actually uploaded the video.
        ?? AuthorDetails
            ?.GetPropertyOrNull("navigationEndpoint")
            ?.GetPropertyOrNull("showDialogCommand")
            ?.GetPropertyOrNull("panelLoadingStrategy")
            ?.GetPropertyOrNull("inlineContent")
            ?.GetPropertyOrNull("dialogViewModel")
            ?.GetPropertyOrNull("customContent")
            ?.GetPropertyOrNull("listViewModel")
            ?.GetPropertyOrNull("listItems")
            ?.EnumerateArrayOrNull()
            ?.FirstOrNull()
            ?.GetPropertyOrNull("listItemViewModel")
            ?.GetPropertyOrNull("rendererContext")
            ?.GetPropertyOrNull("commandContext")
            ?.GetPropertyOrNull("onTap")
            ?.GetPropertyOrNull("innertubeCommand")
            ?.GetPropertyOrNull("browseEndpoint")
            ?.GetPropertyOrNull("browseId")
            ?.GetStringOrNull();

    public TimeSpan? Duration =>
        content
            .GetPropertyOrNull("lengthSeconds")
            ?.GetStringOrNull()
            ?.Pipe(s =>
                double.TryParse(s, CultureInfo.InvariantCulture, out var result)
                    ? result
                    : (double?)null
            )
            ?.Pipe(TimeSpan.FromSeconds)
        ?? content
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
