
// YoutubeProvider.cs
// Провайдер для работы с YouTube через YoutubeExplode
// Поиск, получение информации о треках, плейлистах и аудио потоках

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
using YoutubeExplode.Channels;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Провайдер для работы с YouTube.
/// Предоставляет функции:
/// - Поиск видео/музыки
/// - Получение информации о треках
/// - Извлечение аудио потоков с разным качеством
/// - Работа с плейлистами
/// - Скачивание треков
/// </summary>
public partial class YoutubeProvider
{
    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    private readonly YoutubeClient _youtube;
    private readonly string _downloadFolder;
    private readonly LibraryService? _libraryService;
    private readonly HttpClient _httpClient = new();
    private readonly GoogleAuthService? _authService;

    private readonly Dictionary<string, StreamCacheEntry> _streamCache = [];
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);

    private class StreamCacheEntry
    {
        public required string Url { get; init; }
        public long Size { get; init; }
        public int Bitrate { get; init; }
        public required string Codec { get; init; }
        public required string Container { get; init; }
        public DateTime Obtained { get; init; }
    }

    public bool IsReady { get; private set; }

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    public YoutubeProvider() : this(null, null)
    {
    }

    public YoutubeProvider(LibraryService? libraryService, GoogleAuthService? authService)
    {
        _youtube = new YoutubeClient();
        _libraryService = libraryService;
        _authService = authService;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _downloadFolder = Path.Combine(appFolder, "Downloads");

        Directory.CreateDirectory(_downloadFolder);
    }

    public Task InitializeAsync()
    {
        IsReady = true;
        NotifyStatus("[YouTube] Initialized");
        return Task.CompletedTask;
    }

    #region RefreshStreamUrlAsync

    public async Task<(string Url, long Size, int Bitrate, string Codec, string Container)?> RefreshStreamUrlAsync(
        TrackInfo track,
        CancellationToken ct = default)
    {
        string? videoId = ExtractVideoIdFromTrack(track);
        if (string.IsNullOrEmpty(videoId))
        {
            NotifyError("[YouTube] Could not extract video ID");
            return null;
        }

        var sw = Stopwatch.StartNew();
        NotifyStatus($"[YouTube] [{videoId}] Getting stream URL...");

        string? targetContainer = track.TransientContainer;
        int targetBitrate = track.TransientBitrate;

        if (string.IsNullOrEmpty(targetContainer))
        {
            if (_libraryService?.Data.RememberTrackFormat == true)
            {
                targetContainer = track.PreferredContainer;
                targetBitrate = track.PreferredBitrate;
            }
        }

        string cacheKey = GenerateCacheKey(videoId, targetContainer, targetBitrate);

        if (TryGetFromCache(cacheKey, out var cached))
        {
            track.StreamUrl = cached.Url;
            NotifyStatus($"[YouTube] [{videoId}] Using cached URL ({cached.Codec}/{cached.Bitrate}kbps)");
            return (cached.Url, cached.Size, cached.Bitrate, cached.Codec, cached.Container);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cts.Token);
            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
            {
                NotifyError($"[YouTube] [{videoId}] No audio streams found");
                return null;
            }

            AudioOnlyStreamInfo? selectedStream = SelectBestStream(audioStreams, targetContainer, targetBitrate);

            if (selectedStream == null)
            {
                NotifyError($"[YouTube] [{videoId}] Could not select audio stream");
                return null;
            }

            var url = selectedStream.Url;
            var size = selectedStream.Size.Bytes;
            var bitrate = (int)selectedStream.Bitrate.KiloBitsPerSecond;
            var container = selectedStream.Container.Name;
            var codec = DetermineCodec(container, selectedStream);

            sw.Stop();
            NotifyStatus($"[YouTube] [{videoId}] Got stream: {codec}/{bitrate}kbps ({container}) in {sw.ElapsedMilliseconds}ms");

            CacheStreamUrl(cacheKey, url, size, bitrate, codec, container);

            track.StreamUrl = url;
            return (url, size, bitrate, codec, container);
        }
        catch (OperationCanceledException)
        {
            NotifyError($"[YouTube] [{videoId}] Request timed out");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] [{videoId}] Error: {ex.Message}");
            return null;
        }
    }

    private AudioOnlyStreamInfo? SelectBestStream(
        List<AudioOnlyStreamInfo> streams,
        string? preferredContainer,
        int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        if (!string.IsNullOrEmpty(preferredContainer))
        {
            var containerStreams = streams.Where(s =>
                s.Container.Name.Equals(preferredContainer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (containerStreams.Count > 0)
            {
                if (preferredBitrate > 0)
                {
                    return containerStreams.MinBy(s => Math.Abs(s.Bitrate.KiloBitsPerSecond - preferredBitrate));
                }
                Log.Info($"[YouTube] Using preferred container: {preferredContainer}");
                return containerStreams.First();
            }
        }

        var qualityPref = _libraryService?.Data.QualityPreference ?? AudioQualityPreference.BestAvailable;

        return qualityPref switch
        {
            AudioQualityPreference.BestAvailable => streams.FirstOrDefault(),
            AudioQualityPreference.Standard => streams.FirstOrDefault(s => s.Container.Name == "mp4")
                                ?? streams.FirstOrDefault(),
            _ => streams.FirstOrDefault(),
        };
    }

    private static string DetermineCodec(string container, AudioOnlyStreamInfo stream)
    {
        var codecStr = stream.AudioCodec;

        if (!string.IsNullOrEmpty(codecStr))
        {
            if (codecStr.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
            if (codecStr.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (codecStr.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (codecStr.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        }

        return container.ToLower() switch
        {
            "webm" => "Opus",
            "mp4" => "AAC",
            "m4a" => "AAC",
            _ => container.ToUpper()
        };
    }

    public async Task<List<StreamOption>> GetStreamOptionsAsync(string videoId)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId)) return [];

        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId);

            return [.. manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .Select(s => new StreamOption
                {
                    Container = s.Container.Name,
                    Bitrate = s.Bitrate.KiloBitsPerSecond,
                    Codec = DetermineCodec(s.Container.Name, s),
                    SizeMb = s.Size.MegaBytes
                })];
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetStreamOptions error: {ex.Message}");
            return [];
        }
    }

    #endregion

    #region Cache

    private static string GenerateCacheKey(string videoId, string? container, int bitrate = 0)
    {
        var key = string.IsNullOrEmpty(container) ? videoId : $"{videoId}_{container}";
        if (bitrate > 0) key += $"_{bitrate}";
        return key;
    }

    private bool TryGetFromCache(string cacheKey, out StreamCacheEntry result)
    {
        if (_streamCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Obtained < _streamCacheLifetime)
            {
                result = cached;
                return true;
            }
            _streamCache.Remove(cacheKey);
        }

        result = null!;
        return false;
    }

    private void CacheStreamUrl(string cacheKey, string url, long size, int bitrate, string codec, string container)
    {
        _streamCache[cacheKey] = new StreamCacheEntry
        {
            Url = url,
            Size = size,
            Bitrate = bitrate,
            Codec = codec,
            Container = container,
            Obtained = DateTime.UtcNow
        };

        if (_streamCache.Count > MaxCacheSize) CleanupExpiredCache();
    }

    private void CleanupExpiredCache()
    {
        var expired = _streamCache
            .Where(kv => DateTime.UtcNow - kv.Value.Obtained > _streamCacheLifetime)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired) _streamCache.Remove(key);
    }

    public void ClearCache()
    {
        _streamCache.Clear();
        Log.Info("[YouTube] Stream cache cleared");
    }

    #endregion

    #region Search, Playlist, etc.

    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return QueryType.DirectUrl;
        }

        return QueryType.Search;
    }

    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        if (match.Success) return match.Groups[1].Value;
        try { return VideoId.TryParse(url)?.Value; } catch { return null; }
    }

    private static string? ExtractVideoIdFromTrack(TrackInfo track)
    {
        string cleanId = track.Id?.Trim() ?? "";
        if (cleanId.StartsWith("yt_"))
        {
            var rawId = cleanId[3..];
            var safeId = new string([.. rawId.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);
            if (ValidYoutubeId.IsMatch(safeId)) return safeId;
        }
        if (!string.IsNullOrWhiteSpace(track.Url)) return ExtractVideoId(track.Url);
        return null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            var video = await _youtube.Videos.GetAsync(videoId);
            var track = ConvertToTrackInfo(video);
            if (video.IsMusic) track.IsMusic = true;
            return track;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    #region Search

    /// <summary>
    /// Потоковый поиск - возвращает результаты по мере их получения
    /// </summary>
    public async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
        string query,
        int maxResults = 300,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) yield break;

        var sw = Stopwatch.StartNew();
        int count = 0;

        NotifyStatus($"[YouTube] Starting streaming search for '{query}'...");

        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, YoutubeExplode.Search.SearchFilter.Video, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var tracks = new List<TrackInfo>();

            foreach (var result in batch.Items)
            {
                if (count >= maxResults) break;

                if (result is VideoSearchResult video)
                {
                    tracks.Add(ConvertSearchResultToTrackInfo(video));
                    count++;
                }
            }

            if (tracks.Count > 0)
            {
                NotifyStatus($"[YouTube] Got batch: +{tracks.Count} tracks (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        NotifyStatus($"[YouTube] Search complete: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Быстрый поиск - возвращает первые результаты как можно быстрее
    /// </summary>
    public async Task<List<TrackInfo>> SearchFastAsync(string query, int maxResults = 100, CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var sw = Stopwatch.StartNew();
        var results = new List<TrackInfo>(maxResults);

        try
        {
            await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, YoutubeExplode.Search.SearchFilter.Video, ct))
            {
                foreach (var result in batch.Items)
                {
                    if (results.Count >= maxResults) break;

                    if (result is VideoSearchResult video)
                    {
                        results.Add(ConvertSearchResultToTrackInfo(video));
                    }
                }

                if (results.Count >= maxResults) break;
            }

            sw.Stop();
            NotifyStatus($"[YouTube] Fast search '{query}': {results.Count} results in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            NotifyStatus($"[YouTube] Search cancelled after {results.Count} results");
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] SearchFastAsync error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Старый метод для совместимости - теперь использует быстрый поиск
    /// </summary>
    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 100)
    {
        return await SearchFastAsync(query, maxResults);
    }

    /// <summary>
    /// Поиск с поддержкой продолжения (continuation) для ленивой загрузки
    /// </summary>
    public class SearchSession : IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly HashSet<string> _seenIds = [];
        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private bool _disposed;
        private readonly List<TrackInfo> _buffer = []; // Буфер для накопления результатов

        public bool HasMore => (_hasMore || _buffer.Count > 0) && !_disposed && _seenIds.Count < _maxResults;
        public int LoadedCount => _seenIds.Count;

        internal SearchSession(YoutubeClient youtube, string query, int maxResults = 300, IEnumerable<string>? skipTrackIds = null)
        {
            _youtube = youtube;
            _query = query;
            _maxResults = maxResults;
            _seenIds = [];

            // Конвертируем TrackInfo.Id ("yt_xxx") в YouTube ID ("xxx")
            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    var cleanId = id.StartsWith("yt_") ? id[3..] : id;
                    _seenIds.Add(cleanId);
                }
                Log.Info($"[SearchSession] Initialized with {_seenIds.Count} pre-skipped IDs");
            }
        }

        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 50, CancellationToken ct = default)
        {
            if (_disposed || _seenIds.Count >= _maxResults) return [];

            var results = new List<TrackInfo>();

            // Сначала берем из буфера
            while (results.Count < count && _buffer.Count > 0)
            {
                results.Add(_buffer[0]);
                _buffer.RemoveAt(0);
            }

            // Если буфер пуст и нужно больше - грузим из сети
            while (results.Count < count && _hasMore && _seenIds.Count < _maxResults)
            {
                try
                {
                    // Создаем енумератор при первом вызове
                    _enumerator ??= _youtube.Search
                        .GetResultBatchesAsync(_query, YoutubeExplode.Search.SearchFilter.Video, ct)
                        .GetAsyncEnumerator(ct);

                    if (!await _enumerator.MoveNextAsync())
                    {
                        _hasMore = false;
                        Log.Info($"[SearchSession] No more batches from YouTube");
                        break;
                    }

                    var batch = _enumerator.Current;
                    Log.Info($"[SearchSession] Got batch with {batch.Items.Count} items");

                    foreach (var item in batch.Items)
                    {
                        if (_seenIds.Count >= _maxResults) break;

                        if (item is VideoSearchResult video && _seenIds.Add(video.Id))
                        {
                            var track = ConvertSearchResultToTrackInfo(video);

                            if (results.Count < count)
                            {
                                results.Add(track);
                            }
                            else
                            {
                                // Остальное в буфер
                                _buffer.Add(track);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"[SearchSession] Error: {ex.Message}");
                    _hasMore = false;
                    break;
                }
            }

            Log.Info($"[SearchSession] Returning {results.Count} tracks, buffer: {_buffer.Count}, total seen: {_seenIds.Count}, hasMore: {HasMore}");
            return results;
        }

        private static TrackInfo ConvertSearchResultToTrackInfo(VideoSearchResult video)
        {
            var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
            return new TrackInfo
            {
                Id = $"yt_{video.Id.Value}",
                Title = video.Title,
                Author = video.Author.ChannelTitle,
                Url = video.Url,
                Duration = video.Duration ?? TimeSpan.Zero,
                ThumbnailUrl = thumb?.Url ?? "",
                IsOfficialArtist = video.IsOfficialArtist
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hasMore = false;
            _buffer.Clear();

            if (_enumerator != null)
            {
                _ = _enumerator.DisposeAsync().AsTask();
            }
        }
    }

    private SearchSession? _currentSearchSession;

    /// <summary>
    /// Создает сессию поиска для ленивой загрузки
    /// </summary>
    public SearchSession CreateSearchSession(string query, int maxResults = 300)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, query, maxResults);
        NotifyStatus($"[YouTube] Created search session for '{query}' (max: {maxResults})");
        return _currentSearchSession;
    }

    /// <summary>
    /// Создаёт сессию поиска для ленивой загрузки
    /// </summary>
    /// <param name="skipTrackIds">ID треков для пропуска (TrackInfo.Id с префиксом yt_)</param>
    public SearchSession CreateSearchSession(string query, int maxResults = 300, IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, query, maxResults, skipTrackIds);

        var skipCount = skipTrackIds?.Count() ?? 0;
        NotifyStatus($"[YouTube] Created search session for '{query}' (max: {maxResults}, skip: {skipCount})");

        return _currentSearchSession;
    }

    /// <summary>
    /// Быстрый первоначальный поиск с сессией
    /// </summary>
    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 50,
        int maxResults = 300,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return ([], null!);

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct);

        sw.Stop();
        NotifyStatus($"[YouTube] Initial search '{query}': {tracks.Count} results in {sw.ElapsedMilliseconds}ms (session hasMore: {session.HasMore})");

        return (tracks, session);
    }
    #endregion

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
            NotifyStatus($"[YouTube] Playlist '{playlist.Title}': {tracks.Count} tracks");
            return (playlist.Title, tracks);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    // Исправление 6: Robust метод для получения плейлистов с фейкового аккаунта (публичного канала)
    public async Task<(string ChannelName, List<PlaylistSearchResult> Playlists)?> GetChannelPlaylistsForSyncAsync(string channelUrl, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(channelUrl, ct);
        if (channel is null) return null;

        NotifyStatus($"[YouTube] Fetching playlists from channel: {channel.Title}...");

        try
        {
            var results = new List<PlaylistSearchResult>();

            // Используем Channels.GetPlaylistsAsync, он надежнее для списка плейлистов канала
            await foreach (var pl in _youtube.Channels.GetPlaylistsAsync(channel.Id, ct))
            {
                // Фильтрация системных плейлистов, если нужно
                if (pl.Title.Equals("Uploads", StringComparison.OrdinalIgnoreCase)) continue;

                results.Add(new PlaylistSearchResult(
                   pl.Id,
                   pl.Title,
                   pl.Author,
                   pl.Thumbnails
               ));
            }

            NotifyStatus($"[YouTube] Found {results.Count} playlists.");
            return (channel.Title, results);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error parsing channel playlists: {ex.Message}");
            return (channel.Title, []);
        }
    }

    public async Task<List<MyLiteMusicPlayer.Models.Playlist>> GetUserPlaylistsByAuthAsync()
    {
        var userDataService = Program.Services.GetRequiredService<YoutubeUserDataService>();
        return await userDataService.GetMyPlaylistsAsync();
    }

    public async Task<Playlist?> ImportPlaylistAsync(string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            var ytPlaylist = await _youtube.Playlists.GetAsync(playlistId, ct);
            var videos = await _youtube.Playlists.GetVideosAsync(playlistId, ct).CollectAsync();

            var newPlaylist = new Playlist
            {
                Id = $"yt_{ytPlaylist.Id}",
                YoutubeId = ytPlaylist.Id,
                Name = ytPlaylist.Title,
                Author = ytPlaylist.Author?.ChannelTitle,
                ThumbnailUrl = ytPlaylist.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault()?.Url,
                SyncMode = isAccountSync ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.CloudPublic,
            };

            foreach (var video in videos)
            {
                var trackInfo = ConvertPlaylistVideoToTrackInfo(video);
                _libraryService?.AddOrUpdateTrack(trackInfo);
                newPlaylist.TrackIds.Add(trackInfo.Id);
            }
            return newPlaylist;
        }
        catch (Exception ex)
        {
            NotifyError($"Error importing playlist {playlistId}: {ex.Message}");
            return null;
        }
    }

    public async Task<(string Name, string AvatarUrl)?> GetChannelInfoAsync(string url, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(url, ct);
        if (channel == null) return null;
        return (channel.Title, channel.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault()?.Url ?? "");
    }

    private async Task<Channel?> GetChannelFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            // Улучшенный Regex парсинг для разных форматов
            if (url.Contains("/channel/"))
            {
                var id = url.Split("/channel/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetAsync(id, ct);
            }
            if (url.Contains("/@"))
            {
                var handle = url.Split("/@")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByHandleAsync(handle, ct);
            }
            if (url.Contains("/c/"))
            {
                var slug = url.Split("/c/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetBySlugAsync(slug, ct);
            }
            if (url.Contains("/user/"))
            {
                var user = url.Split("/user/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByUserAsync(user, ct);
            }

            NotifyError("[YouTube] Could not recognize channel URL format.");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error getting channel info: {ex.Message}");
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
        catch
        {
            return await SearchAsync("top music 2024", count);
        }
    }

    public async Task<string?> DownloadTrackAsync(
        TrackInfo track,
        IProgress<float>? progress = null,
        CancellationToken ct = default)
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
            NotifyStatus($"[YouTube] Downloaded: {fileName}");
            return filePath;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Download error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Helpers

    private static TrackInfo ConvertToTrackInfo(Video video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault();
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

    private static TrackInfo ConvertSearchResultToTrackInfo(VideoSearchResult video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? "",
            IsOfficialArtist = video.IsOfficialArtist
        };
    }

    private static TrackInfo ConvertPlaylistVideoToTrackInfo(PlaylistVideo video)
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
        var sanitized = new string([.. name.Where(c => !invalid.Contains(c))]);
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    private void NotifyStatus(string message)
    {
        Log.Info(message);
        OnStatusChanged?.Invoke(message);
    }

    private void NotifyError(string message)
    {
        Log.Error(message);
        OnError?.Invoke(message);
    }

    [GeneratedRegex(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubeVideoRegex();

    [GeneratedRegex(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubePlaylistRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex _ValidYoutubeId();

    #endregion
}

/// <summary>
/// Информация о доступном аудио потоке
/// </summary>
public class StreamOption
{
    /// <summary>Контейнер (webm/mp4)</summary>
    public string Container { get; set; } = "";

    /// <summary>Битрейт в kbps</summary>
    public double Bitrate { get; set; }

    /// <summary>Кодек (Opus/AAC)</summary>
    public string Codec { get; set; } = "";

    /// <summary>Размер в мегабайтах</summary>
    public double SizeMb { get; set; }

    /// <summary>Отображаемое имя для UI</summary>
    public string DisplayName => $"{Codec} {Bitrate:F0}kbps ({Container})";
}