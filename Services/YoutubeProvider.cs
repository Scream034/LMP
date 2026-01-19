using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using MyLiteMusicPlayer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.Services;

public class YoutubeProvider
{
    private readonly YoutubeDL _ytdl;
    private readonly HttpClient _http;
    private readonly GoogleAuthService _auth;
    private readonly string _binFolder;
    private readonly string _downloadFolder;

    public bool IsReady { get; private set; }

    private static readonly Regex YoutubeVideoRegex = new(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YoutubePlaylistRegex = new(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ValidYoutubeId = new(
        @"^[a-zA-Z0-9_-]{11}$",
        RegexOptions.Compiled);

    public YoutubeProvider(GoogleAuthService auth)
    {
        _auth = auth;
        _http = new HttpClient();

        SetupEncoding();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _binFolder = Path.Combine(appFolder, "Bin");
        _downloadFolder = Path.Combine(appFolder, "Downloads");

        Directory.CreateDirectory(_binFolder);
        Directory.CreateDirectory(_downloadFolder);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = Path.Combine(_binFolder, "yt-dlp.exe"),
            FFmpegPath = Path.Combine(_binFolder, "ffmpeg.exe"),
            OutputFolder = _downloadFolder
        };
    }

    private static void SetupEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Environment.SetEnvironmentVariable("PYTHONIOENCODING", "utf-8");
            Environment.SetEnvironmentVariable("PYTHONUTF8", "1");
            Environment.SetEnvironmentVariable("LANG", "en_US.UTF-8");
            Environment.SetEnvironmentVariable("LC_ALL", "en_US.UTF-8");

            if (OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("CHCP", "65001");
            }

            Debug.WriteLine("[YoutubeProvider] Encoding setup complete. PYTHONIOENCODING=utf-8");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeProvider] Encoding setup warning: {ex.Message}");
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            SetupEncoding();

            if (!File.Exists(_ytdl.YoutubeDLPath))
            {
                Debug.WriteLine("[YoutubeProvider] Downloading yt-dlp...");
                await YoutubeDLSharp.Utils.DownloadYtDlp(_binFolder);
            }

            if (!File.Exists(_ytdl.FFmpegPath))
            {
                Debug.WriteLine("[YoutubeProvider] Downloading ffmpeg...");
                await YoutubeDLSharp.Utils.DownloadFFmpeg(_binFolder);
            }

            try
            {
                Debug.WriteLine("[YoutubeProvider] Updating yt-dlp...");
                await _ytdl.RunUpdate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YoutubeProvider] Update warning: {ex.Message}");
            }

            IsReady = true;
            Debug.WriteLine("[YoutubeProvider] Initialized successfully.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeProvider] Init Error: {ex.Message}");
            IsReady = false;
        }
    }

    public QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query))
            return QueryType.Playlist;

        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return QueryType.DirectUrl;

        return QueryType.Search;
    }

    public string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            Debug.WriteLine($"[YoutubeProvider] === GetTrackByUrlAsync START ===");
            Debug.WriteLine($"[YoutubeProvider] URL: '{url}'");

            var res = await _ytdl.RunVideoDataFetch(url);

            if (!res.Success)
            {
                Debug.WriteLine($"[YoutubeProvider] Fetch FAILED!");
                foreach (var err in res.ErrorOutput ?? Array.Empty<string>())
                    Debug.WriteLine($"  - {err}");
                return null;
            }

            Debug.WriteLine($"[YoutubeProvider] Fetch SUCCESS. Converting to TrackInfo...");
            var track = ConvertToTrackInfo(res.Data);

            if (track != null)
            {
                Debug.WriteLine($"[YoutubeProvider] Track created: ID='{track.Id}', StreamUrl length={track.StreamUrl?.Length ?? 0}");
            }

            return track;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeProvider] GetTrackByUrlAsync EXCEPTION: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 20)
    {
        if (!IsReady) return new List<TrackInfo>();

        try
        {
            string searchParam = $"ytsearch{maxResults}:{query}";
            var res = await _ytdl.RunVideoDataFetch(searchParam);

            if (!res.Success) return new List<TrackInfo>();

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
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeProvider] SearchAsync EXCEPTION: {ex.Message}");
            return new List<TrackInfo>();
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeProvider] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url)) return new List<TrackInfo>();
        try
        {
            string? videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) return new List<TrackInfo>();
            string mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";
            var result = await GetPlaylistAsync(mixUrl);
            var tracks = result?.Tracks.Take(count).ToList() ?? new List<TrackInfo>();
            foreach (var track in tracks) track.RadioSeedId = sourceTrack.Id;
            return tracks;
        }
        catch { return new List<TrackInfo>(); }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            string trendingUrl = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(trendingUrl);
            return result?.Tracks.Take(count).ToList() ?? new List<TrackInfo>();
        }
        catch { return await SearchAsync("top music 2024", count); }
    }

    public async Task<List<Playlist>> GetUserPlaylistsAsync() => await Task.FromResult(new List<Playlist>());
    public async Task<List<TrackInfo>> GetPersonalRecommendationsAsync(int count = 20) => await GetTrendingAsync(count);

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

    public async Task<string?> RefreshStreamUrlAsync(TrackInfo track)
    {
        Debug.WriteLine($"[YoutubeProvider] === RefreshStreamUrlAsync START ===");
        Debug.WriteLine($"[YoutubeProvider] Track: '{track.Title}' (ID: {track.Id})");

        // Ждём готовности, если ещё не готов
        if (!IsReady)
        {
            Debug.WriteLine("[YoutubeProvider] Not ready, waiting...");
            for (int i = 0; i < 50 && !IsReady; i++) // макс 5 сек
            {
                await Task.Delay(100);
            }
            if (!IsReady)
            {
                Debug.WriteLine("[YoutubeProvider] Still not ready after waiting!");
                return null;
            }
        }

        string? urlToUse = null;
        string cleanId = track.Id?.Trim() ?? string.Empty;

        if (cleanId.StartsWith("yt_"))
        {
            string rawId = cleanId.Substring(3);
            var safeIdChars = rawId.Where(c =>
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_' || c == '-').ToArray();
            string safeId = new string(safeIdChars);

            if (ValidYoutubeId.IsMatch(safeId))
            {
                urlToUse = $"https://www.youtube.com/watch?v={safeId}";
                Debug.WriteLine($"[YoutubeProvider] Reconstructed URL: '{urlToUse}'");
            }
        }

        if (string.IsNullOrEmpty(urlToUse) && !string.IsNullOrWhiteSpace(track.Url))
        {
            var extractedId = ExtractVideoId(track.Url);
            if (!string.IsNullOrEmpty(extractedId) && ValidYoutubeId.IsMatch(extractedId))
            {
                urlToUse = $"https://www.youtube.com/watch?v={extractedId}";
            }
            else
            {
                urlToUse = track.Url.Trim();
            }
        }

        if (string.IsNullOrEmpty(urlToUse))
        {
            Debug.WriteLine($"[YoutubeProvider] CRITICAL: Could not determine URL!");
            return null;
        }

        var freshTrack = await GetTrackByUrlAsync(urlToUse);

        if (freshTrack != null && !string.IsNullOrEmpty(freshTrack.StreamUrl))
        {
            Debug.WriteLine($"[YoutubeProvider] Got fresh stream URL (length: {freshTrack.StreamUrl.Length})");

            track.StreamUrl = freshTrack.StreamUrl;
            if (freshTrack.Duration.TotalSeconds > 0)
                track.Duration = freshTrack.Duration;
            if (string.IsNullOrEmpty(track.Url))
                track.Url = freshTrack.Url;

            return freshTrack.StreamUrl;
        }

        Debug.WriteLine($"[YoutubeProvider] FAILED to get stream URL");
        return null;
    }

    private TrackInfo? ConvertToTrackInfo(YoutubeDLSharp.Metadata.VideoData data)
    {
        if (data == null) return null;

        Debug.WriteLine($"[YoutubeProvider] ConvertToTrackInfo:");
        Debug.WriteLine($"  ID: '{data.ID}', Title: '{data.Title}'");
        Debug.WriteLine($"  Uploader: '{data.Uploader}', Channel: '{data.Channel}'");

        string bestStream = string.Empty;

        if (data.Formats != null && data.Formats.Any())
        {
            // Фильтруем ТОЛЬКО аудио-форматы (без видео)
            var audioOnlyFormats = data.Formats
                .Where(f => f.AudioBitrate != null && f.AudioBitrate > 0)
                .Where(f => f.VideoCodec == "none" || string.IsNullOrEmpty(f.VideoCodec))
                .OrderByDescending(f => f.AudioBitrate)
                .ToList();

            Debug.WriteLine($"  Audio-only formats found: {audioOnlyFormats.Count}");

            foreach (var fmt in audioOnlyFormats)
            {
                Debug.WriteLine($"    - ext={fmt.Extension}, abr={fmt.AudioBitrate}, acodec={fmt.AudioCodec}, vcodec={fmt.VideoCodec}");
            }

            YoutubeDLSharp.Metadata.FormatData? bestFormat;

            // ИЗМЕНЕНИЕ: Всегда предпочитаем webm/opus - он лучше работает со стримингом
            // m4a требует seek к концу файла для чтения метаданных, что плохо для стриминга
            bestFormat = audioOnlyFormats.FirstOrDefault(f => f.Extension == "webm" && f.AudioCodec == "opus")
                         ?? audioOnlyFormats.FirstOrDefault(f => f.Extension == "webm")
                         ?? audioOnlyFormats.FirstOrDefault(f => f.Extension == "m4a")
                         ?? audioOnlyFormats.FirstOrDefault();

            if (bestFormat != null)
            {
                bestStream = bestFormat.Url ?? string.Empty;
                Debug.WriteLine($"  Selected format: ext={bestFormat.Extension}, bitrate={bestFormat.AudioBitrate}, codec={bestFormat.AudioCodec}");
            }
            else
            {
                Debug.WriteLine("  WARNING: No suitable audio format found!");
            }
        }
        else
        {
            Debug.WriteLine("  WARNING: No formats available!");
        }

        string videoId = data.ID ?? "";
        if (string.IsNullOrEmpty(videoId) || !ValidYoutubeId.IsMatch(videoId))
        {
            var cleanedId = new string((videoId ?? "").Where(c =>
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_' || c == '-').ToArray());

            if (cleanedId.Length == 11)
                videoId = cleanedId;
            else
                videoId = Guid.NewGuid().ToString("N").Substring(0, 11);
        }

        string trackUrl = data.WebpageUrl ?? "";
        if (string.IsNullOrEmpty(trackUrl) && ValidYoutubeId.IsMatch(videoId))
        {
            trackUrl = $"https://youtube.com/watch?v={videoId}";
        }

        return new TrackInfo
        {
            Id = $"yt_{videoId}",
            Title = data.Title ?? "Unknown Title",
            Author = data.Uploader ?? data.Channel ?? "Unknown Artist",
            Url = trackUrl,
            StreamUrl = bestStream,
            Duration = data.Duration != null ? TimeSpan.FromSeconds((double)data.Duration) : TimeSpan.Zero,
            ThumbnailUrl = data.Thumbnail ?? string.Empty
        };
    }
}