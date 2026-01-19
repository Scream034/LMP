using YoutubeDLSharp;
using MyLiteMusicPlayer.Models;
using System.IO;

namespace MyLiteMusicPlayer.Services;

public class YoutubeProvider
{
    private readonly YoutubeDL _ytdl;
    public bool IsReady { get; private set; } = false;

    public YoutubeProvider()
    {
        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = "yt-dlp.exe",
            FFmpegPath = "ffmpeg.exe",
            OutputFolder = "Downloads" // На всякий случай
        };
    }

    public async Task InitializeAsync()
    {
        // В реальном проекте тут стоит скачивать yt-dlp, если его нет
        // Для примера просто проверяем наличие
        if (!File.Exists(_ytdl.YoutubeDLPath)) 
        {
            Console.WriteLine("Warning: yt-dlp.exe not found!");
            // Тут можно вызвать Utils.DownloadYtDlp(), если он есть
        }
        
        IsReady = true;
        await Task.CompletedTask;
    }

    public async Task<TrackInfo?> SearchAndGetTrackAsync(string query)
    {
        if (!IsReady) return null;

        string searchParam = query.StartsWith("http") ? query : $"ytsearch1:{query}";
        
        // Получаем метаданные без скачивания видео
        var res = await _ytdl.RunVideoDataFetch(searchParam);
        
        if (!res.Success || res.Data == null) return null;

        var data = res.Data;
        string bestStream = string.Empty;

        // Пытаемся найти лучший аудиопоток
        if (data.Formats != null)
        {
            var audioFormat = data.Formats
                .Where(f => f.AudioBitrate != null) // Берем любые с аудио
                .OrderByDescending(f => f.AudioBitrate)
                .FirstOrDefault();
            
            bestStream = audioFormat?.Url ?? data.Url ?? string.Empty;
        }

        return new TrackInfo
        {
            Title = data.Title ?? "Unknown Title",
            Author = data.Uploader ?? "Unknown Author",
            Url = data.WebpageUrl ?? string.Empty,
            StreamUrl = bestStream,
            Duration = data.Duration != null ? TimeSpan.FromSeconds((double)data.Duration) : TimeSpan.Zero,
            ThumbnailUrl = data.Thumbnail ?? string.Empty
        };
    }
}