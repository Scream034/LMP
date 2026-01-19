using System.Collections.Generic;

namespace MyLiteMusicPlayer.Models;

/// <summary>
/// Данные библиотеки и настройки пользователя, сохраняемые в JSON.
/// </summary>
public class LibraryData
{
    public Dictionary<string, TrackInfo> Tracks { get; set; } = [];
    public Dictionary<string, Playlist> Playlists { get; set; } = [];
    public List<string> LikedTrackIds { get; set; } = [];
    public List<string> RecentlyPlayedIds { get; set; } = [];

    // --- Настройки плеера ---
    public float Volume { get; set; } = 0.5f;
    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    // --- Настройки звука (Новое) ---
    /// <summary>
    /// Максимальный предел громкости (100, 200, 300 или 400).
    /// </summary>
    public int MaxVolumeLimit { get; set; } = 100;

    /// <summary>
    /// Целевое усиление в дБ (нормализация). 0 - без изменений.
    /// </summary>
    public float TargetGainDb { get; set; } = 0f;

    // --- Настройки приложения ---
    public string LanguageCode { get; set; } = "en";
    public string DownloadPath { get; set; } = string.Empty;
    public bool DiscordRpcEnabled { get; set; } = true;
    public bool AutoPlayOnUrlPaste { get; set; } = true;

    public int LoadBatchSize { get; set; } = 20;
    public bool EnableSmoothLoading { get; set; } = true;
}

public enum RepeatMode
{
    None,
    RepeatOne,
    RepeatAll
}