// YoutubeProvider.cs
// Провайдер для работы с YouTube через YoutubeExplode
// Поиск, получение информации о треках, плейлистах и аудио потоках

using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using MyLiteMusicPlayer.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Playlist = MyLiteMusicPlayer.Core.Models.Playlist;
using YoutubeExplode.Channels;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace MyLiteMusicPlayer.Core.Services;

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

    private YoutubeClient _youtube = null!;
    private readonly CookieAuthService _cookieAuth;
    private readonly string _downloadFolder;
    private readonly LibraryService? _libraryService;

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

    public YoutubeProvider(LibraryService? libraryService, CookieAuthService cookieAuth)
    {
        _libraryService = libraryService;
        _cookieAuth = cookieAuth;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _downloadFolder = Path.Combine(appFolder, "Downloads");
        Directory.CreateDirectory(_downloadFolder);

        ReloadClient();
        _cookieAuth.OnAuthStateChanged += ReloadClient;
    }

    public void ReloadClient()
    {
        // Берем куки
        var cookies = _cookieAuth.GetCookies();
        // Берем UA из сервиса (он загрузил его из файла или взял дефолтный)
        var ua = _cookieAuth.UserAgent;

        // Передаем в конструктор YoutubeClient
        _youtube = new YoutubeClient(new HttpClient(), cookies, ua);

        Log.Info($"[YouTube] Client reloaded. Authenticated: {_cookieAuth.IsAuthenticated} (Cookies: {cookies.Count}). UA: {ua[..30]}...");
    }

    public Task InitializeAsync()
    {
        IsReady = true;
        NotifyStatus("[YouTube] Initialized");
        return Task.CompletedTask;
    }

    public YoutubeClient GetClient() => _youtube;

    // --- ПЕРСОНАЛИЗАЦИЯ ---

    /// <summary>
    /// Получает полки с главной страницы (My Supermix, Listen Again...)
    /// </summary>
    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (!_cookieAuth.IsAuthenticated) return [];

        try
        {
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);
            var sections = new List<HomeSection>();

            foreach (var shelf in shelves)
            {
                var section = new HomeSection { Title = shelf.Title };

                foreach (var item in shelf.Items)
                {
                    // Конвертируем MusicItem в TrackInfo
                    // Важно: MusicItem может быть песней, видео, альбомом или плейлистом
                    var track = new TrackInfo
                    {
                        Id = "yt_" + item.Id,
                        Title = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = item.Thumbnails.LastOrDefault()?.Url ?? "",
                        Duration = item.Duration ?? TimeSpan.Zero,
                        IsMusic = true
                    };

                    // Если это плейлист (например, "My Supermix"), ID начинается с "RD..." или "PL..."
                    // Мы можем пометить его как плейлист
                    if (item.Type == "Playlist" || item.Type == "Album")
                    {
                        // Для плейлистов UI должен знать, что по клику нужно открывать плейлист, а не играть трек
                        track.Id = "yt_pl_" + item.Id;
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
        if (!_cookieAuth.IsAuthenticated) return;
        try
        {
            var vid = trackId.Replace("yt_", "");
            await _youtube.Music.LikeTrackAsync(vid, like);
            Log.Info($"[Music] Liked status set to {like} for {vid}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw; // <-- ВАЖНО: Пробрасываем ошибку, чтобы Manager и UI знали о сбое
        }
    }

    public async Task<string?> CreatePlaylistAsync(string title)
    {
        if (!_cookieAuth.IsAuthenticated) return null;
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
        if (!_cookieAuth.IsAuthenticated) return;
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
        // Меняем сообщение лога, чтобы видеть, что происходит обновление
        if (forceRefresh)
            NotifyStatus($"[YouTube] [{videoId}] 403 detected. Forcing stream URL refresh...");
        else
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

        // ИЗМЕНЕНИЕ: Если forceRefresh == true, мы пропускаем блок с кэшем
        if (!forceRefresh && TryGetFromCache(cacheKey, out var cached))
        {
            track.StreamUrl = cached.Url;
            NotifyStatus($"[YouTube] [{videoId}] Using cached URL ({cached.Codec}/{cached.Bitrate}kbps)");
            return (cached.Url, cached.Size, cached.Bitrate, cached.Codec, cached.Container);
        }

        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
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
        SearchFilter filter = SearchFilter.Video, // Добавлен аргумент фильтра
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) yield break;

        var sw = Stopwatch.StartNew();
        int count = 0;

        NotifyStatus($"[YouTube] Starting streaming search for '{query}' (Filter: {filter})...");

        // Передаем filter в YoutubeExplode
        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var tracks = new List<TrackInfo>();

            foreach (var result in batch.Items)
            {
                if (count >= maxResults) break;

                // Обработка видео (и музыки)
                if (result is VideoSearchResult video)
                {
                    tracks.Add(ConvertSearchResultToTrackInfo(video));
                    count++;
                }
                // Обработка плейлистов (если выбран фильтр Playlist)
                else if (result is PlaylistSearchResult playlist)
                {
                    tracks.Add(ConvertPlaylistSearchResultToTrackInfo(playlist));
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
    /// Быстрый поиск - возвращает первые результаты как можно быстрее
    /// </summary>
    public async Task<List<TrackInfo>> SearchFastAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.Video, // Добавлен аргумент фильтра
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var sw = Stopwatch.StartNew();
        var results = new List<TrackInfo>(maxResults);

        try
        {
            await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
            {
                foreach (var result in batch.Items)
                {
                    if (results.Count >= maxResults) break;

                    if (result is VideoSearchResult video)
                    {
                        results.Add(ConvertSearchResultToTrackInfo(video));
                    }
                    else if (result is PlaylistSearchResult playlist)
                    {
                        results.Add(ConvertPlaylistSearchResultToTrackInfo(playlist));
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
    /// Старый метод для совместимости - теперь использует быстрый поиск по видео
    /// </summary>
    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 100)
    {
        return await SearchFastAsync(query, maxResults, SearchFilter.Video);
    }

    /// <summary>
    /// Поиск с поддержкой продолжения (continuation) для ленивой загрузки
    /// </summary>
    public class SearchSession : IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly SearchFilter _filter; // Храним фильтр
        private readonly HashSet<string> _seenIds = [];
        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private bool _disposed;
        private readonly List<TrackInfo> _buffer = [];

        public bool HasMore => (_hasMore || _buffer.Count > 0) && !_disposed && _seenIds.Count < _maxResults;
        public int LoadedCount => _seenIds.Count;

        // Конструктор обновлен для приема SearchFilter
        internal SearchSession(
            YoutubeClient youtube,
            string query,
            int maxResults = 300,
            SearchFilter filter = SearchFilter.Video,
            IEnumerable<string>? skipTrackIds = null)
        {
            _youtube = youtube;
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

            while (results.Count < count && _buffer.Count > 0)
            {
                results.Add(_buffer[0]);
                _buffer.RemoveAt(0);
            }

            while (results.Count < count && _hasMore && _seenIds.Count < _maxResults)
            {
                try
                {
                    // Используем _filter при создании перечислителя
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

                        string? id = null;
                        TrackInfo? track = null;

                        // Логика обработки разных типов результатов
                        if (item is VideoSearchResult video)
                        {
                            id = video.Id.Value;
                            if (_seenIds.Add(id))
                                track = ConvertSearchResultToTrackInfo(video);
                        }
                        else if (item is PlaylistSearchResult playlist)
                        {
                            id = playlist.Id.Value;
                            if (_seenIds.Add(id))
                                track = ConvertPlaylistSearchResultToTrackInfo(playlist);
                        }

                        if (track != null)
                        {
                            if (results.Count < count)
                                results.Add(track);
                            else
                                _buffer.Add(track);
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

        // ... (Dispose метод остается прежним)
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
    /// Создает сессию поиска для ленивой загрузки (с поддержкой фильтра)
    /// </summary>
    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, query, maxResults, filter, skipTrackIds);

        var skipCount = skipTrackIds?.Count() ?? 0;
        NotifyStatus($"[YouTube] Created search session for '{query}' (max: {maxResults}, filter: {filter})");

        return _currentSearchSession;
    }

    /// <summary>
    /// Быстрый первоначальный поиск с сессией
    /// </summary>
    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 50,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video, // Добавлен аргумент
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

    public async Task<List<Models.Playlist>> GetUserPlaylistsByAuthAsync()
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

    private static TrackInfo ConvertPlaylistSearchResultToTrackInfo(PlaylistSearchResult playlist)
    {
        var thumb = playlist.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault();
        return new TrackInfo
        {
            // Используем префикс yt_pl_ чтобы отличить плейлист от трека, если UI поддерживает это
            // Или можно использовать просто yt_, но тогда при попытке воспроизвести как трек будет ошибка
            Id = $"yt_pl_{playlist.Id.Value}",
            Title = playlist.Title,
            Author = playlist.Author?.ChannelTitle ?? "Unknown",
            Url = playlist.Url,
            Duration = TimeSpan.Zero, // У плейлистов нет длительности в поиске
            ThumbnailUrl = thumb?.Url ?? "",
            IsMusic = false // Плейлист сам по себе не музыкальный файл
        };
    }

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
            IsOfficialArtist = video.IsOfficialArtist,
            IsMusic = video.IsMusic
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

public class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}