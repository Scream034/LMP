using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Models;

/// <summary>
/// Представляет музыкальный трек.
/// string interning для ID, минимизированы аллокации.
/// </summary>
public sealed class TrackInfo : ReactiveObject, IBatchItem, ISearchResult
{
    private static readonly ConditionalWeakTable<string, string> _idCache = [];

    #region Identity

    /// <summary>
    /// Уникальный идентификатор с кэшированием для избежания дубликатов строк.
    /// </summary>
    public string Id
    {
        get;
        set
        {
            if (field == value) return;

            if (!string.IsNullOrEmpty(value))
            {
                if (value.StartsWith("yt_") || value.StartsWith("yt_pl_"))
                {
                    field = value;
                }
                else
                {
                    field = GetCachedPrefixedId("yt_", value);
                }
            }
            else
            {
                field = value ?? string.Empty;
            }

            this.RaisePropertyChanged();
        }
    } = string.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetCachedPrefixedId(string prefix, string rawId)
    {
        if (!_idCache.TryGetValue(rawId, out var cached))
        {
            cached = string.Concat(prefix, rawId);
            _idCache.AddOrUpdate(rawId, cached);
        }
        return cached;
    }

    /// <summary>
    /// Извлекает чистый YouTube ID без префикса (zero-alloc через Span).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetRawIdSpan()
    {
        var span = Id.AsSpan();
        if (span.StartsWith("yt_pl_".AsSpan()))
            return span[6..];
        if (span.StartsWith("yt_".AsSpan()))
            return span[3..];
        return span;
    }

    /// <summary>
    /// Получает чистый ID как строку (для async контекста).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetRawId()
    {
        if (Id.StartsWith("yt_pl_")) return Id[6..];
        if (Id.StartsWith("yt_")) return Id[3..];
        return Id;
    }

    #endregion

    #region Metadata

    /// <summary>
    /// Название трека.
    /// </summary>
    [Reactive] public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Исполнитель/автор.
    /// </summary>
    [Reactive] public string Author { get; set; } = string.Empty;

    /// <summary>
    /// ID канала YouTube.
    /// </summary>
    [Reactive] public string? ChannelId { get; set; }

    /// <summary>
    /// URL трека на YouTube.
    /// </summary>
    [Reactive] public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Длительность трека.
    /// </summary>
    [Reactive] public TimeSpan Duration { get; set; }

    /// <summary>
    /// URL обложки.
    /// </summary>
    [Reactive] public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Флаг официального канала исполнителя.
    /// </summary>
    public bool IsOfficialArtist { get; set; }

    /// <summary>
    /// Флаг музыкального контента.
    /// </summary>
    public bool IsMusic { get; set; }

    [JsonIgnore]
    public bool IsExplicitVideoClip => IsOfficialArtist && !IsMusic;

    [JsonIgnore]
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    #endregion

    #region User State

    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDisliked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public bool IsCached { get; set; }

    [JsonIgnore]
    public bool IsAvailableOffline => IsDownloaded || IsCached;

    [Reactive] public string? LocalPath { get; set; }

    #endregion

    #region Playlists


    public HashSet<string> InPlaylists
    {
        get => field ??= new HashSet<string>(StringComparer.Ordinal);
        set;
    }

    #endregion

    #region Format Preferences

    [Reactive] public string? PreferredContainer { get; set; }
    [Reactive] public int PreferredBitrate { get; set; }

    public string? RadioSeedId { get; set; }

    #endregion

    #region Runtime Cache

    [JsonIgnore] public string? TransientContainer { get; set; }
    [JsonIgnore] public int TransientBitrate { get; set; }
    [JsonIgnore] public long TransientSize { get; set; }

    [JsonIgnore, Reactive] public string StreamUrl { get; set; } = string.Empty;

    [JsonIgnore] public string CachedCodec { get; set; } = string.Empty;
    [JsonIgnore] public int CachedBitrate { get; set; }
    [JsonIgnore] public string CachedContainer { get; set; } = string.Empty;

    /// <summary>
    /// Трек доступен только через HLS (обычные стримы заблокированы).
    /// </summary>
    [JsonIgnore] public bool IsHlsOnly { get; set; }

    /// <summary>
    /// URL HLS манифеста (если IsHlsOnly = true).
    /// </summary>
    [JsonIgnore] public string? HlsManifestUrl { get; set; }

    #endregion

    #region Audio Normalization

    /// <summary>
    /// Сырое значение <c>loudnessDb</c> из YouTube InnerTube API.
    ///
    /// <para><b>Семантика:</b> Разница между интегральной громкостью трека и целевым
    /// уровнем YouTube (-14 LUFS). Положительное значение = трек ГРОМЧЕ цели
    /// (нужна аттенуация); отрицательное = трек ТИШЕ (YouTube не бустит).</para>
    ///
    /// <para><b>Примеры:</b></para>
    /// <list type="bullet">
    ///   <item>loudnessDb=+6.8 → трек на -7.2 LUFS, нужна аттенуация 6.8 dB</item>
    ///   <item>loudnessDb=-2.0 → трек на -16 LUFS, тихий, YouTube не трогает</item>
    ///   <item>loudnessDb=0 → трек ровно на -14 LUFS или YouTube не указал поправку</item>
    /// </list>
    ///
    /// <para><b>Ограничения использования:</b> Значение валидно исключительно для
    /// режима <see cref="NormalizationMode.DownwardOnly"/> при target ≈ -14 LUFS.
    /// Для Bidirectional или других targetLufs требуется EBU R128 анализ.</para>
    ///
    /// <para><c>float.NaN</c> = значение отсутствует в API ответе.</para>
    /// </summary>
    [JsonIgnore]
    public float YoutubeIntegratedLoudnessDb { get; private set; } = float.NaN;

    /// <summary>
    /// <c>true</c> если YouTube передал значение <see cref="YoutubeIntegratedLoudnessDb"/>.
    /// </summary>
    [JsonIgnore]
    public bool HasYoutubeLoudnessDb =>
        !float.IsNaN(YoutubeIntegratedLoudnessDb) && float.IsFinite(YoutubeIntegratedLoudnessDb);

    /// <summary>
    /// Закэшированный linear gain нормализации, вычисленный EBU R128 анализом.
    /// Единственный источник истины для Bidirectional режима и нестандартных targetLufs.
    ///
    /// <para><b>Откуда берётся:</b></para>
    /// <list type="bullet">
    ///   <item>EBU R128 pre-scan — через <see cref="SetGain"/></item>
    ///   <item>EBU R128 real-time анализ (~3 сек) — через <see cref="SetGain"/></item>
    /// </list>
    ///
    /// <para><c>float.NaN</c> = не вычислен → требуется EBU R128 анализ.</para>
    ///
    /// <para><b>Намеренное отсутствие YouTube gain здесь:</b> YouTube gain является
    /// DownwardOnly аттенуацией при фиксированном target -14 LUFS и хранится
    /// отдельно в <see cref="YoutubeIntegratedLoudnessDb"/>. Смешивание двух
    /// семантически разных величин в одном поле вызывало некорректное
    /// применение gain в Bidirectional режиме.</para>
    /// </summary>
    [JsonIgnore]
    public float CachedNormalizationGain { get; set; } = float.NaN;

    /// <summary>
    /// <c>true</c> если EBU R128 gain вычислен и готов к применению.
    /// </summary>
    [JsonIgnore]
    public bool HasCachedNormalizationGain =>
        !float.IsNaN(CachedNormalizationGain) && float.IsFinite(CachedNormalizationGain);

    /// <summary>
    /// Устанавливает EBU R128 gain. Перезаписывает если значение значимо изменилось.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetGain(float gain)
    {
        if (!float.IsFinite(gain) || gain <= 0f) return false;

        // Перезапись только при значимом изменении (> 0.1% разницы).
        // Предотвращает лишние persist при RepeatMode.One.
        if (HasCachedNormalizationGain && MathF.Abs(CachedNormalizationGain - gain) < 0.001f)
            return false;

        CachedNormalizationGain = gain;
        return true;
    }

    /// <summary>
    /// Кэширует сырое значение <c>loudnessDb</c> из YouTube InnerTube API.
    /// </summary>
    /// <remarks>
    /// <para><b>Не записывает в <see cref="CachedNormalizationGain"/>.</b>
    /// YouTube данные имеют DownwardOnly семантику и несовместимы с Bidirectional
    /// режимом или нестандартными targetLufs. Конвертация и применение
    /// выполняются через <see cref="NormalizationGainResolver.Resolve"/>
    /// с учётом текущей <see cref="NormalizationConfig"/>.</para>
    /// </remarks>
    /// <param name="loudnessDb">Значение поля loudnessDb из InnerTube adaptiveFormats.</param>
    /// <returns><c>true</c> если значение валидно и сохранено.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySetGainFromLoudness(float loudnessDb)
    {
        if (float.IsNaN(loudnessDb) || !float.IsFinite(loudnessDb)) return false;
        YoutubeIntegratedLoudnessDb = loudnessDb;
        return true;
    }

    #endregion

    #region Constructors

    public TrackInfo() { }

    #endregion

    #region Methods

    /// <summary>
    /// Обновляет метаданные из свежего объекта.
    /// Игнорирует пустые значения и предотвращает затирание статуса лайка
    /// ответами API, не содержащими пользовательского контекста.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMetadata(TrackInfo fresh)
    {
        if (!string.IsNullOrEmpty(fresh.Title) && fresh.Title != Title)
            Title = fresh.Title;

        if (!string.IsNullOrEmpty(fresh.Author) && fresh.Author != Author)
            Author = fresh.Author;

        if (!string.IsNullOrEmpty(fresh.Url) && fresh.Url != Url)
            Url = fresh.Url;

        if (!string.IsNullOrEmpty(fresh.ThumbnailUrl) && fresh.ThumbnailUrl != ThumbnailUrl)
            ThumbnailUrl = fresh.ThumbnailUrl;

        if (fresh.Duration.TotalSeconds > 0 && fresh.Duration != Duration)
            Duration = fresh.Duration;

        if (fresh.IsOfficialArtist && !IsOfficialArtist)
            IsOfficialArtist = true;

        if (fresh.IsMusic && !IsMusic)
            IsMusic = true;

        if (!string.IsNullOrEmpty(fresh.ChannelId) && fresh.ChannelId != ChannelId)
            ChannelId = fresh.ChannelId;

        // Повышаем статус лайка. API плейлистов/поиска возвращает IsLiked = false,
        // поэтому мы не должны перезаписывать локальный true на false.
        if (fresh.IsLiked && !IsLiked)
            IsLiked = true;

        if (fresh.IsDisliked && !IsDisliked)
            IsDisliked = true;

        if (fresh.HasCachedNormalizationGain && !HasCachedNormalizationGain)
            CachedNormalizationGain = fresh.CachedNormalizationGain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkAsCached(string? container = null, int bitrate = 0)
    {
        IsCached = true;
        if (!string.IsNullOrEmpty(container)) PreferredContainer = container;
        if (bitrate > 0) PreferredBitrate = bitrate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkAsDownloaded(string localPath, string? container = null, int bitrate = 0)
    {
        IsDownloaded = true;
        IsCached = true;
        LocalPath = localPath;
        if (!string.IsNullOrEmpty(container)) PreferredContainer = container;
        if (bitrate > 0) PreferredBitrate = bitrate;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearCacheStatus()
    {
        if (!IsDownloaded)
        {
            IsCached = false;
        }
    }

    #endregion
}