using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlayerResponse(JsonElement content)
{
    private JsonElement? Playability => content.GetPropertyOrNull("playabilityStatus");

    private string? PlayabilityStatus =>
        Playability?.GetPropertyOrNull("status")?.GetStringOrNull();

    public string? PlayabilityError => Playability?.GetPropertyOrNull("reason")?.GetStringOrNull();

    public string? Category =>
        content
            .GetPropertyOrNull("microformat")
            ?.GetPropertyOrNull("playerMicroformatRenderer")
            ?.GetPropertyOrNull("category")
            ?.GetStringOrNull();

    public bool IsMusic =>
        string.Equals(Category, "Music", StringComparison.OrdinalIgnoreCase) ||
        content.GetPropertyOrNull("videoDetails")?.GetPropertyOrNull("musicVideoType") != null;

    public bool IsAvailable =>
        !string.Equals(PlayabilityStatus, "error", StringComparison.OrdinalIgnoreCase)
        && Details is not null;

    public bool IsPlayable =>
        string.Equals(PlayabilityStatus, "ok", StringComparison.OrdinalIgnoreCase);

    private JsonElement? Details => content.GetPropertyOrNull("videoDetails");

    public string? Title => Details?.GetPropertyOrNull("title")?.GetStringOrNull();

    public string? ChannelId => Details?.GetPropertyOrNull("channelId")?.GetStringOrNull();

    public string? Author => Details?.GetPropertyOrNull("author")?.GetStringOrNull();

    public DateTimeOffset? UploadDate =>
        content
            .GetPropertyOrNull("microformat")
            ?.GetPropertyOrNull("playerMicroformatRenderer")
            ?.GetPropertyOrNull("uploadDate")
            ?.GetDateTimeOffset();

    public TimeSpan? Duration =>
        Details
            ?.GetPropertyOrNull("lengthSeconds")
            ?.GetStringOrNull()
            ?.Pipe(static s =>
                double.TryParse(s, CultureInfo.InvariantCulture, out var result)
                    ? result
                    : (double?)null
            )
            ?.Pipe(TimeSpan.FromSeconds);

    public IReadOnlyList<ThumbnailData> Thumbnails =>
        Details
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => new ThumbnailData(j))
            .ToArray() ?? [];

    public IReadOnlyList<string> Keywords =>
        Details
            ?.GetPropertyOrNull("keywords")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetStringOrNull())
            .WhereNotNull()
            .ToArray() ?? [];

    public string? Description => Details?.GetPropertyOrNull("shortDescription")?.GetStringOrNull();

    public long? ViewCount =>
        Details
            ?.GetPropertyOrNull("viewCount")
            ?.GetStringOrNull()
            ?.Pipe(static s =>
                long.TryParse(s, CultureInfo.InvariantCulture, out var result)
                    ? result
                    : (long?)null
            );

    public string? PreviewVideoId =>
        Playability
            ?.GetPropertyOrNull("errorScreen")
            ?.GetPropertyOrNull("playerLegacyDesktopYpcTrailerRenderer")
            ?.GetPropertyOrNull("trailerVideoId")
            ?.GetStringOrNull()
        ?? Playability
            ?.GetPropertyOrNull("errorScreen")
            ?.GetPropertyOrNull("ypcTrailerRenderer")
            ?.GetPropertyOrNull("playerVars")
            ?.GetStringOrNull()
            ?.Pipe(UrlEx.GetQueryParameters)
            .GetValueOrDefault("video_id")
        ?? Playability
            ?.GetPropertyOrNull("errorScreen")
            ?.GetPropertyOrNull("ypcTrailerRenderer")
            ?.GetPropertyOrNull("playerResponse")
            ?.GetStringOrNull()
            ?
            // YouTube uses weird base64-like encoding here.
            .Replace('-', '+')
            .Replace('_', '/')
            .Pipe(Convert.FromBase64String)
            .Pipe(Encoding.UTF8.GetString)
            .Pipe(static s => MyRegex().Match(s).Groups[1].Value)
            .NullIfWhiteSpace();

    private JsonElement? StreamingData => content.GetPropertyOrNull("streamingData");

    public string? DashManifestUrl =>
        StreamingData?.GetPropertyOrNull("dashManifestUrl")?.GetStringOrNull();

    public string? HlsManifestUrl =>
        StreamingData?.GetPropertyOrNull("hlsManifestUrl")?.GetStringOrNull();

    // Lazy enumerable to save allocations
    public IEnumerable<IStreamData> Streams
    {
        get
        {
            var formats = StreamingData?.GetPropertyOrNull("formats");
            if (formats != null)
            {
                foreach (var j in formats.Value.EnumerateArrayOrEmpty())
                    yield return new StreamData(j);
            }

            var adaptiveFormats = StreamingData?.GetPropertyOrNull("adaptiveFormats");
            if (adaptiveFormats != null)
            {
                foreach (var j in adaptiveFormats.Value.EnumerateArrayOrEmpty())
                    yield return new StreamData(j);
            }
        }
    }

    public IEnumerable<ClosedCaptionTrackData> ClosedCaptionTracks
    {
        get
        {
            var tracks = content
                .GetPropertyOrNull("captions")
                ?.GetPropertyOrNull("playerCaptionsTracklistRenderer")
                ?.GetPropertyOrNull("captionTracks");

            if (tracks != null)
            {
                foreach (var j in tracks.Value.EnumerateArrayOrEmpty())
                    yield return new ClosedCaptionTrackData(j);
            }
        }
    }

    [GeneratedRegex(@"video_id=(.{11})")]
    private static partial Regex MyRegex();
}

internal partial class PlayerResponse
{
    public class ClosedCaptionTrackData(JsonElement content)
    {
        public string? Url => content.GetPropertyOrNull("baseUrl")?.GetStringOrNull();

        public string? LanguageCode => content.GetPropertyOrNull("languageCode")?.GetStringOrNull();

        public string? LanguageName =>
            content.GetPropertyOrNull("name")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("name")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetPropertyOrNull("text")?.GetStringOrNull())
                .WhereNotNull()
                .Pipe(string.Concat);

        public bool IsAutoGenerated =>
            content
                .GetPropertyOrNull("vssId")
                ?.GetStringOrNull()
                ?.StartsWith("a.", StringComparison.OrdinalIgnoreCase) ?? false;
    }
}

internal partial class PlayerResponse
{
    public class StreamData(JsonElement content) : IStreamData
    {
        public int? Itag => content.GetPropertyOrNull("itag")?.GetInt32OrNull();

        private IReadOnlyDictionary<string, string>? CipherData =>
            content.GetPropertyOrNull("cipher")?.GetStringOrNull()?.Pipe(UrlEx.GetQueryParameters)
            ?? content
                .GetPropertyOrNull("signatureCipher")
                ?.GetStringOrNull()
                ?.Pipe(UrlEx.GetQueryParameters);

        public string? Url =>
            content.GetPropertyOrNull("url")?.GetStringOrNull()
            ?? CipherData?.GetValueOrDefault("url");

        public string? Signature => CipherData?.GetValueOrDefault("s");

        public string? SignatureParameter => CipherData?.GetValueOrDefault("sp");

        public long? ContentLength =>
            content
                .GetPropertyOrNull("contentLength")
                ?.GetStringOrNull()
                ?.Pipe(s =>
                    long.TryParse(s, CultureInfo.InvariantCulture, out var result)
                        ? result
                        : (long?)null
                )
            ?? Url?.Pipe(s => UrlEx.TryGetQueryParameterValue(s, "clen"))
                ?.NullIfWhiteSpace()
                ?.Pipe(s =>
                    long.TryParse(s, CultureInfo.InvariantCulture, out var result)
                        ? result
                        : (long?)null
                );

        public long? Bitrate => content.GetPropertyOrNull("bitrate")?.GetInt64OrNull();

        private string? MimeType => content.GetPropertyOrNull("mimeType")?.GetStringOrNull();

        public string? Container => MimeType?.SubstringUntil(";").SubstringAfter("/");

        private bool IsAudioOnly =>
            MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false;

        public string? Codecs => MimeType?.SubstringAfter("codecs=\"").SubstringUntil("\"");

        public string? AudioCodec =>
            IsAudioOnly ? Codecs : Codecs?.SubstringAfter(", ").NullIfWhiteSpace();

        public string? AudioLanguageCode =>
            content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("id")
                ?.GetStringOrNull()
                ?.SubstringUntil(".");

        public string? AudioLanguageName =>
            content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("displayName")
                ?.GetStringOrNull();

        public bool? IsAudioLanguageDefault =>
            content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("audioIsDefault")
                ?.GetBooleanOrNull();

        public string? VideoCodec
        {
            get
            {
                var codec = IsAudioOnly ? null : Codecs?.SubstringUntil(", ").NullIfWhiteSpace();

                if (string.Equals(codec, "unknown", StringComparison.OrdinalIgnoreCase))
                    return "av01.0.05M.08";

                return codec;
            }
        }

        public string? VideoQualityLabel =>
            content.GetPropertyOrNull("qualityLabel")?.GetStringOrNull();

        public int? VideoWidth => content.GetPropertyOrNull("width")?.GetInt32OrNull();

        public int? VideoHeight => content.GetPropertyOrNull("height")?.GetInt32OrNull();

        public int? VideoFramerate => content.GetPropertyOrNull("fps")?.GetInt32OrNull();
    }
}

internal partial class PlayerResponse
{
    public static PlayerResponse Parse(string raw) => new(Json.Parse(raw));
}