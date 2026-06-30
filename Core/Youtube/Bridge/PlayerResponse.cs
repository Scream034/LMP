using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Bridge;

internal partial class PlayerResponse(JsonElement content)
{
    private JsonElement? _details;
    private bool _detailsCached;
    private JsonElement? _playability;
    private bool _playabilityCached;

    private JsonElement? Playability
    {
        get
        {
            if (!_playabilityCached)
            {
                _playability = content.GetPropertyOrNull("playabilityStatus");
                _playabilityCached = true;
            }
            return _playability;
        }
    }

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

            // 1. Bot detection — ОБЯЗАТЕЛЬНО первым
            // YouTube ANDROID_VR возвращает "Sign in to confirm you're not a bot"
            // со статусом LOGIN_REQUIRED. Слово "confirm" ранее ложно
            // срабатывало на ветку AgeRestricted.
            if (reason.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("не робот", StringComparison.OrdinalIgnoreCase))
            {
                return LoginRequiredReason.BotDetection;
            }

            // 2. Age gate
            var desktopAgeGateReason = Playability
                ?.GetPropertyOrNull("desktopLegacyAgeGateReason")
                ?.GetInt32OrNull();

            if (desktopAgeGateReason == 1 ||
                reason.Contains("age", StringComparison.OrdinalIgnoreCase))
            {
                return LoginRequiredReason.AgeRestricted;
            }

            // 3. Private
            if (reason.Contains("private", StringComparison.OrdinalIgnoreCase))
            {
                return LoginRequiredReason.Private;
            }

            // 4. Members only
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
    private JsonElement? Details
    {
        get
        {
            if (!_detailsCached)
            {
                _details = content.GetPropertyOrNull("videoDetails");
                _detailsCached = true;
            }
            return _details;
        }
    }

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
    public TimeSpan? Duration
    {
        get
        {
            var s = Details?.GetPropertyOrNull("lengthSeconds")?.GetStringOrNull();
            if (s is null) return null;
            return double.TryParse(s, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ThumbnailData> Thumbnails
    {
        get
        {
            var thumbsArray = Details
                ?.GetPropertyOrNull("thumbnail")
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

    /// <inheritdoc/>
    public IReadOnlyList<string> Keywords
    {
        get
        {
            var keywordsArray = Details?.GetPropertyOrNull("keywords");
            if (keywordsArray is null || keywordsArray.Value.ValueKind != JsonValueKind.Array)
                return [];

            var array = keywordsArray.Value;
            int len = array.GetArrayLength();
            if (len == 0) return [];

            var result = new List<string>(len);
            for (int i = 0; i < len; i++)
            {
                var s = array[i].GetStringOrNull();
                if (s is not null) result.Add(s);
            }

            return result.Count > 0 ? result : [];
        }
    }

    /// <inheritdoc/>
    public string? Description => Details?.GetPropertyOrNull("shortDescription")?.GetStringOrNull();

    /// <inheritdoc/>
    public long? ViewCount
    {
        get
        {
            var s = Details?.GetPropertyOrNull("viewCount")?.GetStringOrNull();
            if (s is null) return null;
            return long.TryParse(s, CultureInfo.InvariantCulture, out var result) ? result : null;
        }
    }

    /// <inheritdoc/>
    public string? PreviewVideoId =>
        Playability
            ?.GetPropertyOrNull("errorScreen")
            ?.GetPropertyOrNull("playerLegacyDesktopYpcTrailerRenderer")
            ?.GetPropertyOrNull("trailerVideoId")
            ?.GetStringOrNull()
        ?? (Playability
            ?.GetPropertyOrNull("errorScreen")
            ?.GetPropertyOrNull("ypcTrailerRenderer")
            ?.GetPropertyOrNull("playerVars")
            ?.GetStringOrNull() is { } playerVars
                ? UrlEx.GetQueryParameters(playerVars).GetValueOrDefault("video_id")
                : null)
        ?? (Playability
            ?.GetPropertyOrNull("errorScreen")
            ?.GetPropertyOrNull("ypcTrailerRenderer")
            ?.GetPropertyOrNull("playerResponse")
            ?.GetStringOrNull() is { } encoded
                ? DecodePreviewVideoId(encoded)
                : null);

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

    /// <summary>
    /// Integrated loudness трека в LUFS из <c>playerConfig.audioConfig.perceptualLoudnessDb</c>.
    /// <c>float.NaN</c> если поле отсутствует.
    /// </summary>
    public float PerceptualLoudnessDb
    {
        get
        {
            var audioConfig = content
                .GetPropertyOrNull("playerConfig"u8)
                ?.GetPropertyOrNull("audioConfig"u8);

            if (audioConfig.HasValue)
            {
                var val = audioConfig.Value
                    .GetPropertyOrNull("perceptualLoudnessDb"u8)
                    ?.GetDoubleOrNull();

                if (val.HasValue && double.IsFinite(val.Value))
                    return (float)val.Value;
            }

            return float.NaN;
        }
    }

    /// <summary>
    /// Декодирует YouTube-специфичную base64-строку PlayerResponse и извлекает video_id.
    /// YouTube использует URL-safe base64 (- вместо +, _ вместо /).
    /// </summary>
    private static string? DecodePreviewVideoId(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
        var decoded = Encoding.UTF8.GetString(bytes);
        return MyRegex().Match(decoded).Groups[1].Value.NullIfWhiteSpace();
    }

    [GeneratedRegex(@"video_id=(.{11})")]
    private static partial Regex MyRegex();
}

internal partial class PlayerResponse
{
    public sealed class ClosedCaptionTrackData(JsonElement content)
    {
        /// <inheritdoc/>
        public string? Url => content.GetPropertyOrNull("baseUrl")?.GetStringOrNull();

        /// <inheritdoc/>
        public string? LanguageCode => content.GetPropertyOrNull("languageCode")?.GetStringOrNull();

        /// <inheritdoc/>
        public string? LanguageName
        {
            get
            {
                var name = content.GetPropertyOrNull("name");
                if (name is null) return null;

                return name.Value.GetPropertyOrNull("simpleText")?.GetStringOrNull()
                    ?? YoutubeParsingHelpers.ConcatTextRuns(name.Value.GetPropertyOrNull("runs"));
            }
        }

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
    public sealed class StreamData : IStreamData
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