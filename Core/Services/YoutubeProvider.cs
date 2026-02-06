using LMP.Core.Youtube;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net;
using LMP.Core.Youtube.Channels;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;
using ReactiveUI.Fody.Helpers;
using ReactiveUI;

namespace LMP.Core.Services;

public partial class YoutubeProvider : IDisposable
{
    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    private readonly TrackRegistry _trackRegistry;
    private readonly CookieAuthService? _cookieAuth;
    private readonly LibraryService? _libraryService;
    private readonly Dictionary<string, StreamCacheEntry> _streamCache = [];
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);

    private YoutubeClient _youtube = null!;
    private HttpClient? _currentHttpClient; // Храним ссылку для dispose
    private bool _disposed;

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

    public YoutubeProvider(TrackRegistry trackRegistry, LibraryService? libraryService, CookieAuthService? cookieAuth)
    {
        _trackRegistry = trackRegistry;
        _libraryService = libraryService;
        _cookieAuth = cookieAuth;

        if (_cookieAuth != null)
        {
            ReloadClient();
            _cookieAuth.OnAuthStateChanged += ReloadClient;
        }
    }

    public void ReloadClient()
    {
        // 1. СНАЧАЛА диспозим старый клиент
        DisposeCurrentClient();

        // 2. Создаём новый
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false
        };

        var baseHttpClient = new HttpClient(handler);
        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, _cookieAuth, disposeClient: true);
        _currentHttpClient = new HttpClient(youtubeHandler, disposeHandler: true);

        _youtube = new YoutubeClient(_currentHttpClient);

        Log.Info($"[YouTube] Client reloaded. Auth provided: {_cookieAuth?.IsAuthenticated ?? false}");
    }

    private void DisposeCurrentClient()
    {
        try
        {
            // Диспозим текущую сессию поиска
            _currentSearchSession?.Dispose();
            _currentSearchSession = null;

            // YoutubeClient может иметь свой Dispose
            // Если нет - диспозим HttpClient напрямую
            if (_youtube is IDisposable disposableClient)
            {
                disposableClient.Dispose();
            }

            _currentHttpClient?.Dispose();
            _currentHttpClient = null;
            _youtube = null!;
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Error disposing client: {ex.Message}");
        }
    }

    public async Task InitializeAsync()
    {
        if (_cookieAuth?.IsAuthenticated == true)
        {
            Log.Info("[YouTube] Fetching fresh Visitor Data for auth session...");
            var visitorData = await FetchVisitorDataAsync();

            if (!string.IsNullOrEmpty(visitorData))
            {
                _youtube.Music.SetVisitorData(visitorData);
                Log.Info($"[YouTube] Visitor Data synchronized: {visitorData}");
            }
            else
            {
                _youtube.Music.SetVisitorData("CgtsZG1ySnZiQWtSbyiMjuGSBg%3D%3D");
            }
        }

        IsReady = true;
        NotifyStatus("[YouTube] Initialized");
    }

    private async Task<string?> FetchVisitorDataAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);

            if (_cookieAuth != null)
            {
                client.DefaultRequestHeaders.Add("Cookie", _cookieAuth.GetCookieHeader());
            }

            var jsonStr = await client.GetStringAsync("https://music.youtube.com/sw.js_data");

            if (jsonStr.StartsWith(")]}'")) jsonStr = jsonStr[4..];

            var json = Json.Parse(jsonStr);
            var visitorData = json[0][2][0][0][13].GetStringOrNull();

            return visitorData;
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Failed to fetch sw.js_data: {ex.Message}");
            return null;
        }
    }

    public YoutubeClient GetClient() => _youtube ?? throw new InvalidOperationException("YouTube client not initialized");

    // --- ПЕРСОНАЛИЗАЦИЯ ---

    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (_cookieAuth?.IsAuthenticated != true) return [];

        try
        {
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);
            var sections = new List<HomeSection>();

            foreach (var shelf in shelves)
            {
                var section = new HomeSection { Title = shelf.Title };

                foreach (var item in shelf.Items)
                {
                    var thumbUrl = item.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url
                                   ?? $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg";

                    bool isMusicContent = string.Equals(item.Type, "Song", StringComparison.OrdinalIgnoreCase);

                    var track = new TrackInfo
                    {
                        Id = "yt_" + item.Id,
                        Title = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = thumbUrl,
                        Duration = item.Duration ?? TimeSpan.Zero,
                        IsMusic = isMusicContent,
                        Url = $"https://music.youtube.com/watch?v={item.Id}"
                    };

                    if (item.Type == "Playlist" || item.Type == "Album")
                    {
                        track.Id = "yt_pl_" + item.Id;
                    }
                    else
                    {
                        track = _trackRegistry.RegisterOrUpdate(track);
                    }

                    section.Tracks.Add(track);
                }

                if (section.Tracks.Count > 0)
                    sections.Add(section);
            }

            return sections;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to get home: {ex.Message}");
            return [];
        }
    }

    // --- ЛАЙКИ И ПЛЕЙЛИСТЫ (WRITE) ---

    public async Task LikeTrackAsync(string trackId, bool like)
    {
        if (_cookieAuth?.IsAuthenticated != true) return;
        try
        {
            var vid = trackId.Replace("yt_", "");
            await _youtube.Music.LikeTrackAsync(vid, like);
            Log.Info($"[Music] Liked status set to {like} for {vid}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw;
        }
    }

    public async Task<string?> CreatePlaylistAsync(string title)
    {
        if (_cookieAuth?.IsAuthenticated != true) return null;
        try
        {
            var id = await _youtube.Music.CreatePlaylistAsync(title);
            return id;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    public async Task AddToPlaylistAsync(string playlistId, string trackId)
    {
        if (_cookieAuth?.IsAuthenticated != true) return;
        try
        {
            await _youtube.Music.AddToPlaylistAsync(playlistId, trackId.Replace("yt_", ""));
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to add to playlist: {ex.Message}");
        }
    }

    #region RefreshStreamUrlAsync
    public async Task<(string Url, long Size, int Bitrate, string Codec, string Container)?> RefreshStreamUrlAsync(
       TrackInfo track,
       bool forceRefresh = false,
       CancellationToken ct = default)
    {
        string? videoId = ExtractVideoIdFromTrack(track);
        if (string.IsNullOrEmpty(videoId))
        {
            NotifyError("[YouTube] Could not extract video ID");
            return null;
        }

        var sw = Stopwatch.StartNew();

        if (forceRefresh)
            NotifyStatus($"[YouTube] [{videoId}] 403 detected. Forcing stream URL refresh...");
        else
            NotifyStatus($"[YouTube] [{videoId}] Getting stream URL...");

        string? targetContainer = track.TransientContainer;
        int targetBitrate = track.TransientBitrate;

        if (string.IsNullOrEmpty(targetContainer))
        {
            var settings = _libraryService?.Settings;
            if (settings?.RememberTrackFormat == true)
            {
                targetContainer = track.PreferredContainer;
                targetBitrate = track.PreferredBitrate;
            }
        }

        string cacheKey = GenerateCacheKey(videoId, targetContainer, targetBitrate);

        if (!forceRefresh && TryGetFromCache(cacheKey, out var cached))
        {
            track.StreamUrl = cached.Url;
            NotifyStatus($"[YouTube] [{videoId}] Using cached URL ({cached.Codec}/{cached.Bitrate}kbps)");
            return (cached.Url, cached.Size, cached.Bitrate, cached.Codec, cached.Container);
        }

        try
        {
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
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
                return containerStreams.First();
            }
        }

        var qualityPref = _libraryService?.Settings.QualityPreference ?? AudioQualityPreference.BestAvailable;

        return qualityPref switch
        {
            AudioQualityPreference.BestAvailable => streams.FirstOrDefault(),
            AudioQualityPreference.Standard => streams.FirstOrDefault(s => s.Container.Name == "mp4")
                                ?? streams.FirstOrDefault(),
            _ => streams.FirstOrDefault(),
        };
    }
    #endregion

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
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId);

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
            var rawTrack = await _youtube.Videos.GetAsync(videoId);
            return _trackRegistry.RegisterOrUpdate(rawTrack);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    #region Search
    /// <summary>
    /// Streaming search с пагинацией батчами.
    /// </summary>
    public async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.None,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) yield break;

        var sw = Stopwatch.StartNew();
        int count = 0;
        bool isMusicFilter = filter.IsMusicContext();

        NotifyStatus($"[YouTube] Starting streaming search for '{query}' (Filter: {filter})...");

        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var tracks = new List<TrackInfo>();

            foreach (var result in batch.Items)
            {
                if (count >= maxResults) break;

                if (result is TrackInfo rawTrack)
                {
                    // Для музыкального фильтра принудительно ставим IsMusic
                    if (isMusicFilter)
                    {
                        rawTrack.IsMusic = true;
                    }

                    var track = _trackRegistry.RegisterOrUpdate(rawTrack);
                    tracks.Add(track);
                    count++;
                }
            }

            if (tracks.Count > 0)
            {
                NotifyStatus($"[YouTube] Got batch: +{tracks.Count} items (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        NotifyStatus($"[YouTube] Search complete: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Быстрый поиск без пагинации.
    /// </summary>
    public async Task<List<TrackInfo>> SearchFastAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.None,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var sw = Stopwatch.StartNew();
        var results = new List<TrackInfo>(maxResults);
        bool isMusicFilter = filter.IsMusicContext();

        try
        {
            // Используем GetResultBatchesAsync для поддержки всех типов фильтров
            await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
            {
                foreach (var item in batch.Items)
                {
                    if (results.Count >= maxResults) break;

                    if (item is TrackInfo rawTrack)
                    {
                        if (isMusicFilter) rawTrack.IsMusic = true;
                        var track = _trackRegistry.RegisterOrUpdate(rawTrack);
                        results.Add(track);
                    }
                }

                if (results.Count >= maxResults) break;
            }

            sw.Stop();
            NotifyStatus($"[YouTube] Fast search '{query}' (Filter: {filter}): {results.Count} results in {sw.ElapsedMilliseconds}ms");
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
    /// Простой поиск с фильтром по умолчанию (обратная совместимость).
    /// </summary>
    public async Task<List<TrackInfo>> SearchAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.None)
    {
        return await SearchFastAsync(query, maxResults, filter);
    }

    public sealed class SearchSession : IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly TrackRegistry _registry;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly SearchFilter _filter;
        private readonly HashSet<string> _seenIds;
        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private bool _disposed;
        private readonly List<TrackInfo> _buffer = [];
        private readonly SemaphoreSlim _disposeLock = new(1, 1);

        public bool HasMore => (_hasMore || _buffer.Count > 0) && !_disposed && _seenIds.Count < _maxResults;
        public int LoadedCount => _seenIds.Count;
        public SearchFilter Filter => _filter;

        internal SearchSession(
            YoutubeClient youtube,
            TrackRegistry registry,
            string query,
            int maxResults = 300,
            SearchFilter filter = SearchFilter.Video,
            IEnumerable<string>? skipTrackIds = null)
        {
            _youtube = youtube;
            _registry = registry;
            _query = query;
            _maxResults = maxResults;
            _filter = filter;
            _seenIds = [];

            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    var cleanId = id.StartsWith("yt_") ? id[3..] : id;
                    _seenIds.Add(cleanId);
                }
            }
        }

        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 50, CancellationToken ct = default)
        {
            if (_disposed || _seenIds.Count >= _maxResults) return [];

            var results = new List<TrackInfo>();

            // Берём из буфера
            while (results.Count < count && _buffer.Count > 0)
            {
                results.Add(_buffer[0]);
                _buffer.RemoveAt(0);
            }

            // Загружаем новые
            while (results.Count < count && _hasMore && _seenIds.Count < _maxResults)
            {
                try
                {
                    _enumerator ??= _youtube.Search
                        .GetResultBatchesAsync(_query, _filter, ct)
                        .GetAsyncEnumerator(ct);

                    if (!await _enumerator.MoveNextAsync())
                    {
                        _hasMore = false;
                        break;
                    }

                    var batch = _enumerator.Current;

                    foreach (var item in batch.Items)
                    {
                        if (_seenIds.Count >= _maxResults) break;

                        if (item is TrackInfo tInfo)
                        {
                            var rawId = tInfo.Id.StartsWith("yt_") ? tInfo.Id[3..] : tInfo.Id;
                            if (!_seenIds.Add(rawId)) continue;

                            var track = _registry.RegisterOrUpdate(tInfo);

                            if (results.Count < count)
                                results.Add(track);
                            else
                                _buffer.Add(track);
                        }
                        else if (item is Playlist pInfo)
                        {
                            var rawId = pInfo.YoutubeId;
                            if (string.IsNullOrEmpty(rawId) || !_seenIds.Add(rawId)) continue;

                            // Для плейлистов можно добавить конвертацию если нужно
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error($"[SearchSession] Error: {ex.Message}");
                    _hasMore = false;
                    break;
                }
            }

            return results;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposeLock.Wait();
            try
            {
                if (_disposed) return;
                _disposed = true;
                _hasMore = false;
                _buffer.Clear();
                _seenIds.Clear();

                // Синхронно ждём dispose enumerator
                if (_enumerator != null)
                {
                    try
                    {
                        _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[SearchSession] Dispose error: {ex.Message}");
                    }
                    _enumerator = null;
                }
            }
            finally
            {
                _disposeLock.Release();
                _disposeLock.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    private SearchSession? _currentSearchSession;

    /// <summary>
    /// Создаёт новую поисковую сессию с заданными параметрами.
    /// </summary>
    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.None,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, _trackRegistry, query, maxResults, filter, skipTrackIds);
        NotifyStatus($"[YouTube] Created search session for '{query}' (max: {maxResults}, filter: {filter})");

        return _currentSearchSession;
    }

    /// <summary>
    /// Выполняет поиск и возвращает начальные результаты вместе с сессией для пагинации.
    /// </summary>
    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 50,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.None,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return ([], null!);

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults, filter);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct);

        sw.Stop();
        NotifyStatus($"[YouTube] Initial search '{query}': {tracks.Count} results in {sw.ElapsedMilliseconds}ms (Filter: {filter})");

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
            var tracks = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            foreach (var t in tracks) _trackRegistry.RegisterOrUpdate(t);

            NotifyStatus($"[YouTube] Playlist '{playlist.Name}': {tracks.Count} tracks");
            return (playlist.Name, tracks.ToList());
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<(string ChannelName, List<PlaylistSearchResult> Playlists)?> GetChannelPlaylistsForSyncAsync(string channelUrl, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(channelUrl, ct);
        if (channel is null) return null;

        NotifyStatus($"[YouTube] Fetching playlists from channel: {channel.Title}...");

        try
        {
            var results = new List<PlaylistSearchResult>();

            await foreach (var pl in _youtube.Channels.GetPlaylistsAsync(channel.Id, ct))
            {
                if (pl.Name.Equals("Uploads", StringComparison.OrdinalIgnoreCase)) continue;

                var thumbs = new List<Thumbnail>();
                if (!string.IsNullOrEmpty(pl.ThumbnailUrl)) thumbs.Add(new Thumbnail(pl.ThumbnailUrl, new Resolution(0, 0)));

                var auth = pl.Author != null ? new Author(new ChannelId(channel.Id.Value), pl.Author) : null;

                results.Add(new PlaylistSearchResult(
                   new PlaylistId(pl.YoutubeId ?? ""),
                   pl.Name,
                   auth,
                   thumbs
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

    public static async Task<List<Playlist>> GetUserPlaylistsByAuthAsync()
    {
        var userDataService = Program.Services.GetRequiredService<YoutubeUserDataService>();
        return await userDataService.GetMyPlaylistsAsync();
    }

    public async Task<Playlist?> ImportPlaylistAsync(string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            var plId = new PlaylistId(playlistId);
            var playlist = await _youtube.Playlists.GetAsync(plId, ct);

            playlist.SyncMode = isAccountSync ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.CloudPublic;

            var tracks = await _youtube.Playlists.GetVideosAsync(plId, ct).CollectAsync();

            foreach (var track in tracks)
            {
                if (_libraryService != null)
                {
                    await _libraryService.AddOrUpdateTrackAsync(track, ct);
                }
                playlist.TrackIds.Add(track.Id);
            }
            return playlist;
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
            if (url.Contains("/channel/"))
            {
                var id = url.Split("/channel/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetAsync(new ChannelId(id), ct);
            }
            if (url.Contains("/@"))
            {
                var handle = url.Split("/@")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByHandleAsync(new ChannelHandle(handle), ct);
            }
            if (url.Contains("/c/"))
            {
                var slug = url.Split("/c/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetBySlugAsync(new ChannelSlug(slug), ct);
            }
            if (url.Contains("/user/"))
            {
                var user = url.Split("/user/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByUserAsync(new UserName(user), ct);
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
            if (result == null) return [];

            var tracks = result.Value.Tracks.Take(count).ToList();
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

            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(G.Folder.Downloads, fileName);

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cookieAuth?.OnAuthStateChanged -= ReloadClient;

        // КРИТИЧНО: Освобождаем все ресурсы
        DisposeCurrentClient();
        _streamCache.Clear();

        GC.SuppressFinalize(this);
        Log.Info("[YouTube] Provider disposed");
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

public class StreamOption : ReactiveObject
{
    public string Container { get; set; } = "";
    public double Bitrate { get; set; }
    public string Codec { get; set; } = "";
    public double SizeMb { get; set; }

    public string DisplayName => $"{Codec} {string.Format(LocalizationService.Instance.Get("Stream_Bitrate"), Bitrate)} ({Container})";

    public string SizeMbFormatted => string.Format(
    LocalizationService.Instance.Get("Stream_Format_Mb", "{0:F1} MB"),
    SizeMb);

    [Reactive] public bool IsDownloaded { get; set; }

    /// <summary>
    /// Текущий активный формат (который сейчас воспроизводится).
    /// </summary>
    [Reactive] public bool IsActive { get; set; }
}

public class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}