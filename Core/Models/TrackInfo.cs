using System.Text.Json.Serialization;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Models;

/// <summary>
/// Представляет музыкальный трек.
/// Является единственным источником правды для состояния трека.
/// Все свойства реактивные — UI обновляется автоматически.
/// </summary>
public sealed class TrackInfo : ReactiveObject, IBatchItem, ISearchResult
{
    #region Identity

    /// <summary>
    /// Уникальный идентификатор (yt_ID для YouTube или local_ID для локальных).
    /// </summary>
    public string Id { get; set; } = string.Empty;

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
    /// ID канала YouTube (для связи с исполнителем).
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
    /// Флаг музыкального контента (не видеоклип).
    /// </summary>
    public bool IsMusic { get; set; }

    /// <summary>
    /// Является ли трек явным видеоклипом.
    /// </summary>
    [JsonIgnore]
    public bool IsExplicitVideoClip => IsOfficialArtist && !IsMusic;

    /// <summary>
    /// Есть ли обложка.
    /// </summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    #endregion

    #region User State

    /// <summary>
    /// Трек добавлен в "Любимое".
    /// </summary>
    [Reactive] public bool IsLiked { get; set; }

    /// <summary>
    /// Трек помечен как "Не нравится".
    /// </summary>
    [Reactive] public bool IsDisliked { get; set; }

    /// <summary>
    /// Трек сохранён в папку Downloads (явно скачан пользователем).
    /// Файл НЕ удаляется при очистке кэша.
    /// </summary>
    [Reactive] public bool IsDownloaded { get; set; }

    /// <summary>
    /// Трек полностью закэширован (доступен офлайн через StreamCache).
    /// Файл МОЖЕТ быть удалён при очистке кэша.
    /// </summary>
    [Reactive] public bool IsCached { get; set; }

    /// <summary>
    /// Трек доступен для офлайн-воспроизведения.
    /// True если скачан ИЛИ закэширован.
    /// </summary>
    [JsonIgnore]
    public bool IsAvailableOffline => IsDownloaded || IsCached;

    /// <summary>
    /// Путь к локальному файлу (для скачанных треков).
    /// </summary>
    [Reactive] public string? LocalPath { get; set; }

    #endregion

    #region Playlists

    /// <summary>
    /// ID плейлистов, в которых находится трек.
    /// </summary>
    public HashSet<string> InPlaylists { get; set; } = [];

    #endregion

    #region Format Preferences

    /// <summary>
    /// Предпочтительный контейнер (webm, mp4, m4a).
    /// Сохраняется в БД.
    /// </summary>
    [Reactive] public string? PreferredContainer { get; set; }

    /// <summary>
    /// Предпочтительный битрейт (kbps).
    /// Сохраняется в БД.
    /// </summary>
    [Reactive] public int PreferredBitrate { get; set; }

    /// <summary>
    /// ID трека-источника для радио.
    /// </summary>
    public string? RadioSeedId { get; set; }

    #endregion

    #region Runtime Cache (не сохраняется)

    /// <summary>
    /// Временный контейнер для текущей сессии (ручной выбор качества).
    /// </summary>
    [JsonIgnore] public string? TransientContainer { get; set; }

    /// <summary>
    /// Временный битрейт для текущей сессии.
    /// </summary>
    [JsonIgnore] public int TransientBitrate { get; set; }

    /// <summary>
    /// Кэшированный URL потока (не сохраняется).
    /// </summary>
    [JsonIgnore, Reactive] public string StreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Кэшированный кодек текущего потока.
    /// </summary>
    [JsonIgnore] public string CachedCodec { get; set; } = "";

    /// <summary>
    /// Кэшированный битрейт текущего потока.
    /// </summary>
    [JsonIgnore] public int CachedBitrate { get; set; }

    /// <summary>
    /// Кэшированный контейнер текущего потока.
    /// </summary>
    [JsonIgnore] public string CachedContainer { get; set; } = "";

    #endregion

    #region Constructors

    /// <summary>
    /// Конструктор по умолчанию для сериализатора.
    /// </summary>
    public TrackInfo() { }

    #endregion

    #region Methods

    /// <summary>
    /// Обновляет метаданные из свежего объекта.
    /// НЕ перезаписывает пользовательское состояние (IsLiked, IsDownloaded, IsCached).
    /// </summary>
    /// <param name="fresh">Объект с новыми данными (обычно из API).</param>
    public void UpdateMetadata(TrackInfo fresh)
    {
        if (!string.IsNullOrEmpty(fresh.Title)) Title = fresh.Title;
        if (!string.IsNullOrEmpty(fresh.Author)) Author = fresh.Author;
        if (!string.IsNullOrEmpty(fresh.Url)) Url = fresh.Url;
        if (!string.IsNullOrEmpty(fresh.ThumbnailUrl)) ThumbnailUrl = fresh.ThumbnailUrl;
        if (fresh.Duration.TotalSeconds > 0) Duration = fresh.Duration;
        if (fresh.IsOfficialArtist) IsOfficialArtist = true;
        if (fresh.IsMusic) IsMusic = true;
        if (!string.IsNullOrEmpty(fresh.ChannelId)) ChannelId = fresh.ChannelId;
    }

    /// <summary>
    /// Помечает трек как полностью закэшированный.
    /// Трек доступен офлайн, но файл в кэше (не в Downloads).
    /// </summary>
    /// <param name="container">Контейнер файла (webm, mp4).</param>
    /// <param name="bitrate">Битрейт в kbps.</param>
    public void MarkAsCached(string? container = null, int bitrate = 0)
    {
        IsCached = true;
        if (!string.IsNullOrEmpty(container)) PreferredContainer = container;
        if (bitrate > 0) PreferredBitrate = bitrate;
    }

    /// <summary>
    /// Помечает трек как скачанный (сохранён в Downloads).
    /// Также устанавливает IsCached = true.
    /// </summary>
    /// <param name="localPath">Путь к файлу в Downloads.</param>
    /// <param name="container">Контейнер файла.</param>
    /// <param name="bitrate">Битрейт в kbps.</param>
    public void MarkAsDownloaded(string localPath, string? container = null, int bitrate = 0)
    {
        IsDownloaded = true;
        IsCached = true;
        LocalPath = localPath;
        if (!string.IsNullOrEmpty(container)) PreferredContainer = container;
        if (bitrate > 0) PreferredBitrate = bitrate;
    }

    /// <summary>
    /// Сбрасывает статус кэширования (при очистке кэша).
    /// НЕ сбрасывает IsDownloaded.
    /// </summary>
    public void ClearCacheStatus()
    {
        if (!IsDownloaded)
        {
            IsCached = false;
        }
    }

    #endregion
}