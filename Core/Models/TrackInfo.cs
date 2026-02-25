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
    private static readonly ConditionalWeakTable<string, string> _idCache = new();

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
        if (Id.StartsWith("yt_pl_")) return Id.Substring(6);
        if (Id.StartsWith("yt_")) return Id.Substring(3);
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

    #region Constructors

    public TrackInfo() { }

    #endregion

    #region Methods

    /// <summary>
    /// Обновляет метаданные из свежего объекта.
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