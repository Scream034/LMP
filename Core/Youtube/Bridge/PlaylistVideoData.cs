using System.Globalization;
using System.Text.Json;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

internal class PlaylistVideoData(JsonElement content)
{
    private bool _authorDetailsCached;
    private JsonElement? _cachedAuthorDetails;

    public int? Index =>
        content
            .GetPropertyOrNull("navigationEndpoint")
            ?.GetPropertyOrNull("watchEndpoint")
            ?.GetPropertyOrNull("index")
            ?.GetInt32OrNull();

    public string? Id => content.GetPropertyOrNull("videoId")?.GetStringOrNull();

    /// <summary>
    /// Текст заголовка видео.
    /// </summary>
    public string? Title
    {
        get
        {
            var titleEl = content.GetPropertyOrNull("title");
            if (titleEl is null) return null;

            return titleEl.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                ?? YoutubeParsingHelpers.ConcatTextRuns(titleEl.Value.GetPropertyOrNull("runs"));
        }
    }

    private JsonElement? AuthorDetails
    {
        get
        {
            if (_authorDetailsCached) return _cachedAuthorDetails;

            _cachedAuthorDetails =
                content.GetPropertyOrNull("longBylineText")
                    ?.GetPropertyOrNull("runs")
                    ?.GetArrayElementOrNull(0)
                ?? content.GetPropertyOrNull("shortBylineText")
                    ?.GetPropertyOrNull("runs")
                    ?.GetArrayElementOrNull(0);

            _authorDetailsCached = true;
            return _cachedAuthorDetails;
        }
    }

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

    /// <summary>
    /// Длительность видеоролика.
    /// </summary>
    public TimeSpan? Duration
    {
        get
        {
            var raw = content.GetPropertyOrNull("lengthSeconds")?.GetStringOrNull();
            if (raw is not null && double.TryParse(raw, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);

            var simpleText = content
                .GetPropertyOrNull("lengthText")
                ?.GetPropertyOrNull("simpleText")
                ?.GetStringOrNull();
            if (simpleText is not null)
                return YoutubeClientUtils.DurationParser.Parse(simpleText);

            var text = YoutubeParsingHelpers.ConcatTextRuns(
                content.GetPropertyOrNull("lengthText")?.GetPropertyOrNull("runs"));

            return text is not null ? YoutubeClientUtils.DurationParser.Parse(text) : null;
        }
    }

    public IReadOnlyList<ThumbnailData> Thumbnails
    {
        get
        {
            var thumbsArray = content
                .GetPropertyOrNull("thumbnail")
                ?.GetPropertyOrNull("thumbnails");

            if (thumbsArray is null || thumbsArray.Value.ValueKind != JsonValueKind.Array)
                return [];

            var array = thumbsArray.Value;
            int len = array.GetArrayLength();
            if (len == 0) return [];

            var result = new ThumbnailData[len];
            for (int i = 0; i < len; i++)
                result[i] = new ThumbnailData(array[i]);
            return result;
        }
    }
}
