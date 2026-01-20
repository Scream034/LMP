using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using MyLiteMusicPlayer.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Playlist = MyLiteMusicPlayer.Models.Playlist;

namespace MyLiteMusicPlayer.Services;

public class YoutubeProvider
{
    private readonly YoutubeClient _youtube;
    private readonly string _downloadFolder;

    private readonly Dictionary<string, (string Url, long Size, DateTime Obtained)> _streamCache = new();
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(4);

    public bool IsReady { get; private set; }

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    private static readonly Regex YoutubeVideoRegex = new(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YoutubePlaylistRegex = new(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ValidYoutubeId = new(
        @"^[a-zA-Z0-9_-]{11}$",
        RegexOptions.Compiled);

    public YoutubeProvider()
    {
        _youtube = new YoutubeClient();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _downloadFolder = Path.Combine(appFolder, "Downloads");
        Directory.CreateDirectory(_downloadFolder);
    }

    public Task InitializeAsync()
    {
        IsReady = true;
        NotifyStatus(" Initialized");
        return Task.CompletedTask;
    }

    #region ========== RefreshStreamUrlAsync ==========

    public async Task<(string Url, long Size)?> RefreshStreamUrlAsync(TrackInfo track, CancellationToken ct = default)
    {
        string? videoId = ExtractVideoIdFromTrack(track);
        if (string.IsNullOrEmpty(videoId))
        {
            NotifyError("Could not extract video ID");
            return null;
        }

        var sw = Stopwatch.StartNew();
        NotifyStatus($"🔄 [{videoId}] Getting stream URL...");

        if (TryGetFromCache(videoId, out var cachedUrl, out var cachedSize))
        {
            track.StreamUrl = cachedUrl!;
            return (cachedUrl!, cachedSize);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cts.Token);

            var audioStream = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Container.Name == "mp4" ? 2 : 0)
                .ThenByDescending(s => s.Container.Name == "m4a" ? 1 : 0)
                .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                .FirstOrDefault();

            if (audioStream != null)
            {
                var url = audioStream.Url;
                var size = audioStream.Size.Bytes;
                sw.Stop();
                NotifyStatus($"[{videoId}] Got stream in {sw.ElapsedMilliseconds}ms ({audioStream.Container.Name}, {audioStream.Bitrate}, {size / 1024 / 1024}MB)");

                CacheStreamUrl(videoId, url, size);
                track.StreamUrl = url;
                return (url, size);
            }

            NotifyError($"[{videoId}] No audio streams found");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[{videoId}] Error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region ========== Cache ==========

    private bool TryGetFromCache(string videoId, out string? url, out long size)
    {
        if (_streamCache.TryGetValue(videoId, out var cached))
        {
            if (DateTime.UtcNow - cached.Obtained < _streamCacheLifetime)
            {
                NotifyStatus($"  Cache hit");
                url = cached.Url;
                size = cached.Size;
                return true;
            }
            _streamCache.Remove(videoId);
        }
        url = null;
        size = 0;
        return false;
    }

    private void CacheStreamUrl(string videoId, string url, long size)
    {
        _streamCache[videoId] = (url, size, DateTime.UtcNow);

        if (_streamCache.Count > 200)
        {
            var expired = _streamCache
                .Where(kv => DateTime.UtcNow - kv.Value.Obtained > _streamCacheLifetime)
                .Select(kv => kv.Key).ToList();
            foreach (var key in expired)
                _streamCache.Remove(key);
        }
    }

    #endregion

    #region ========== Search, Playlist, etc. ==========

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
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        if (match.Success) return match.Groups[1].Value;
        try { return VideoId.TryParse(url)?.Value; } catch { return null; }
    }

    private string? ExtractVideoIdFromTrack(TrackInfo track)
    {
        string cleanId = track.Id?.Trim() ?? "";
        if (cleanId.StartsWith("yt_"))
        {
            var rawId = cleanId[3..];
            var safeId = new string(rawId.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            if (ValidYoutubeId.IsMatch(safeId)) return safeId;
        }
        if (!string.IsNullOrWhiteSpace(track.Url))
            return ExtractVideoId(track.Url);
        return null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            var video = await _youtube.Videos.GetAsync(videoId);
            return ConvertToTrackInfo(video);
        }
        catch (Exception ex)
        {
            NotifyError($"GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 20)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var sw = Stopwatch.StartNew();

        try
        {
            var results = new List<TrackInfo>();

            await foreach (var video in _youtube.Search.GetVideosAsync(query))
            {
                if (results.Count >= maxResults) break;
                results.Add(ConvertSearchResultToTrackInfo(video));
            }

            sw.Stop();
            NotifyStatus($"Search '{query}': {results.Count} results in {sw.ElapsedMilliseconds}ms");
            return results;
        }
        catch (Exception ex)
        {
            NotifyError($"SearchAsync error: {ex.Message}");
            return [];
        }
    }

    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;
        try
        {
            var playlistId = PlaylistId.TryParse(url);
            if (playlistId == null) return null;

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            var videos = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            var tracks = videos.Select(ConvertPlaylistVideoToTrackInfo).ToList();
            return (playlist.Title, tracks);
        }
        catch (Exception ex)
        {
            NotifyError($"GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url)) return [];
        try
        {
            var videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) return [];
            var mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";
            var result = await GetPlaylistAsync(mixUrl);
            var tracks = result?.Tracks.Take(count).ToList() ?? [];
            foreach (var t in tracks) t.RadioSeedId = sourceTrack.Id;
            return tracks;
        }
        catch { return []; }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            var url = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(url);
            return result?.Tracks.Take(count).ToList() ?? await SearchAsync("top music 2024", count);
        }
        catch { return await SearchAsync("top music 2024", count); }
    }

    public Task<List<Playlist>> GetUserPlaylistsAsync() => Task.FromResult(new List<Playlist>());
    public Task<List<TrackInfo>> GetPersonalRecommendationsAsync(int count = 20) => GetTrendingAsync(count);

    public async Task<string?> DownloadTrackAsync(TrackInfo track, IProgress<float>? progress = null, CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url)) return null;
        try
        {
            var videoId = ExtractVideoId(track.Url);
            if (string.IsNullOrEmpty(videoId)) return null;

            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);

            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(_downloadFolder, fileName);

            var prog = progress != null ? new Progress<double>(p => progress.Report((float)p)) : null;
            await _youtube.Videos.Streams.DownloadAsync(stream, filePath, progress: prog, cancellationToken: ct);
            return filePath;
        }
        catch (Exception ex)
        {
            NotifyError($"Download error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region ========== Helpers ==========

    private TrackInfo ConvertToTrackInfo(Video video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private TrackInfo ConvertSearchResultToTrackInfo(VideoSearchResult video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private TrackInfo ConvertPlaylistVideoToTrackInfo(PlaylistVideo video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    private void NotifyStatus(string message)
    {
        Log.Info(message); // Теперь Log указывает на твой Logger
        OnStatusChanged?.Invoke(message);
    }

    private void NotifyError(string message)
    {
        Log.Error(message); // Теперь Log указывает на твой Logger
        OnError?.Invoke(message);
    }

    #endregion
}