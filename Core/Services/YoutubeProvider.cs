using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using ReactiveUI;

using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Bridge.NToken;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Bridge.PoToken;
using LMP.Core.Audio.Http;

namespace LMP.Core.Services;

public partial class YoutubeProvider : IDisposable
{
    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    private readonly NTokenDecryptor _nTokenDecryptor;
    private readonly SigCipherDecryptor _sigCipherDecryptor;
    private readonly TrackRegistry _trackRegistry;
    public readonly CookieAuthService AuthService = null!;
    private readonly LibraryService? _libraryService;
    private readonly YoutubeUserDataService _userDataService;
    private PoTokenProvider? _poTokenProvider;

    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);
    private readonly ConcurrentDictionary<string, StreamCacheEntry> _streamCache =
        new(StringComparer.Ordinal);

    private YoutubeClient _youtube = null!;

    private SocketsHttpHandler? _currentHandler;
    private HttpClient? _currentHttpClient;
    private volatile bool _disposed;

    private Task? _initTask;
    private readonly Lock _initLock = new();

    private readonly struct StreamCacheEntry
    {
        public readonly string Url;
        public readonly long Size;
        public readonly int Bitrate;
        public readonly string Codec;
        public readonly string Container;
        public readonly DateTime Obtained;

        public StreamCacheEntry(string url, long size, int bitrate, string codec, string container)
        {
            Url = url;
            Size = size;
            Bitrate = bitrate;
            Codec = codec;
            Container = container;
            Obtained = DateTime.UtcNow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired(TimeSpan lifetime) =>
            DateTime.UtcNow - Obtained > lifetime;
    }

    public bool IsReady { get; private set; }

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    public event Action<string>? OnNTokenDecryptionStarted;

    public YoutubeProvider(
        TrackRegistry trackRegistry,
        LibraryService? libraryService,
        CookieAuthService cookieAuth,
        NTokenDecryptor nTokenDecryptor,
        SigCipherDecryptor sigCipherDecryptor,
        YoutubeUserDataService userDataService)
    {
        _trackRegistry = trackRegistry;
        _libraryService = libraryService;
        AuthService = cookieAuth;
        _nTokenDecryptor = nTokenDecryptor;
        _sigCipherDecryptor = sigCipherDecryptor;
        _userDataService = userDataService;
        _poTokenProvider = new PoTokenProvider(SharedHttpClient.Instance);

        // Связываем централизованный утилитный класс с куками сессии
        YoutubeClientUtils.Initialize(cookieAuth);

        _nTokenDecryptor.OnComplexDecryptionStarted += HandleNTokenDecryptionStarted;

        if (AuthService != null)
        {
            ReloadClient();
            AuthService.OnAuthStateChanged += ReloadClient;
        }
    }

    private void HandleNTokenDecryptionStarted(string? rawVideoId)
    {
        if (string.IsNullOrWhiteSpace(rawVideoId))
            return;

        OnNTokenDecryptionStarted?.Invoke(rawVideoId);
    }

    #region Bot Detection — Stateless Helpers

    public static bool CanPlayOffline(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Id))
            return false;

        var rawId = track.GetRawIdSpan().ToString();
        if (string.IsNullOrEmpty(rawId))
            return false;

        var cached = AudioSourceFactory.FindAnyCachedTrack(rawId);
        return cached != null;
    }

    public static bool CanPerformNetworkOperation() => !VideoController.IsInCooldown;

    public static void ThrowIfInCooldown() => VideoController.ThrowIfInCooldown();

    #endregion

    #region Client Initialization

    public void ReloadClient()
    {
        DisposeCurrentClient();

        // VisitorData меняется при смене аккаунта → старый session token невалиден
        _poTokenProvider?.Invalidate();

        _currentHandler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 20,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        var baseHttpClient = new HttpClient(_currentHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, AuthService, disposeClient: true);

        _currentHttpClient = new HttpClient(youtubeHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _youtube = new YoutubeClient(
            _currentHttpClient,
            _nTokenDecryptor,
            _sigCipherDecryptor,
            isAuthenticatedCheck: () => AuthService?.IsAuthenticated ?? false,
            ownsHttpClient: false,
            poTokenProvider: _poTokenProvider);

        Log.Info($"[YouTube] Client reloaded. Auth: {AuthService?.IsAuthenticated ?? false}");
    }

    private void DisposeCurrentClient()
    {
        try
        {
            _currentSearchSession?.Dispose();
            _currentSearchSession = null;

            _currentHttpClient?.Dispose();
            _currentHttpClient = null;

            _youtube = null!;
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Error disposing client: {ex.Message}");
        }
    }

    /// <summary>
    /// Инициализирует провайдер YouTube (потокобезопасно, выполняется только 1 раз).
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_initLock)
        {
            _initTask ??= InitializeInternalAsync();
            return _initTask;
        }
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            Log.Info("[YouTube] Initiating lazy background Visitor Data resolution...");

            // YoutubeClientUtils сама получит VisitorData, учтет куки и защитит таймаутом.
            // Больше не нужно вручную пробрасывать токен в клиент Music.
            _ = await YoutubeClientUtils.EnsureVisitorDataAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Initialization non-fatal warning: {ex.Message}");
        }
        finally
        {
            IsReady = true;
            NotifyStatus("[YouTube] Initialized");
        }
    }

    public YoutubeClient GetClient() =>
        _youtube ?? throw new InvalidOperationException("YouTube client not initialized");

    #endregion

    #region Personalization

    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true) return [];

        ThrowIfInCooldown();

        try
        {
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);
            var sections = new List<HomeSection>(shelves.Count);

            foreach (var shelf in shelves)
            {
                if (VideoController.IsInCooldown)
                {
                    Log.Warn("[YouTube] Home loading interrupted by bot detection");
                    break;
                }

                var section = new HomeSection { Title = shelf.Title };

                for (int i = 0; i < shelf.Items.Count; i++)
                {
                    var item = shelf.Items[i];

                    string thumbUrl = ThumbnailUtils.GetBestUrlOrDefault(item.Thumbnails, $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg");

                    bool isMusicContent = string.Equals(item.Type, "Song", StringComparison.OrdinalIgnoreCase);

                    var track = new TrackInfo
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = thumbUrl,
                        Duration = item.Duration ?? TimeSpan.Zero,
                        IsMusic = isMusicContent,
                        Url = $"https://music.youtube.com/watch?v={item.Id}"
                    };

                    if (item.Type is "Playlist" or "Album")
                        track.Id = $"yt_pl_{item.Id}";
                    else
                        track = _trackRegistry.RegisterOrUpdate(track);

                    section.Tracks.Add(track);
                }

                if (section.Tracks.Count > 0)
                    sections.Add(section);
            }

            return sections;
        }
        catch (BotDetectionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to get home: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Edit youtube

    public async Task LikeTrackAsync(string trackId, bool like)
    {
        if (AuthService?.IsAuthenticated != true) return;
        try
        {
            var rawId = ExtractRawIdSpan(trackId).ToString();
            await _youtube.Music.LikeTrackAsync(rawId, like);
            Log.Info($"[Music] Like={like} for {rawId}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw;
        }
    }

    public async Task<string?> CreatePlaylistAsync(
        string title,
        IReadOnlyList<string>? videoIds = null)
    {
        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException(
                "Cannot create cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            var ytId = await _youtube.Mutations.CreatePlaylistAsync(title, videoIds);
            Log.Info($"[Music] Created playlist '{title}' → YT ID: {ytId}");
            return ytId;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist '{title}': {ex.Message}");
            throw;
        }
    }

    public async Task<string?> AddToPlaylistAsync(string playlistId, string trackId)
    {
        if (AuthService?.IsAuthenticated != true) return null;
        try
        {
            var rawId = ExtractRawIdSpan(trackId).ToString();
            var setVideoIds = await _youtube.Mutations.AddTracksAsync(playlistId, [rawId]);
            var result = setVideoIds.Count > 0 ? setVideoIds[0] : null;
            if (!string.IsNullOrEmpty(result))
                Log.Debug($"[Music] Added {rawId} to {playlistId}, setVideoId={result}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to add to playlist: {ex.Message}");
            return null;
        }
    }

    public async Task<List<string?>> AddTracksToPlaylistAsync(
        string playlistId, IReadOnlyList<string> trackIds)
    {
        if (AuthService?.IsAuthenticated != true) return [];

        ThrowIfInCooldown();

        try
        {
            var rawIds = new List<string>(trackIds.Count);
            for (int i = 0; i < trackIds.Count; i++)
                rawIds.Add(ExtractRawIdSpan(trackIds[i]).ToString());

            var result = await _youtube.Mutations.AddTracksAsync(playlistId, rawIds);
            Log.Info($"[Music] Batch added {trackIds.Count} tracks to {playlistId}");
            return result;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to batch add to playlist: {ex.Message}");
            return [];
        }
    }

    public async Task RemoveFromPlaylistAsync(string playlistId, string setVideoId)
    {
        if (AuthService?.IsAuthenticated != true) return;

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RemoveTracksAsync(playlistId, [setVideoId]);
            Log.Info($"[Music] Removed item {setVideoId} from playlist {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to remove from playlist: {ex.Message}");
            throw;
        }
    }

    public async Task RemoveTracksFromPlaylistAsync(
        string playlistId, IReadOnlyList<string> setVideoIds)
    {
        if (AuthService?.IsAuthenticated != true) return;

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RemoveTracksAsync(playlistId, setVideoIds);
            Log.Info($"[Music] Batch removed {setVideoIds.Count} tracks from {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to batch remove from playlist: {ex.Message}");
            throw;
        }
    }

    public async Task EditPlaylistDescriptionAsync(string playlistId, string description)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException(
                "Cannot edit cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.SetPlaylistDescriptionAsync(playlistId, description);
            Log.Info($"[Music] Updated description for playlist {playlistId}");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to update playlist description {playlistId}: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> UploadPlaylistThumbnailAsync(
        string playlistId,
        byte[] imageData,
        string mimeType = "image/jpeg")
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException(
                "Cannot upload thumbnail: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            var result = await _youtube.Mutations.UploadPlaylistThumbnailAsync(
                playlistId, imageData, mimeType);

            if (result)
            {
                Log.Info($"[Music] Thumbnail uploaded for playlist {playlistId}");
            }

            return result;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to upload thumbnail for playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    public async Task RenamePlaylistAsync(string playlistId, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException(
                "Cannot rename cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.RenamePlaylistAsync(playlistId, newTitle);
            Log.Info($"[Music] Renamed playlist {playlistId} → '{newTitle}'");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to rename playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist ID cannot be empty.", nameof(playlistId));

        if (AuthService?.IsAuthenticated != true)
            throw new InvalidOperationException(
                "Cannot delete cloud playlist: user is not authenticated.");

        ThrowIfInCooldown();

        try
        {
            await _youtube.Mutations.DeletePlaylistAsync(playlistId);
            Log.Info($"[Music] Deleted playlist {playlistId} from YouTube");
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to delete playlist {playlistId}: {ex.Message}");
            throw;
        }
    }

    public async Task<FullPlaylistSyncData?> GetFullPlaylistDataAsync(
        string youtubePlaylistId,
        CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true) return null;

        ThrowIfInCooldown();

        try
        {
            var data = await _youtube.Sync.GetFullPlaylistDataAsync(youtubePlaylistId, ct);

            Log.Info($"[Music] Fetched full playlist data: " +
                     $"'{data.Title}', {data.Tracks.Count} tracks for {youtubePlaylistId}");

            return data;
        }
        catch (BotDetectionException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to fetch full playlist data: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Выполняет аварийный сброс и очистку кэшей обходных движков при обнаружении фатальной ошибки 403 Forbidden.
    /// Предотвращает циклические блокировки при ротации шифров YouTube.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void HandlePlayback403Fatal()
    {
        Log.Warn("[YouTube] Fatal 403 Forbidden detected. Triggering self-healing bypass reset...");
        try
        {
            _nTokenDecryptor.InvalidateCache();
            _sigCipherDecryptor.InvalidateCache();
            _nTokenDecryptor.PlayerManager.InvalidateContext();
            _youtube.Videos.Streams.InvalidateCipherManifest();
            _poTokenProvider?.Invalidate();
            _ = YoutubeClientUtils.EnsureVisitorDataAsync(forceRefresh: true);
            ClearCache();
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] Self-healing reset failed: {ex.Message}");
        }
    }

    #endregion

    public async Task<(string Url, long Size, int Bitrate, string Codec, string Container)?> RefreshStreamUrlAsync(
            TrackInfo track,
            bool forceRefresh = false,
            CancellationToken ct = default)
    {
        var videoId = track.GetRawIdSpan().ToString();
        if (string.IsNullOrEmpty(videoId))
        {
            NotifyError("[YouTube] Could not extract video ID");
            return null;
        }

        var sw = Stopwatch.StartNew();

        // АВАРИЙНОЕ САМОВОССТАНОВЛЕНИЕ ПРИ 403
        if (forceRefresh)
        {
            Log.Warn($"[YouTube] [{videoId}] Force refresh requested (playback 403 recovery). Resetting bypass caches...");
            try
            {
                _nTokenDecryptor.InvalidateCache();
                _sigCipherDecryptor.InvalidateCache();
                // Мягкая инвалидация: НЕ удаляем base.js с диска.
                _nTokenDecryptor.PlayerManager.InvalidateContext();

                // Сброс stale STS при 403-recovery
                // _signatureTimestamp в VideoController и _cipherManifest в StreamClient
                // кэшируются на уровне экземпляра и НЕ инвалидировались ранее.
                // После PlayerManager.Invalidate() контекст будет загружен заново,
                // но оба поля вернут старый (потенциально пустой) STS из field-кэша.
                // InvalidateCipherManifest() сбрасывает оба поля через единую точку входа.
                _youtube.Videos.Streams.InvalidateCipherManifest();

                ClearCache();
                Log.Info($"[YouTube] [{videoId}] Cache purged, bypass engine ready for fresh compilation.");
            }
            catch (Exception ex)
            {
                Log.Error($"[YouTube] [{videoId}] Cache purge during force refresh failed: {ex.Message}");
            }
        }

        if (!forceRefresh)
        {
            var cached = AudioSourceFactory.FindAnyCachedTrack(videoId);
            if (cached != null)
            {
                Log.Info($"[YouTube] [{videoId}] Using fully cached track ({cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps)");
                track.StreamUrl = "";
                return ("", cached.Value.Entry.TotalSize,
                        cached.Value.Entry.Bitrate,
                        cached.Value.Entry.Codec.ToString(),
                        cached.Value.Entry.Format.ToString());
            }
        }

        if (track.IsHlsOnly)
        {
            if (!string.IsNullOrEmpty(track.HlsManifestUrl) && !forceRefresh)
            {
                NotifyStatus($"[YouTube] [{videoId}] Using cached HLS manifest");
                return (track.HlsManifestUrl, 0, 128, "HLS", "m3u8");
            }

            ThrowIfInCooldown();

            var freshHls = await GetHlsManifestAsync(videoId, ct);
            if (freshHls != null)
            {
                track.HlsManifestUrl = freshHls;
                return (freshHls, 0, 128, "HLS", "m3u8");
            }

            throw new StreamUnavailableException(
                $"HLS manifest unavailable for {videoId}",
                videoId,
                StreamUnavailableReason.AllClientsFailed,
                wasHlsFallback: true);
        }

        ThrowIfInCooldown();

        NotifyStatus(forceRefresh
            ? $"[YouTube] [{videoId}] Forcing URL refresh..."
            : $"[YouTube] [{videoId}] Getting stream URL...");

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

        string cacheKey = GenerateCacheKeyFromString(videoId, targetContainer, targetBitrate);

        if (!forceRefresh && TryGetFromCache(cacheKey, out var cachedUrl))
        {
            track.StreamUrl = cachedUrl.Url;
            NotifyStatus($"[YouTube] [{videoId}] Cached ({cachedUrl.Codec}/{cachedUrl.Bitrate}kbps)");
            return (cachedUrl.Url, cachedUrl.Size, cachedUrl.Bitrate, cachedUrl.Codec, cachedUrl.Container);
        }

        try
        {
            var vId = VideoId.Parse(videoId);

            try
            {
                var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
                var audioStreams = manifest.GetAudioOnlyStreams()
                    .OrderByDescending(s => s.Bitrate)
                    .ToList();

                if (audioStreams.Count > 0)
                {
                    var selectedStream = SelectBestStream(audioStreams, targetContainer, targetBitrate);
                    if (selectedStream != null)
                    {
                        var url = selectedStream.Url;
                        var size = selectedStream.Size.Bytes;
                        var bitrate = (int)Math.Round(selectedStream.Bitrate.KiloBitsPerSecond);
                        var container = selectedStream.Container.Name;
                        var codec = DetermineCodec(container, selectedStream);

                        sw.Stop();

                        NotifyStatus($"[YouTube] [{videoId}] Stream: {codec}/{bitrate}kbps in {sw.ElapsedMilliseconds}ms");

                        CacheStreamUrl(cacheKey, url, size, bitrate, codec, container);

                        track.StreamUrl = url;
                        track.TransientContainer = container;
                        track.TransientSize = size;
                        track.CachedCodec = codec;
                        track.CachedBitrate = bitrate;
                        track.CachedContainer = container;
                        track.IsHlsOnly = false;
                        track.HlsManifestUrl = null;

                        track.TrySetGainFromLoudness(selectedStream.LoudnessDb);

                        return (url, size, bitrate, codec, container);
                    }
                }
                else
                {
                    Log.Warn($"[YouTube] [{videoId}] No audio-only streams available");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BotDetectionException)
            {
                throw;
            }
            catch (VideoUnplayableException ex)
            {
                Log.Warn($"[YouTube] [{videoId}] Video unplayable: {ex.Message}");

                if (IsBotDetectionError(ex.Message))
                    throw new BotDetectionException(ex.Message, VideoController.GetRemainingCooldown());
            }
            catch (StreamUnavailableException ex) when (ex.HttpStatusCode == 403)
            {
                Log.Error($"[YouTube] [{videoId}] HTTP 403 Forbidden");
                HandlePlayback403Fatal();
                throw;
            }
            catch (StreamUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"[YouTube] [{videoId}] Stream manifest failed: {ex.Message}");
            }

            Log.Info($"[YouTube] [{videoId}] Falling back to HLS (IOS priority)...");

            try
            {
                var hlsUrl = await GetHlsManifestAsync(videoId, ct);

                if (!string.IsNullOrEmpty(hlsUrl))
                {
                    track.IsHlsOnly = true;
                    track.HlsManifestUrl = hlsUrl;
                    track.StreamUrl = hlsUrl;
                    track.TransientContainer = "m3u8";
                    track.CachedCodec = "HLS";
                    track.CachedBitrate = 128;
                    track.CachedContainer = "m3u8";

                    sw.Stop();
                    NotifyStatus($"[YouTube] [{videoId}] HLS-only track in {sw.ElapsedMilliseconds}ms");
                    Log.Warn($"[YouTube] [{videoId}] Track marked as HLS-only — normal streams unavailable");

                    return (hlsUrl, 0, 128, "HLS", "m3u8");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (StreamUnavailableException ex) when (ex.WasHlsFallback)
            {
                Log.Error($"[YouTube] [{videoId}] HLS fallback also failed with 403");
                HandlePlayback403Fatal(); // Сброс при ошибке HLS-манифеста
                throw;
            }
            catch (BotDetectionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"[YouTube] [{videoId}] HLS fallback failed: {ex.Message}");
            }

            Log.Error($"[YouTube] [{videoId}] No streams available (including HLS)");

            throw new StreamUnavailableException(
                $"Could not get any stream for video {videoId}",
                videoId,
                StreamUnavailableReason.AllClientsFailed);
        }
        catch (BotDetectionException)
        {
            throw;
        }
        catch (StreamUnavailableException)
        {
            throw;
        }
        catch (LoginRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] [{videoId}] Error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Проверяет, вызвана ли ошибка механизмами обнаружения ботов на стороне YouTube.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBotDetectionError(string error)
    {
        return VideoController.IsBotDetectionError(error);
    }

    private async Task<string?> GetHlsManifestAsync(string videoId, CancellationToken ct)
    {
        try
        {
            var vId = VideoId.Parse(videoId);
            var controller = new VideoController(_currentHttpClient!, _nTokenDecryptor.PlayerManager);
            return await controller.GetHlsManifestUrlAsync(vId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BotDetectionException)
        {
            throw;
        }
        catch (StreamUnavailableException)
        {
            throw;
        }
        catch (LoginRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] [{videoId}] GetHlsManifest failed: {ex.Message}");
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
            AudioOnlyStreamInfo? bestMatch = null;
            double bestDelta = double.MaxValue;
            AudioOnlyStreamInfo? firstInContainer = null;

            for (int i = 0; i < streams.Count; i++)
            {
                if (!streams[i].Container.Name.Equals(preferredContainer, StringComparison.OrdinalIgnoreCase))
                    continue;

                firstInContainer ??= streams[i];

                if (preferredBitrate > 0)
                {
                    var delta = Math.Abs(streams[i].Bitrate.KiloBitsPerSecond - preferredBitrate);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestMatch = streams[i];
                    }
                }
            }

            if (preferredBitrate > 0 && bestMatch != null) return bestMatch;
            if (firstInContainer != null) return firstInContainer;
        }

        var qualityPref = _libraryService?.Settings.QualityPreference ?? AudioQualityPreference.BestAvailable;

        if (qualityPref == AudioQualityPreference.Standard)
        {
            for (int i = 0; i < streams.Count; i++)
            {
                if (streams[i].Container.Name is "mp4" or "m4a")
                    return streams[i];
            }
        }

        return streams.Count > 0 ? streams[0] : null;
    }

    /// <summary>
    /// Выполняет точечный запрос к InnerTube API для получения только громкости видео.
    /// Использует ту же fallback-цепочку player-клиентов, что и основной playback path.
    /// </summary>
    /// <param name="videoId">Уникальный идентификатор видео.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Значение громкости в dB, либо <c>float.NaN</c> в случае ошибки.</returns>
    public async ValueTask<float> GetLoudnessDbOnlyAsync(string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return float.NaN;

        try
        {
            var vId = VideoId.Parse(videoId);
            var client = GetClient();
            var response = await client.Videos.GetPlayerResponseAsync(vId, ct).ConfigureAwait(false);
            float loudnessDb = response.LoudnessDb;

            if (float.IsFinite(loudnessDb))
                AudioSourceFactory.GlobalCache?.TryUpdateYoutubeLoudnessDb(videoId, loudnessDb);

            return loudnessDb;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"[YouTube] GetLoudnessDbOnlyAsync failed for {videoId}: {ex.Message}");
            return float.NaN;
        }
    }

    private static string DetermineCodec(string container, AudioOnlyStreamInfo stream)
    {
        var codecStr = stream.AudioCodec;

        if (!string.IsNullOrEmpty(codecStr))
        {
            var span = codecStr.AsSpan();
            if (span.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
            if (span.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (span.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (span.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        }

        return container switch
        {
            "webm" => "Opus",
            "mp4" or "m4a" => "AAC",
            _ => container.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Возвращает список доступных форматов стрима для указанного видео с поддержкой отмены операции.
    /// Исключает устаревшие HLS-манифесты и возвращает только физические аудиопотоки.
    /// </summary>
    /// <param name="videoId">Идентификатор видео YouTube.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Список доступных вариантов стрима.</returns>
    public async Task<List<StreamOption>> GetStreamOptionsAsync(string videoId, CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId)) return [];

        try
        {
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0) return [];

            var result = new List<StreamOption>(audioStreams.Count);
            for (int i = 0; i < audioStreams.Count; i++)
            {
                var s = audioStreams[i];
                result.Add(new StreamOption
                {
                    Container = s.Container.Name,
                    Bitrate = s.Bitrate.KiloBitsPerSecond,
                    Codec = DetermineCodec(s.Container.Name, s),
                    SizeMb = s.Size.MegaBytes
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetStreamOptions error: {ex.Message}");
            throw; // Пробрасываем для обработки во ViewModel
        }
    }

    #region Cache

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GenerateCacheKey(ReadOnlySpan<char> videoId, string? container, int bitrate = 0)
    {
        if (string.IsNullOrEmpty(container))
            return videoId.ToString();

        if (bitrate > 0)
        {
            var bitrateStr = bitrate.ToString();
            return string.Create(
                videoId.Length + 1 + container.Length + 1 + bitrateStr.Length,
                (videoId: videoId.ToString(), container, bitrateStr),
                static (span, state) =>
                {
                    int pos = 0;
                    state.videoId.AsSpan().CopyTo(span);
                    pos += state.videoId.Length;
                    span[pos++] = '_';
                    state.container.AsSpan().CopyTo(span[pos..]);
                    pos += state.container.Length;
                    span[pos++] = '_';
                    state.bitrateStr.AsSpan().CopyTo(span[pos..]);
                });
        }

        return string.Create(
            videoId.Length + 1 + container.Length,
            (videoId: videoId.ToString(), container),
            static (span, state) =>
            {
                int pos = 0;
                state.videoId.AsSpan().CopyTo(span);
                pos += state.videoId.Length;
                span[pos++] = '_';
                state.container.AsSpan().CopyTo(span[pos..]);
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetFromCache(string cacheKey, out StreamCacheEntry result)
    {
        if (_streamCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired(_streamCacheLifetime))
        {
            result = cached;
            return true;
        }

        if (!cached.Equals(default(StreamCacheEntry)))
            _streamCache.TryRemove(cacheKey, out _);

        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheStreamUrl(string cacheKey, string url, long size, int bitrate, string codec, string container)
    {
        _streamCache[cacheKey] = new StreamCacheEntry(url, size, bitrate, codec, container);

        if (_streamCache.Count > MaxCacheSize)
            CleanupExpiredCache();
    }

    private void CleanupExpiredCache()
    {
        foreach (var kvp in _streamCache)
        {
            if (kvp.Value.IsExpired(_streamCacheLifetime))
                _streamCache.TryRemove(kvp.Key, out _);
        }
    }

    public void ClearCache()
    {
        _streamCache.Clear();
        Log.Info("[YouTube] Stream cache cleared");
    }

    #endregion

    #region Search

    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> ExtractRawIdSpan(string trackId)
    {
        var span = trackId.AsSpan().Trim();
        if (span.StartsWith("yt_pl_".AsSpan()))
            return span[6..];
        if (span.StartsWith("yt_".AsSpan()))
            return span[3..];
        return span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidYoutubeIdChars(ReadOnlySpan<char> id)
    {
        for (int i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
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

    public async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
     string query,
     int maxResults = 300,
     SearchFilter filter = SearchFilter.MusicSong,
     [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            yield break;

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Search blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            yield break;
        }

        var sw = Stopwatch.StartNew();
        int count = 0;
        bool isMusicFilter = filter.IsMusicContext();

        NotifyStatus($"[YouTube] Streaming search '{query}' (Filter: {filter})...");

        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            if (VideoController.IsInCooldown)
            {
                Log.Warn("[YouTube] Search interrupted by bot detection cooldown");
                yield break;
            }

            var tracks = new List<TrackInfo>(batch.Items.Count);

            for (int i = 0; i < batch.Items.Count && count < maxResults; i++)
            {
                if (batch.Items[i] is not TrackInfo rawTrack) continue;

                if (isMusicFilter) rawTrack.IsMusic = true;

                var track = _trackRegistry.RegisterOrUpdate(rawTrack);
                tracks.Add(track);
                count++;
            }

            if (tracks.Count > 0)
            {
                NotifyStatus($"[YouTube] +{tracks.Count} (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        NotifyStatus($"[YouTube] Search done: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    public async Task<List<TrackInfo>> SearchFastAsync(
     string query,
     int maxResults = 100,
     SearchFilter filter = SearchFilter.MusicSong,
     CancellationToken ct = default)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Search blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        var results = new List<TrackInfo>(maxResults);

        try
        {
            await foreach (var batch in SearchStreamingAsync(query, maxResults, filter, ct))
            {
                results.AddRange(batch);
                if (results.Count >= maxResults) break;
            }
        }
        catch (OperationCanceledException)
        {
            NotifyStatus($"[YouTube] Search cancelled after {results.Count} results");
        }
        catch (BotDetectionException)
        {
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] SearchFastAsync error: {ex.Message}");
        }

        return results;
    }

    public Task<List<TrackInfo>> SearchAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.MusicSong)
    {
        return SearchFastAsync(query, maxResults, filter);
    }

    #endregion

    #region Search Session

    public sealed class SearchSession : IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly TrackRegistry _registry;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly HashSet<string> _seenIds;
        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private volatile bool _disposed;

        private readonly Queue<TrackInfo> _buffer = new();
        private readonly SemaphoreSlim _disposeLock = new(1, 1);

        public bool HasMore => (_hasMore || _buffer.Count > 0) && !_disposed && _seenIds.Count < _maxResults;
        public int LoadedCount => _seenIds.Count;
        public SearchFilter Filter { get; }

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
            Filter = filter;
            _seenIds = new HashSet<string>(64, StringComparer.Ordinal);

            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    var cleanId = id.AsSpan();
                    if (cleanId.StartsWith("yt_".AsSpan()))
                        cleanId = cleanId[3..];
                    _seenIds.Add(cleanId.ToString());
                }
            }
        }

        /// <summary>
        /// Получает следующий пакет результатов поиска.
        /// </summary>
        /// <param name="count">Размер пакета.</param>
        /// <param name="ct">Токен отмены операции.</param>
        /// <returns>Список найденных треков.</returns>
        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 50, CancellationToken ct = default)
        {
            if (_disposed || _seenIds.Count >= _maxResults) return [];

            var results = new List<TrackInfo>(count);

            while (_buffer.Count > 0 && results.Count < count)
            {
                results.Add(_buffer.Dequeue());
            }

            while (results.Count < count && _hasMore && _seenIds.Count < _maxResults)
            {
                try
                {
                    _enumerator ??= _youtube.Search
                        .GetResultBatchesAsync(_query, Filter, ct)
                        .GetAsyncEnumerator(ct);

                    if (!await _enumerator.MoveNextAsync())
                    {
                        _hasMore = false;
                        break;
                    }

                    var batch = _enumerator.Current;

                    for (int i = 0; i < batch.Items.Count; i++)
                    {
                        if (_seenIds.Count >= _maxResults) break;

                        if (batch.Items[i] is TrackInfo tInfo)
                        {
                            var rawIdSpan = tInfo.GetRawIdSpan();
                            var rawId = rawIdSpan.ToString();

                            if (!_seenIds.Add(rawId)) continue;

                            var track = _registry.RegisterOrUpdate(tInfo);

                            if (results.Count < count)
                                results.Add(track);
                            else
                                _buffer.Enqueue(track);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Не замалчиваем отмену задачи. Пробрасываем исключение 
                    // для корректного завершения асинхронного конвейера.
                    throw;
                }
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

                if (_enumerator != null)
                {
                    try { _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception ex) { Log.Warn($"[SearchSession] Dispose error: {ex.Message}"); }
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

    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, _trackRegistry, query, maxResults, filter, skipTrackIds);
        NotifyStatus($"[YouTube] Search session: '{query}' (max:{maxResults}, filter:{filter})");
        return _currentSearchSession;
    }

    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 50,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.MusicSong,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return ([], null!);

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults, filter);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct);

        sw.Stop();
        NotifyStatus($"[YouTube] Initial '{query}': {tracks.Count} in {sw.ElapsedMilliseconds}ms (Filter: {filter})");

        return (tracks, session);
    }

    #endregion

    #region Playlist, Channel, Radio, Download

    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Playlist blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var playlistId = PlaylistId.TryParse(url);
            if (playlistId == null) return null;

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            var tracks = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            for (int i = 0; i < tracks.Count; i++)
                _trackRegistry.RegisterOrUpdate(tracks[i]);

            NotifyStatus($"[YouTube] Playlist '{playlist.Name}': {tracks.Count} tracks");
            return (playlist.Name, tracks.ToList());
        }
        catch (BotDetectionException)
        {
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<(string ChannelName, List<PlaylistSearchResult> Playlists)?> GetChannelPlaylistsForSyncAsync(
     string channelUrl, CancellationToken ct = default)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Sync blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        var channel = await GetChannelFromUrlAsync(channelUrl, ct);
        if (channel is null) return null;

        NotifyStatus($"[YouTube] Fetching playlists from: {channel.Title}...");

        try
        {
            var results = new List<PlaylistSearchResult>();

            await foreach (var pl in _youtube.Channels.GetPlaylistsAsync(channel.Id, ct))
            {
                if (VideoController.IsInCooldown)
                {
                    Log.Warn("[YouTube] Channel sync interrupted by bot detection");
                    break;
                }

                if (pl.Name.Equals("Uploads", StringComparison.OrdinalIgnoreCase)) continue;

                var thumbs = new List<Thumbnail>();
                if (!string.IsNullOrEmpty(pl.ThumbnailUrl))
                    thumbs.Add(new Thumbnail(pl.ThumbnailUrl, new Resolution(0, 0)));

                var auth = pl.Author != null
                    ? new Author(new ChannelId(channel.Id.Value), pl.Author)
                    : null;

                results.Add(new PlaylistSearchResult(
                    new PlaylistId(pl.YoutubeId ?? ""),
                    pl.Name, auth, thumbs));
            }

            NotifyStatus($"[YouTube] Found {results.Count} playlists.");
            return (channel.Title, results);
        }
        catch (BotDetectionException)
        {
            return (channel.Title, []);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error parsing channel playlists: {ex.Message}");
            return (channel.Title, []);
        }
    }

    public async Task<List<Playlist>> GetUserPlaylistsByAuthAsync()
    {
        return await _userDataService.GetMyPlaylistsAsync();
    }

    /// <summary>
    /// Импортирует плейлист: загружает метаданные и все треки единым запросом,
    /// определяет ownership и visibility, сохраняет треки в БД.
    /// </summary>
    public async Task<Playlist?> ImportPlaylistAsync(
        string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Import blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var plId = new PlaylistId(playlistId);

            var result = await _youtube.Playlists.ImportAsync(plId, ct).ConfigureAwait(false);

            var playlist = result.Playlist;
            playlist.SyncMode = isAccountSync
                ? PlaylistSyncMode.TwoWaySync
                : PlaylistSyncMode.CloudPublic;

            // Ownership & Visibility
            ResolveOwnershipAndVisibility(playlist, isAccountSync);

            var tracks = result.Tracks;
            if (_libraryService != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _libraryService.AddOrUpdateTrackAsync(tracks[i], ct).ConfigureAwait(false);
                    playlist.TrackIds.Add(tracks[i].Id);
                }
            }
            else
            {
                for (int i = 0; i < tracks.Count; i++)
                    playlist.TrackIds.Add(tracks[i].Id);
            }

            Log.Info($"[YouTube] ImportPlaylist '{playlistId}': {tracks.Count} tracks, " +
                     $"Ownership={playlist.Ownership}, Visibility={playlist.Visibility}");
            return playlist;
        }
        catch (BotDetectionException) { return null; }
        catch (OperationCanceledException) { throw; }
        catch (PlaylistUnavailableException ex)
        {
            Log.Warn($"[YouTube] Playlist '{playlistId}' unavailable: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error importing playlist '{playlistId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Определяет ownership и задаёт fallback-visibility, если YouTube не отдал privacy явно.
    /// </summary>
    private void ResolveOwnershipAndVisibility(Playlist playlist, bool isAccountSync)
    {
        if (!isAccountSync)
        {
            playlist.Ownership = PlaylistOwnership.Foreign;

            // URL-импорт: если privacy не удалось получить из API, считаем минимум Public
            if (playlist.Visibility == PlaylistVisibility.Unknown)
                playlist.Visibility = PlaylistVisibility.Public;

            return;
        }

        // Account sync: определяем ownership сравнением Author с UserName
        if (AuthService?.IsAuthenticated != true)
        {
            playlist.Ownership = PlaylistOwnership.Unknown;
            return;
        }

        var userName = AuthService.State.UserName;
        var playlistAuthor = playlist.Author;

        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(playlistAuthor))
        {
            playlist.Ownership = PlaylistOwnership.Unknown;
            return;
        }

        playlist.Ownership = playlistAuthor.Equals(userName, StringComparison.OrdinalIgnoreCase)
            ? PlaylistOwnership.Mine
            : PlaylistOwnership.Foreign;

        // Для чужих плейлистов из библиотеки fallback на Public только если privacy не распознали
        if (playlist.IsForeign && playlist.Visibility == PlaylistVisibility.Unknown)
            playlist.Visibility = PlaylistVisibility.Public;
    }

    public async Task<(string Name, string AvatarUrl)?> GetChannelInfoAsync(
        string url, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(url, ct);
        if (channel == null) return null;

        string avatar = "";
        int maxWidth = 0;
        for (int i = 0; i < channel.Thumbnails.Count; i++)
        {
            if (channel.Thumbnails[i].Resolution.Width > maxWidth)
            {
                maxWidth = channel.Thumbnails[i].Resolution.Width;
                avatar = channel.Thumbnails[i].Url ?? "";
            }
        }

        return (channel.Title, avatar);
    }

    private async Task<Channel?> GetChannelFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var span = url.AsSpan();

            if (TryExtractSegment(span, "/channel/", out var channelId))
                return await _youtube.Channels.GetAsync(new ChannelId(channelId), ct);

            if (TryExtractSegment(span, "/@", out var handle))
                return await _youtube.Channels.GetByHandleAsync(new ChannelHandle(handle), ct);

            if (TryExtractSegment(span, "/c/", out var slug))
                return await _youtube.Channels.GetBySlugAsync(new ChannelSlug(slug), ct);

            if (TryExtractSegment(span, "/user/", out var user))
                return await _youtube.Channels.GetByUserAsync(new UserName(user), ct);

            NotifyError("[YouTube] Unrecognized channel URL format.");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error getting channel info: {ex.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryExtractSegment(ReadOnlySpan<char> url, ReadOnlySpan<char> pattern, out string result)
    {
        int idx = url.IndexOf(pattern);
        if (idx < 0)
        {
            result = "";
            return false;
        }

        var after = url[(idx + pattern.Length)..];

        int endSlash = after.IndexOf('/');
        int endQuery = after.IndexOf('?');

        int end = (endSlash, endQuery) switch
        {
            ( >= 0, >= 0) => Math.Min(endSlash, endQuery),
            ( >= 0, _) => endSlash,
            (_, >= 0) => endQuery,
            _ => after.Length
        };

        result = after[..end].ToString();
        return result.Length > 0;
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url))
            return [];

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Radio blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        try
        {
            var videoIdSpan = sourceTrack.GetRawIdSpan();
            if (videoIdSpan.IsEmpty) return [];

            var videoId = videoIdSpan.ToString();
            var mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";

            var result = await GetPlaylistAsync(mixUrl);
            if (result == null) return [];

            var tracks = result.Value.Tracks;
            int take = Math.Min(count, tracks.Count);
            var output = new List<TrackInfo>(take);

            for (int i = 0; i < take; i++)
            {
                tracks[i].RadioSeedId = sourceTrack.Id;
                output.Add(tracks[i]);
            }

            return output;
        }
        catch (BotDetectionException)
        {
            return [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Trending blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        try
        {
            var url = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(url);

            if (result != null)
            {
                var tracks = result.Value.Tracks;
                int take = Math.Min(count, tracks.Count);
                return tracks.GetRange(0, take);
            }

            return await SearchAsync("top music 2024", count);
        }
        catch (BotDetectionException)
        {
            return [];
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
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Download blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return null;
        }

        try
        {
            var videoIdSpan = track.GetRawIdSpan();
            if (videoIdSpan.IsEmpty) return null;

            var vId = VideoId.Parse(videoIdSpan.ToString());
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(G.Folder.Downloads, fileName);

            var prog = progress != null
                ? new Progress<double>(p => progress.Report((float)p))
                : null;

            await _youtube.Videos.Streams.DownloadAsync(stream, filePath, progress: prog, cancellationToken: ct);
            NotifyStatus($"[YouTube] Downloaded: {fileName}");

            var cacheManager = AudioSourceFactory.GlobalCache;
            if (cacheManager != null)
            {
                var format = AudioSourceFactory.DetectFormat(stream.Url);
                if (format == AudioFormat.Unknown)
                {
                    format = stream.Container.Name switch
                    {
                        "webm" => AudioFormat.WebM,
                        "mp4" or "m4a" => AudioFormat.Mp4,
                        "ogg" => AudioFormat.Ogg,
                        _ => AudioFormat.Unknown
                    };
                }

                if (format != AudioFormat.Unknown)
                {
                    int bitrate = (int)Math.Round(stream.Bitrate.KiloBitsPerSecond);
                    string capturedFilePath = filePath;
                    string capturedTrackId = track.Id;

                    _ = Task.Run(async () =>
                    {
                        await cacheManager.ResumeCacheFromDownloadedFileAsync(
                            capturedTrackId, capturedFilePath, format, bitrate,
                            startChunkHint: 0,
                            ct: CancellationToken.None);
                    });
                }
            }

            return filePath;
        }
        catch (BotDetectionException)
        {
            return null;
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

        Span<char> buffer = name.Length <= 256
            ? stackalloc char[name.Length]
            : new char[name.Length];

        int pos = 0;
        for (int i = 0; i < name.Length && pos < 200; i++)
        {
            var c = name[i];
            if (Array.IndexOf(invalid, c) < 0)
                buffer[pos++] = c;
        }

        return new string(buffer[..pos]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyStatus(string message)
    {
        Log.Info(message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyError(string message)
    {
        Log.Error(message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GenerateCacheKeyFromString(string videoId, string? container, int bitrate = 0)
    {
        return GenerateCacheKey(videoId.AsSpan(), container, bitrate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AuthService?.OnAuthStateChanged -= ReloadClient;
        DisposeCurrentClient();

        _currentHandler?.Dispose();
        _currentHandler = null;

        _poTokenProvider?.Dispose();
        _poTokenProvider = null;

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

public sealed partial class StreamOption : ReactiveObject
{
    public string Container { get; set; } = "";
    public double Bitrate { get; set; }
    public string Codec { get; set; } = "";
    public double SizeMb { get; set; }

    public string DisplayName => $"{Codec} {string.Format(LocalizationService.Instance.Get("Stream_Bitrate"), Bitrate)} ({Container})";

    public string SizeMbFormatted => string.Format(
        LocalizationService.Instance.Get("Stream_Format_Mb", "{0:F1} MB"),
        SizeMb);

    [Reactive] public partial bool IsDownloaded { get; set; }
    [Reactive] public partial bool IsActive { get; set; }
}

public sealed class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}
