using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using MyLiteMusicPlayer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.Services;

public class YoutubeProvider
{
    private readonly YoutubeDL _ytdl;
    private readonly HttpClient _http;
    private readonly GoogleAuthService _auth;

    private readonly string _appFolder;
    private readonly string _binFolder;
    private readonly string _downloadFolder;

    public bool IsReady { get; private set; }

    private static readonly Regex YoutubeVideoRegex = new(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YoutubePlaylistRegex = new(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public YoutubeProvider(GoogleAuthService auth)
    {
        _auth = auth;
        _http = new HttpClient();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _binFolder = Path.Combine(_appFolder, "Bin");
        _downloadFolder = Path.Combine(_appFolder, "Downloads");

        Directory.CreateDirectory(_binFolder);
        Directory.CreateDirectory(_downloadFolder);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = Path.Combine(_binFolder, "yt-dlp.exe"),
            FFmpegPath = Path.Combine(_binFolder, "ffmpeg.exe"),
            OutputFolder = _downloadFolder
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (!File.Exists(_ytdl.YoutubeDLPath))
                await YoutubeDLSharp.Utils.DownloadYtDlp(_binFolder);

            if (!File.Exists(_ytdl.FFmpegPath))
                await YoutubeDLSharp.Utils.DownloadFFmpeg(_binFolder);

            try { await _ytdl.RunUpdate(); } catch { }

            IsReady = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing binaries: {ex.Message}");
            IsReady = false;
        }
    }

    public QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();
        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return QueryType.DirectUrl;
        return QueryType.Search;
    }

    public string? ExtractVideoId(string url)
    {
        var match = YoutubeVideoRegex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady) return null;
        try
        {
            var res = await _ytdl.RunVideoDataFetch(url);
            if (!res.Success || res.Data == null) return null;
            return ConvertToTrackInfo(res.Data);
        }
        catch { return null; }
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 20)
    {
        if (!IsReady) return new List<TrackInfo>();
        try
        {
            // ytsearch вернет список без форматов (StreamUrl будет пустой). 
            // Это быстро и не вешает поиск.
            string searchParam = $"ytsearch{maxResults}:{query}";
            var res = await _ytdl.RunVideoDataFetch(searchParam);
            
            if (!res.Success || res.Data == null) return new List<TrackInfo>();

            var results = new List<TrackInfo>();
            if (res.Data.Entries != null)
            {
                foreach (var entry in res.Data.Entries)
                {
                    var track = ConvertToTrackInfo(entry);
                    if (track != null) results.Add(track);
                }
            }
            else
            {
                var track = ConvertToTrackInfo(res.Data);
                if (track != null) results.Add(track);
            }
            return results;
        }
        catch { return new List<TrackInfo>(); }
    }

    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;
        try
        {
            var res = await _ytdl.RunVideoDataFetch(url);
            if (!res.Success || res.Data == null) return null;

            string playlistName = res.Data.Title ?? "Unknown Playlist";
            var tracks = new List<TrackInfo>();
            if (res.Data.Entries != null)
            {
                foreach (var entry in res.Data.Entries)
                {
                    var track = ConvertToTrackInfo(entry);
                    if (track != null) tracks.Add(track);
                }
            }
            return (playlistName, tracks);
        }
        catch { return null; }
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url)) return new List<TrackInfo>();
        try {
            string? videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) return new List<TrackInfo>();
            string mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";
            var result = await GetPlaylistAsync(mixUrl);
            var tracks = result?.Tracks.Take(count).ToList() ?? new List<TrackInfo>();
            foreach (var track in tracks) track.RadioSeedId = sourceTrack.Id;
            return tracks;
        } catch { return new List<TrackInfo>(); }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try {
            string trendingUrl = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(trendingUrl);
            return result?.Tracks.Take(count).ToList() ?? new List<TrackInfo>();
        } catch { return await SearchAsync("top music 2024", count); }
    }

    public async Task<List<Playlist>> GetUserPlaylistsAsync()
    {
        return await Task.FromResult(new List<Playlist>()); 
        // Заглушка, используйте код из предыдущих версий, если нужна авторизация
    }
    
    public async Task<List<TrackInfo>> GetPersonalRecommendationsAsync(int count = 20)
    {
        return await GetTrendingAsync(count);
    }

    public async Task<string?> DownloadTrackAsync(TrackInfo track, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url)) return null;
        try
        {
            var progressHandler = progress != null ? new Progress<DownloadProgress>(p => progress.Report((float)p.Progress)) : null;
            var res = await _ytdl.RunAudioDownload(track.Url, AudioConversionFormat.Mp3, progress: progressHandler, ct: cancellationToken);
            return res.Success ? res.Data : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Этот метод вызывается перед стартом трека.
    /// Он идет в YouTube и получает "свежую" прямую ссылку на поток.
    /// </summary>
    public async Task<string?> RefreshStreamUrlAsync(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Url)) return null;

        var freshTrack = await GetTrackByUrlAsync(track.Url);
        
        if (freshTrack != null && !string.IsNullOrEmpty(freshTrack.StreamUrl))
        {
            track.StreamUrl = freshTrack.StreamUrl;
            if (freshTrack.Duration.TotalSeconds > 0)
                track.Duration = freshTrack.Duration;
                
            return freshTrack.StreamUrl;
        }

        return null;
    }

    private TrackInfo? ConvertToTrackInfo(YoutubeDLSharp.Metadata.VideoData data)
    {
        if (data == null) return null;

        string bestStream = string.Empty;

        // Если форматы есть, выбираем лучший аудио-поток
        if (data.Formats != null && data.Formats.Count() > 0)
        {
            var audioFormats = data.Formats.Where(f => f.AudioBitrate != null && f.AudioBitrate > 0).ToList();
            
            var bestFormat = audioFormats.FirstOrDefault(f => f.Extension == "m4a") 
                             ?? audioFormats.FirstOrDefault(f => f.Extension == "mp4")
                             ?? audioFormats.FirstOrDefault();

            if (bestFormat != null) bestStream = bestFormat.Url;
        }

        string videoId = data.ID ?? "";
        
        return new TrackInfo
        {
            Id = !string.IsNullOrEmpty(videoId) ? $"yt_{videoId}" : $"yt_{Guid.NewGuid():N}",
            Title = data.Title ?? "Unknown Title",
            Author = data.Uploader ?? data.Channel ?? "Unknown Artist",
            Url = data.WebpageUrl ?? $"https://youtube.com/watch?v={videoId}",
            StreamUrl = bestStream,
            Duration = data.Duration != null ? TimeSpan.FromSeconds((double)data.Duration) : TimeSpan.Zero,
            ThumbnailUrl = data.Thumbnail ?? string.Empty
        };
    }
}