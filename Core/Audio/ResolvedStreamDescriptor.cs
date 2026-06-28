using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Единый immutable дескриптор resolved аудио потока.
/// Передаётся от resolve-слоя до audio source без потерь метаданных.
/// </summary>
/// <remarks>
/// <para><b>Single Source of Truth</b> для всех метаданных потока:
/// format, codec, bitrate, contentLength, loudnessDb, language, expire.</para>
/// <para>Заменяет цепочку <c>string url + string? trackId + int bitrateHint</c>,
/// которая уничтожала структурированные данные между слоями.</para>
/// <para><b>Единицы измерения (зафиксированы):</b></para>
/// <list type="bullet">
///   <item>Bitrate — kbps (int)</item>
///   <item>ContentLength — bytes (long)</item>
///   <item>LoudnessDb — dB (float, <see cref="float.NaN"/> = отсутствует)</item>
/// </list>
/// </remarks>
public readonly record struct ResolvedStreamDescriptor()
{
    /// <summary>Идентификатор трека (с префиксом <c>yt_</c>).</summary>
    public required string TrackId { get; init; }

    /// <summary>YouTube itag потока. 0 = неизвестен (cache-only path).</summary>
    public int Itag { get; init; }

    /// <summary>Формат контейнера (WebM, Mp4, Ogg, Hls).</summary>
    public required AudioFormat Format { get; init; }

    /// <summary>Аудио кодек (Opus, Aac).</summary>
    public required AudioCodec Codec { get; init; }

    /// <summary>Битрейт в kbps.</summary>
    public required int BitrateKbps { get; init; }

    /// <summary>Полный размер контента в байтах. 0 = неизвестен.</summary>
    public long ContentLengthBytes { get; init; }

    /// <summary>
    /// Полный videoplayback URL. Пустая строка для cache-only воспроизведения.
    /// </summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// Время истечения URL (UTC). <see cref="DateTime.MaxValue"/> если expire отсутствует.
    /// </summary>
    public DateTime ExpireUtc { get; init; } = DateTime.MaxValue;

    /// <summary>CDN hostname (например, <c>rr1---sn-xxx.googlevideo.com</c>). Пустая строка если неизвестен.</summary>
    public string CdnHost { get; init; } = "";

    /// <summary>
    /// Сырое значение <c>loudnessDb</c> из YouTube InnerTube API.
    /// <see cref="float.NaN"/> = значение отсутствует.
    /// </summary>
    /// <remarks>
    /// <para>Положительное значение = трек громче цели YouTube (-14 LUFS), нужна аттенуация.</para>
    /// <para>Отрицательное = трек тише цели.</para>
    /// </remarks>
    public float LoudnessDb { get; init; } = float.NaN;

    /// <summary>Код языка аудиодорожки (например, <c>en</c>). <c>null</c> = не указан.</summary>
    public string? LanguageCode { get; init; }

    /// <summary>Является ли аудиодорожка языком по умолчанию.</summary>
    public bool IsDefaultLanguage { get; init; }

    /// <summary>Источник, из которого был получен дескриптор.</summary>
    public required StreamSource Origin { get; init; }

    /// <summary><c>true</c> если <see cref="Url"/> непустой и пригоден для HTTP-запросов.</summary>
    public bool HasLiveUrl => !string.IsNullOrEmpty(Url);

    /// <summary><c>true</c> если URL протух (с учётом 30-минутного safety margin).</summary>
    public bool IsExpired => ExpireUtc != DateTime.MaxValue
        && DateTime.UtcNow >= ExpireUtc.AddMinutes(-30);

    /// <summary><c>true</c> если значение loudness присутствует и конечно.</summary>
    public bool HasLoudness => !float.IsNaN(LoudnessDb) && float.IsFinite(LoudnessDb);

    /// <summary><c>true</c> если дескриптор содержит информацию об истечении URL.</summary>
    public bool HasExpiry => ExpireUtc != DateTime.MaxValue;

    /// <summary>
    /// Возвращает текстовое представление дескриптора для быстрой диагностики.
    /// </summary>
    /// <returns>Строка с ключевыми параметрами потока.</returns>
    public override string ToString()
    {
        return $"track={TrackId}, origin={Origin}, format={Format}, codec={Codec}, " +
               $"bitrate={BitrateKbps}kbps, itag={Itag}, size={ContentLengthBytes}, " +
               $"liveUrl={HasLiveUrl}, host={CdnHost}, expiry={(HasExpiry ? ExpireUtc.ToString("O") : "none")}, " +
               $"loudness={(HasLoudness ? LoudnessDb.ToString("F2") : "NaN")}, lang={LanguageCode ?? "-"}, defaultLang={IsDefaultLanguage}";
    }
}

/// <summary>
/// Источник, из которого был получен <see cref="ResolvedStreamDescriptor"/>.
/// </summary>
public enum StreamSource
{
    /// <summary>Свежий ответ YouTube InnerTube API.</summary>
    YouTubeApi = 0,

    /// <summary>Персистентный session cache (probe-validated URL).</summary>
    SessionCache = 1,

    /// <summary>Полностью закэшированный трек на диске.</summary>
    DiskCacheFull = 2,

    /// <summary>Частичный дисковый кэш (partial cache fast start).</summary>
    DiskCachePartial = 3,

    /// <summary>In-memory кэш YoutubeProvider.</summary>
    ProviderMemoryCache = 4
}