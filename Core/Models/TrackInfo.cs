using System.Text.Json.Serialization;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.Models;

/// <summary>
/// Единственный источник правды для состояния трека.
/// Все свойства реактивные — UI обновляется автоматически.
/// </summary>
public sealed class TrackInfo : ReactiveObject, IBatchItem, ISearchResult
{
    // === Identity (неизменяемое) ===
    public string Id { get; set; } = string.Empty;

    // === Metadata (реактивные, могут обновляться из сети) ===
    [Reactive] public string Title { get; set; } = string.Empty;
    [Reactive] public string Author { get; set; } = string.Empty;
    [Reactive] public string? ChannelId { get; set; }
    [Reactive] public string Url { get; set; } = string.Empty;
    [Reactive] public TimeSpan Duration { get; set; }
    [Reactive] public string ThumbnailUrl { get; set; } = string.Empty;

    public bool IsOfficialArtist { get; set; }
    public bool IsMusic { get; set; }

    [JsonIgnore]
    public bool IsExplicitVideoClip => IsOfficialArtist && !IsMusic;

    // === User State (реактивные — ключевые для UI) ===
    [Reactive] public bool IsLiked { get; set; }
    [Reactive] public bool IsDisliked { get; set; }
    [Reactive] public bool IsDownloaded { get; set; }
    [Reactive] public string? LocalPath { get; set; }

    // === Format Preferences (реактивные) ===
    [Reactive] public string? PreferredContainer { get; set; }
    [Reactive] public int PreferredBitrate { get; set; }

    // === Playlists ===
    public HashSet<string> InPlaylists { get; set; } = [];

    // === Cache/Runtime (не сохраняются) ===
    [JsonIgnore, Reactive] public string StreamUrl { get; set; } = string.Empty;
    [JsonIgnore] public string? TransientContainer { get; set; }
    [JsonIgnore] public int TransientBitrate { get; set; }
    [JsonIgnore] public string CachedCodec { get; set; } = "";
    [JsonIgnore] public int CachedBitrate { get; set; }
    [JsonIgnore] public string CachedContainer { get; set; } = "";

    public string? RadioSeedId { get; set; }
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    public TrackInfo() { }

    /// <summary>
    /// Обновляет метаданные из свежего объекта (НЕ перезаписывает пользовательское состояние).
    /// </summary>
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
    /// Помечает трек как скачанный. Вызывается из StreamCacheManager.
    /// </summary>
    public void MarkAsDownloaded(string localPath, string? container = null, int bitrate = 0)
    {
        IsDownloaded = true;
        LocalPath = localPath;
        if (!string.IsNullOrEmpty(container)) PreferredContainer = container;
        if (bitrate > 0) PreferredBitrate = bitrate;
    }
}