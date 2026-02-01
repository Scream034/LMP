using System.Text.Json.Serialization;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Models;

/// <summary>
/// Представляет музыкальный трек.
/// Реализует паттерн "Active Record" (частично) через реактивные свойства.
/// Является единственной точкой правды для состояния трека в памяти.
/// </summary>
public class TrackInfo : ReactiveObject, IBatchItem, ISearchResult
{
    // === Identity ===

    /// <summary>
    /// Уникальный идентификатор (yt_ID или local_ID). Неизменяемый.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    // === Metadata (Reactive, так как могут уточняться из сети) ===

    [Reactive] public string Title { get; set; } = string.Empty;
    [Reactive] public string Author { get; set; } = string.Empty;
    [Reactive] public string? ChannelId { get; set; }
    [Reactive] public string Url { get; set; } = string.Empty;
    [Reactive] public TimeSpan Duration { get; set; }

    // URL обложки. При изменении UI должен автоматически обновить картинку.
    [Reactive] public string ThumbnailUrl { get; set; } = string.Empty;

    public bool IsOfficialArtist { get; set; }
    public bool IsMusic { get; set; }

    /// <summary>
    /// Определяет, является ли трек явным видеоклипом (официальный канал артиста + НЕ помечен как аудио).
    /// Используется для фильтрации Music/Video.
    /// </summary>
    [JsonIgnore]
    public bool IsExplicitVideoClip => IsOfficialArtist && !IsMusic;

    // === State (Состояние - критически важно для Identity Map) ===

    /// <summary>
    /// Лайкнут ли трек. Изменение этого свойства мгновенно отразится во всех View.
    /// </summary>
    [Reactive] public bool IsLiked { get; set; }

    [Reactive] public bool IsDisliked { get; set; }

    [Reactive] public bool IsDownloaded { get; set; }

    [Reactive] public string? LocalPath { get; set; }

    // Коллекция ID плейлистов, в которых находится трек.
    // Используем HashSet для быстрого поиска.
    public HashSet<string> InPlaylists { get; set; } = [];

    // === Technical / Cache fields ===

    // Стрим URL не сохраняется на диск, но кэшируется в памяти объекта
    [JsonIgnore, Reactive] public string StreamUrl { get; set; } = string.Empty;

    public string? RadioSeedId { get; set; }

    // Предпочтения по формату (сохраняются)
    public string? PreferredContainer { get; set; }
    public int PreferredBitrate { get; set; }

    // Временные предпочтения (для текущей сессии)
    [JsonIgnore] public string? TransientContainer { get; set; }
    [JsonIgnore] public int TransientBitrate { get; set; }

    // Кэш кодеков для быстрого старта
    [JsonIgnore] public string CachedCodec { get; set; } = "";
    [JsonIgnore] public int CachedBitrate { get; set; }
    [JsonIgnore] public string CachedContainer { get; set; } = "";

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    /// <summary>
    /// Конструктор по умолчанию для сериализатора.
    /// </summary>
    public TrackInfo() { }

    /// <summary>
    /// Обновляет метаданные текущего объекта данными из нового (свежего) объекта.
    /// Используется, когда поиск возвращает более полные данные о треке, который уже есть в библиотеке.
    /// НЕ перезаписывает пользовательское состояние (IsLiked, IsDownloaded).
    /// </summary>
    /// <param name="freshInfo">Объект с новыми данными (обычно из API).</param>
    public void UpdateMetadata(TrackInfo freshInfo)
    {
        // Обновляем базовые поля, только если новые данные валидны
        if (!string.IsNullOrEmpty(freshInfo.Title)) Title = freshInfo.Title;
        if (!string.IsNullOrEmpty(freshInfo.Author)) Author = freshInfo.Author;
        if (!string.IsNullOrEmpty(freshInfo.Url)) Url = freshInfo.Url;

        // Часто в поиске приходит более качественная обложка
        if (!string.IsNullOrEmpty(freshInfo.ThumbnailUrl)) ThumbnailUrl = freshInfo.ThumbnailUrl;

        // Длительность из API поиска точнее, чем из парсинга плейлиста
        if (freshInfo.Duration.TotalSeconds > 0) Duration = freshInfo.Duration;

        if (freshInfo.IsOfficialArtist) IsOfficialArtist = true;
        if (freshInfo.IsMusic) IsMusic = true;

        if (!string.IsNullOrEmpty(freshInfo.ChannelId)) ChannelId = freshInfo.ChannelId;
    }
}