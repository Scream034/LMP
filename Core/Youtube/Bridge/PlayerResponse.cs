using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlayerResponse(JsonElement content)
{
    private JsonElement? Playability => content.GetPropertyOrNull("playabilityStatus");

    /// <inheritdoc/>
    private string? PlayabilityStatus =>
        Playability?.GetPropertyOrNull("status")?.GetStringOrNull();

    /// <inheritdoc/>
    public string? PlayabilityError => Playability?.GetPropertyOrNull("reason")?.GetStringOrNull();

    /// <summary>
    /// Требуется ли авторизация для просмотра (LOGIN_REQUIRED).
    /// </summary>
    public bool IsLoginRequired =>
        string.Equals(PlayabilityStatus, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Причина требования авторизации.
    /// </summary>
    public LoginRequiredReason LoginRequiredReason
    {
        get
        {
            if (!IsLoginRequired)
                return LoginRequiredReason.Unknown;

            var reason = PlayabilityError ?? "";
            var desktopAgeGateReason = Playability
                ?.GetPropertyOrNull("desktopLegacyAgeGateReason")
                ?.GetInt32OrNull();

            // desktopLegacyAgeGateReason: 1 = age restricted
            if (desktopAgeGateReason == 1 ||
                reason.Contains("age", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("confirm", StringComparison.OrdinalIgnoreCase))
            {
                return LoginRequiredReason.AgeRestricted;
            }

            if (reason.Contains("private", StringComparison.OrdinalIgnoreCase))
            {
                return LoginRequiredReason.Private;
            }

            if (reason.Contains("members", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("member", StringComparison.OrdinalIgnoreCase))
            {
                return LoginRequiredReason.MembersOnly;
            }

            return LoginRequiredReason.Unknown;
        }
    }

    /// <inheritdoc/>
    public string? Category =>
        content
            .GetPropertyOrNull("microformat")
            ?.GetPropertyOrNull("playerMicroformatRenderer")
            ?.GetPropertyOrNull("category")
            ?.GetStringOrNull();

    /// <inheritdoc/>
    public bool IsMusic =>
        string.Equals(Category, "Music", StringComparison.OrdinalIgnoreCase) ||
        content.GetPropertyOrNull("videoDetails")?.GetPropertyOrNull("musicVideoType") != null;

    /// <inheritdoc/>
    public bool IsAvailable =>
        !string.Equals(PlayabilityStatus, "error", StringComparison.OrdinalIgnoreCase)
        && Details is not null;

    /// <inheritdoc/>
    public bool IsPlayable =>
        string.Equals(PlayabilityStatus, "ok", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    private JsonElement? Details => content.GetPropertyOrNull("videoDetails");

    /// <inheritdoc/>
    public string? Title => Details?.GetPropertyOrNull("title")?.GetStringOrNull();

    /// <inheritdoc/>
    public string? ChannelId => Details?.GetPropertyOrNull("channelId")?.GetStringOrNull();

    /// <inheritdoc/>
    public string? Author => Details?.GetPropertyOrNull("author")?.GetStringOrNull();

    /// <inheritdoc/>
    public DateTimeOffset? UploadDate =>
        content
            .GetPropertyOrNull("microformat")
            ?.GetPropertyOrNull("playerMicroformatRenderer")
            ?.GetPropertyOrNull("uploadDate")
            ?.GetDateTimeOffset();

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public IReadOnlyList<ThumbnailData> Thumbnails =>
        Details
            ?.GetPropertyOrNull("thumbnail")
            ?.GetPropertyOrNull("thumbnails")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => new ThumbnailData(j))
            .ToArray() ?? [];

    /// <inheritdoc/>
    public IReadOnlyList<string> Keywords =>
        Details
            ?.GetPropertyOrNull("keywords")
            ?.EnumerateArrayOrNull()
            ?.Select(static j => j.GetStringOrNull())
            .WhereNotNull()
            .ToArray() ?? [];

    /// <inheritdoc/>
    public string? Description => Details?.GetPropertyOrNull("shortDescription")?.GetStringOrNull();

    /// <inheritdoc/>
    public long? ViewCount =>
        Details
            ?.GetPropertyOrNull("viewCount")
            ?.GetStringOrNull()
            ?.Pipe(static s =>
                long.TryParse(s, CultureInfo.InvariantCulture, out var result)
                    ? result
                    : (long?)null
            );

    /// <inheritdoc/>
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
    // public IEnumerable<IStreamData> Streams
    // {
    //     get
    //     {
    //         var serverAbrUrl = ServerAbrStreamingUrl;

    //         var formats = StreamingData?.GetPropertyOrNull("formats");
    //         if (formats != null)
    //         {
    //             foreach (var j in formats.Value.EnumerateArrayOrEmpty())
    //                 yield return new StreamData(j, serverAbrUrl);
    //         }

    //         var adaptiveFormats = StreamingData?.GetPropertyOrNull("adaptiveFormats");
    //         if (adaptiveFormats != null)
    //         {
    //             foreach (var j in adaptiveFormats.Value.EnumerateArrayOrEmpty())
    //                 yield return new StreamData(j, serverAbrUrl);
    //         }
    //     }
    // }

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

    /// <inheritdoc/>
    /// <summary>
    /// Извлекает значение целевой громкости (loudnessDb) из playerConfig.audioConfig плеера,
    /// либо, в случае его отсутствия, из метаданных первого доступного медиапотока.
    /// </summary>
    /// <remarks>
    /// Использует zero-alloc поиск по UTF-8 байтам для предотвращения аллокаций строк в куче (SOH).
    /// </remarks>
    public float LoudnessDb
    {
        get
        {
            var audioConfig = content
                .GetPropertyOrNull("playerConfig"u8)
                ?.GetPropertyOrNull("audioConfig"u8);

            if (audioConfig.HasValue)
            {
                var val = audioConfig.Value.GetPropertyOrNull("loudnessDb"u8)?.GetDoubleOrNull();
                if (val.HasValue) return (float)val.Value;
            }

            // Fallback-поиск в массиве стримов, если основной конфиг пуст
            foreach (var stream in Streams)
            {
                if (!float.IsNaN(stream.LoudnessDb))
                    return stream.LoudnessDb;
            }

            return float.NaN;
        }
    }

    [GeneratedRegex(@"video_id=(.{11})")]
    private static partial Regex MyRegex();
}

internal partial class PlayerResponse
{
    public class ClosedCaptionTrackData(JsonElement content)
    {
        /// <inheritdoc/>
        public string? Url => content.GetPropertyOrNull("baseUrl")?.GetStringOrNull();

        /// <inheritdoc/>
        public string? LanguageCode => content.GetPropertyOrNull("languageCode")?.GetStringOrNull();

        /// <inheritdoc/>
        public string? LanguageName =>
            content.GetPropertyOrNull("name")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? content
                .GetPropertyOrNull("name")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.Select(j => j.GetPropertyOrNull("text")?.GetStringOrNull())
                .WhereNotNull()
                .Pipe(string.Concat);

        /// <inheritdoc/>
        public bool IsAutoGenerated =>
            content
                .GetPropertyOrNull("vssId")
                ?.GetStringOrNull()
                ?.StartsWith("a.", StringComparison.OrdinalIgnoreCase) ?? false;
    }
}

internal partial class PlayerResponse
{
    /// <summary>
    /// Представляет данные одного медиапотока из InnerTube PlayerResponse.
    /// Поддерживает как прямые URL (ANDROID_VR, авторизованный WEB_REMIX),
    /// так и зашифрованные через <c>signatureCipher</c> (WEB_REMIX без авторизации, WEB).
    /// </summary>
    public class StreamData : IStreamData
    {
        private readonly JsonElement _content;

        /// <summary>
        /// Кэш разобранных параметров <c>signatureCipher</c>/<c>cipher</c>.
        /// <c>null</c> означает либо «не разбирали», либо «поле отсутствует» —
        /// разграничение через <see cref="_cipherDataResolved"/>.
        /// </summary>
        private IReadOnlyDictionary<string, string>? _cipherData;

        /// <summary>
        /// Флаг однократной инициализации <see cref="_cipherData"/>.
        /// Без него при отсутствии cipher-полей геттер повторно сканировал бы JSON
        /// на каждое обращение к <see cref="Url"/>, <see cref="Signature"/>,
        /// <see cref="SignatureParameter"/>.
        /// </summary>
        private bool _cipherDataResolved;

        /// <summary>Создаёт экземпляр данных потока из JSON-элемента PlayerResponse.</summary>
        /// <param name="content">JSON-элемент одного формата из <c>formats</c> / <c>adaptiveFormats</c>.</param>
        public StreamData(JsonElement content)
        {
            _content = content;
        }

        /// <inheritdoc/>
        public int? Itag => _content.GetPropertyOrNull("itag")?.GetInt32OrNull();

        /// <summary>
        /// Разобранные параметры из <c>cipher</c> или <c>signatureCipher</c>.
        /// Инициализируется один раз через <see cref="_cipherDataResolved"/>.
        /// </summary>
        /// <remarks>
        /// YouTube возвращает <c>signatureCipher</c> вместо прямого <c>url</c> когда:
        /// <list type="bullet">
        ///   <item>Клиент не авторизован (WEB_REMIX без кук)</item>
        ///   <item>Age-restricted контент через определённые клиенты</item>
        ///   <item>Запрос без корректного <c>signatureTimestamp</c></item>
        /// </list>
        /// Формат строки: <c>s={encrypted_sig}&amp;sp=sig&amp;url={encoded_url}</c>
        /// </remarks>
        private IReadOnlyDictionary<string, string>? CipherData
        {
            get
            {
                if (_cipherDataResolved) return _cipherData;

                _cipherDataResolved = true;

                var cipherStr =
                    _content.GetPropertyOrNull("cipher")?.GetStringOrNull()
                    ?? _content.GetPropertyOrNull("signatureCipher")?.GetStringOrNull();

                if (cipherStr is null) return null;

                _cipherData = ParseCipherString(cipherStr);

                Log.Debug($"[StreamData] itag={Itag}: signatureCipher detected, " +
                          $"sig_len={_cipherData.GetValueOrDefault("s")?.Length ?? 0}, " +
                          $"sp={_cipherData.GetValueOrDefault("sp") ?? "null"}");

                return _cipherData;
            }
        }

        /// <summary>
        /// Парсит строку <c>signatureCipher</c>/<c>cipher</c> в словарь параметров.
        /// </summary>
        /// <remarks>
        /// <para>Использует <see cref="ReadOnlySpan{T}"/> для zero-alloc разбора без промежуточных
        /// строк, защищая SOH/LOH от лишних аллокаций.</para>
        /// <para>Корректно обрабатывает <c>=</c> внутри значений (base64 padding <c>==</c>
        /// в подписи): разделяет только по первому вхождению <c>=</c>.</para>
        /// <para>Использует <see cref="Uri.UnescapeDataString"/> вместо
        /// <see cref="System.Net.WebUtility.UrlDecode"/>: последний заменяет <c>+</c> на пробел,
        /// что ломает base64-подписи содержащие символ <c>+</c>.</para>
        /// </remarks>
        /// <param name="cipher">URL-encoded строка вида <c>key1=val1&amp;key2=val2</c>.</param>
        /// <returns>Словарь декодированных пар ключ/значение.</returns>
        private static IReadOnlyDictionary<string, string> ParseCipherString(string cipher)
        {
            if (string.IsNullOrEmpty(cipher))
                return new Dictionary<string, string>(0);

            var result = new Dictionary<string, string>(3, StringComparer.Ordinal);
            var span = cipher.AsSpan();
            int start = 0;

            while (start < span.Length)
            {
                int ampIdx = span[start..].IndexOf('&');
                var pair = ampIdx < 0 ? span[start..] : span.Slice(start, ampIdx);

                int eqIdx = pair.IndexOf('=');
                if (eqIdx >= 0)
                {
                    var key = pair[..eqIdx].ToString();
                    var valEncoded = pair[(eqIdx + 1)..].ToString();
                    result[key] = Uri.UnescapeDataString(valEncoded);
                }

                if (ampIdx < 0) break;
                start += ampIdx + 1;
            }

            return result;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Приоритет: прямой <c>"url"</c> из JSON (ANDROID_VR, авторизованный WEB_REMIX),
        /// затем <c>url=...</c> из <c>signatureCipher</c> (неавторизованные веб-клиенты).
        /// </remarks>
        public string? Url =>
            _content.GetPropertyOrNull("url")?.GetStringOrNull()
            ?? CipherData?.GetValueOrDefault("url");

        /// <inheritdoc/>
        public string? Signature => CipherData?.GetValueOrDefault("s");

        /// <inheritdoc/>
        public string? SignatureParameter => CipherData?.GetValueOrDefault("sp");

        // Остальные свойства без изменений ↓

        /// <inheritdoc/>
        public long? ContentLength =>
            _content
                .GetPropertyOrNull("contentLength")
                ?.GetStringOrNull()
                ?.Pipe(s => long.TryParse(s, CultureInfo.InvariantCulture, out var r) ? r : (long?)null)
            ?? Url
                ?.Pipe(s => UrlEx.TryGetQueryParameterValue(s, "clen"))
                ?.NullIfWhiteSpace()
                ?.Pipe(s => long.TryParse(s, CultureInfo.InvariantCulture, out var r) ? r : (long?)null);

        /// <inheritdoc/>
        public long? Bitrate => _content.GetPropertyOrNull("bitrate")?.GetInt64OrNull();

        /// <inheritdoc/>
        public string? MimeType => _content.GetPropertyOrNull("mimeType")?.GetStringOrNull();

        private bool IsAudioOnly =>
            MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ?? false;

        /// <inheritdoc/>
        public string? Container => MimeType?.SubstringUntil(";").SubstringAfter("/");

        /// <summary>Все кодеки из MIME-типа.</summary>
        public string? Codecs => MimeType?.SubstringAfter("codecs=\"").SubstringUntil("\"");

        /// <inheritdoc/>
        public string? AudioCodec =>
            IsAudioOnly ? Codecs : Codecs?.SubstringAfter(", ").NullIfWhiteSpace();

        /// <inheritdoc/>
        public string? AudioLanguageCode =>
            _content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("id")
                ?.GetStringOrNull()
                ?.SubstringUntil(".");

        /// <inheritdoc/>
        public string? AudioLanguageName =>
            _content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("displayName")
                ?.GetStringOrNull();

        /// <inheritdoc/>
        public bool? IsAudioLanguageDefault =>
            _content
                .GetPropertyOrNull("audioTrack")
                ?.GetPropertyOrNull("audioIsDefault")
                ?.GetBooleanOrNull();

        /// <inheritdoc/>
        public float LoudnessDb =>
            _content
                .GetPropertyOrNull("loudnessDb")
                ?.GetDoubleOrNull()
                ?.Pipe(static d => (float)d) ?? float.NaN;

        /// <inheritdoc/>
        public string? VideoCodec
        {
            get
            {
                var codec = IsAudioOnly ? null : Codecs?.SubstringUntil(", ").NullIfWhiteSpace();
                return string.Equals(codec, "unknown", StringComparison.OrdinalIgnoreCase)
                    ? "av01.0.05M.08"
                    : codec;
            }
        }

        /// <inheritdoc/>
        public string? VideoQualityLabel =>
            _content.GetPropertyOrNull("qualityLabel")?.GetStringOrNull();

        /// <inheritdoc/>
        public int? VideoWidth => _content.GetPropertyOrNull("width")?.GetInt32OrNull();

        /// <inheritdoc/>
        public int? VideoHeight => _content.GetPropertyOrNull("height")?.GetInt32OrNull();

        /// <inheritdoc/>
        public int? VideoFramerate => _content.GetPropertyOrNull("fps")?.GetInt32OrNull();
    }
}

internal partial class PlayerResponse
{
    /// <inheritdoc/>
    public static PlayerResponse Parse(string raw) => new(Json.Parse(raw));
}