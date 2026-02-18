using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LMP.Core.Models;
using LMP.Core.Youtube;
using LMP.Core.Youtube.Channels;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Audio;

namespace LMP.Core.Services;

public partial class YoutubeProvider : IDisposable
{
    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    private readonly TrackRegistry _trackRegistry;
    public readonly CookieAuthService? AuthService;
    private readonly LibraryService? _libraryService;
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);

    // struct вместо class для StreamCacheEntry
    private readonly ConcurrentDictionary<string, StreamCacheEntry> _streamCache =
        new(StringComparer.Ordinal);

    private YoutubeClient _youtube = null!;

    // храним handler отдельно для переиспользования
    private SocketsHttpHandler? _currentHandler;
    private HttpClient? _currentHttpClient;
    private volatile bool _disposed;

    /// <summary>
    /// readonly struct вместо class — zero heap allocation.
    /// </summary>
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

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    public YoutubeProvider(TrackRegistry trackRegistry, LibraryService? libraryService, CookieAuthService? cookieAuth)
    {
        _trackRegistry = trackRegistry;
        _libraryService = libraryService;
        AuthService = cookieAuth;

        if (AuthService != null)
        {
            ReloadClient();
            AuthService.OnAuthStateChanged += ReloadClient;
        }

        VideoController.OnBotDetectionCooldown += HandleBotDetectionCooldown;
        VideoController.OnStreamUnavailable += HandleStreamUnavailable;
    }

    #region Bot Detection Handling

    /// <summary>
    /// Проверяет можно ли воспроизвести трек без запросов к YouTube.
    /// </summary>
    public bool CanPlayOffline(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Id))
            return false;

        // Извлекаем сырой ID
        var rawId = track.GetRawIdSpan().ToString();
        if (string.IsNullOrEmpty(rawId))
            return false;

        // Проверяем кэш любого формата/битрейта
        var cached = AudioSourceFactory.FindAnyCachedTrack(rawId);
        return cached != null;
    }

    /// <summary>
    /// Проверяет можно ли выполнить сетевую операцию.
    /// Возвращает true если можно, false если в cooldown.
    /// </summary>
    public static bool CanPerformNetworkOperation()
    {
        return !VideoController.IsInCooldown;
    }

    /// <summary>
    /// Выбрасывает BotDetectionException если в cooldown.
    /// </summary>
    /// <exception cref="BotDetectionException">Если YouTube rate limiting активен.</exception>
    public static void ThrowIfInCooldown()
    {
        if (VideoController.IsInCooldown)
        {
            var remaining = VideoController.GetRemainingCooldown();
            throw new BotDetectionException(
                $"Rate limited by YouTube. Please wait {remaining.TotalSeconds:F0} seconds.",
                remaining);
        }
    }

    private void HandleBotDetectionCooldown(TimeSpan waitTime)
    {
        Log.Warn($"[YouTube] Bot detection cooldown: {waitTime.TotalSeconds:F0}s");

        // Показываем диалог через DialogService
        var dialogService = Program.Services.GetService<IDialogService>();
        if (dialogService != null)
        {
            // Fire-and-forget — диалог показывается async
            _ = dialogService.ShowBotDetectionCooldownAsync(waitTime);
        }
    }

    private void HandleStreamUnavailable(StreamUnavailableException exception)
    {
        Log.Error($"[YouTube] Stream unavailable: {exception.Reason} for {exception.VideoId}");

        var dialogService = Program.Services.GetService<IDialogService>();
        if (dialogService != null)
        {
            _ = dialogService.ShowStreamUnavailableAsync(exception);
        }
    }

    #endregion

    /// <summary>
    /// переиспользуем SocketsHttpHandler вместо создания нового каждый раз.
    /// </summary>
    public void ReloadClient()
    {
        DisposeCurrentClient();

        // Создаем SocketsHttpHandler с connection pooling
        _currentHandler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,

            // CONNECTION POOLING — критичная оптимизация
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 20,

            // HTTP/2 оптимизация
            EnableMultipleHttp2Connections = true,

            // Keep-alive
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),

            // Timeouts
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        // создаем базовый HttpClient с нашим handler
        var baseHttpClient = new HttpClient(_currentHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // YoutubeHttpHandler оборачивает HttpClient
        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, AuthService, disposeClient: true);

        // Финальный HttpClient с YoutubeHttpHandler
        _currentHttpClient = new HttpClient(youtubeHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _youtube = new YoutubeClient(_currentHttpClient, ownsHttpClient: false);

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

    public async Task InitializeAsync()
    {
        if (AuthService?.IsAuthenticated == true)
        {
            Log.Info("[YouTube] Fetching fresh Visitor Data for auth session...");
            var visitorData = await FetchVisitorDataAsync();

            _youtube.Music.SetVisitorData(
                !string.IsNullOrEmpty(visitorData)
                    ? visitorData
                    : "CgtsZG1ySnZiQWtSbyiMjuGSBg%3D%3D");

            if (!string.IsNullOrEmpty(visitorData))
                Log.Info($"[YouTube] Visitor Data synchronized: {visitorData}");
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

            if (AuthService != null)
                client.DefaultRequestHeaders.Add("Cookie", AuthService.GetCookieHeader());

            var jsonStr = await client.GetStringAsync("https://music.youtube.com/sw.js_data");

            if (jsonStr.StartsWith(")]}'"))
                jsonStr = jsonStr[4..];

            var json = Json.Parse(jsonStr);
            return json[0][2][0][0][13].GetStringOrNull();
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] Failed to fetch sw.js_data: {ex.Message}");
            return null;
        }
    }

    public YoutubeClient GetClient() =>
        _youtube ?? throw new InvalidOperationException("YouTube client not initialized");

    #region Персонализация

    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (AuthService?.IsAuthenticated != true) return [];

        // ─── Проверка cooldown ───
        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] Home blocked: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            return [];
        }

        try
        {
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);

            var sections = new List<HomeSection>(shelves.Count);

            foreach (var shelf in shelves)
            {
                // Проверяем cooldown между обработкой секций
                if (VideoController.IsInCooldown)
                {
                    Log.Warn("[YouTube] Home loading interrupted by bot detection");
                    break;
                }

                var section = new HomeSection { Title = shelf.Title };

                for (int i = 0; i < shelf.Items.Count; i++)
                {
                    var item = shelf.Items[i];

                    string thumbUrl = GetBestThumbnailUrl(item.Thumbnails)
                        ?? $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg";

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
                    {
                        track.Id = $"yt_pl_{item.Id}";
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
        catch (BotDetectionException)
        {
            return [];
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to get home: {ex.Message}");
            return [];
        }
    }

    #endregion

    #region Лайки и плейлисты

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

    public async Task<string?> CreatePlaylistAsync(string title)
    {
        if (AuthService?.IsAuthenticated != true) return null;
        try
        {
            return await _youtube.Music.CreatePlaylistAsync(title);
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    public async Task AddToPlaylistAsync(string playlistId, string trackId)
    {
        if (AuthService?.IsAuthenticated != true) return;
        try
        {
            var rawId = ExtractRawIdSpan(trackId).ToString();
            await _youtube.Music.AddToPlaylistAsync(playlistId, rawId);
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to add to playlist: {ex.Message}");
        }
    }

    #endregion

    #region RefreshStreamUrlAsync

    /// <summary>
    /// Получает URL аудио-потока для трека.
    /// Для заблокированных треков использует HLS через IOS.
    /// </summary>
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

        // ══════════════════════════════════════════════════════════════════
        // ПРОВЕРКА КЭША — ПРИОРИТЕТ №1 (работает даже в cooldown)
        // ══════════════════════════════════════════════════════════════════

        if (!forceRefresh)
        {
            var cached = AudioSourceFactory.FindAnyCachedTrack(videoId);
            if (cached != null)
            {
                Log.Info($"[YouTube] [{videoId}] Using fully cached track ({cached.Value.Entry.Format}/{cached.Value.Entry.Bitrate}kbps)");
                track.StreamUrl = ""; // Пустой URL = AudioPlayer использует кэш
                return ("", cached.Value.Entry.TotalSize,
                        cached.Value.Entry.Bitrate,
                        cached.Value.Entry.Codec.ToString(),
                        cached.Value.Entry.Format.ToString());
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Если трек помечен как HLS-only — сразу возвращаем HLS
        // ══════════════════════════════════════════════════════════════════

        if (track.IsHlsOnly)
        {
            if (!string.IsNullOrEmpty(track.HlsManifestUrl) && !forceRefresh)
            {
                NotifyStatus($"[YouTube] [{videoId}] Using cached HLS manifest");
                return (track.HlsManifestUrl, 0, 128, "HLS", "m3u8");
            }

            // Проверяем cooldown перед сетевым запросом
            try
            {
                ThrowIfInCooldown();
            }
            catch (BotDetectionException ex)
            {
                NotifyError($"[YouTube] [{videoId}] Rate limited: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
                return null;
            }

            var freshHls = await GetHlsManifestAsync(videoId, ct);
            if (freshHls != null)
            {
                track.HlsManifestUrl = freshHls;
                return (freshHls, 0, 128, "HLS", "m3u8");
            }

            // HLS не доступен — показываем ошибку
            var hlsException = new StreamUnavailableException(
                $"HLS manifest unavailable for {videoId}",
                videoId,
                StreamUnavailableReason.AllClientsFailed,
                wasHlsFallback: true);

            NotifyStreamError(hlsException);
            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        // ПРОВЕРКА BOT DETECTION — ПЕРЕД СЕТЕВЫМИ ЗАПРОСАМИ
        // ══════════════════════════════════════════════════════════════════

        try
        {
            ThrowIfInCooldown();
        }
        catch (BotDetectionException ex)
        {
            NotifyError($"[YouTube] [{videoId}] Rate limited: wait {ex.RemainingCooldown.TotalSeconds:F0}s");
            // Диалог уже показан через HandleBotDetectionCooldown
            return null;
        }

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

        // Проверяем кэш URL
        if (!forceRefresh && TryGetFromCache(cacheKey, out var cachedUrl))
        {
            track.StreamUrl = cachedUrl.Url;
            NotifyStatus($"[YouTube] [{videoId}] Cached ({cachedUrl.Codec}/{cachedUrl.Bitrate}kbps)");
            return (cachedUrl.Url, cachedUrl.Size, cachedUrl.Bitrate, cachedUrl.Codec, cachedUrl.Container);
        }

        try
        {
            var vId = VideoId.Parse(videoId);

            // STEP 1: Пробуем получить обычные audio-only стримы

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
                        var bitrate = (int)selectedStream.Bitrate.KiloBitsPerSecond;
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

                        return (url, size, bitrate, codec, container);
                    }
                }

                Log.Warn($"[YouTube] [{videoId}] No audio-only streams available");
            }
            catch (BotDetectionException)
            {
                // Пробрасываем bot detection — диалог уже показан
                throw;
            }
            catch (VideoUnplayableException ex)
            {
                Log.Warn($"[YouTube] [{videoId}] Video unplayable: {ex.Message}");

                // Проверяем, не bot detection ли это
                if (IsBotDetectionError(ex.Message))
                {
                    // Bot detection — не пробуем HLS, просто выходим
                    return null;
                }
            }
            catch (StreamUnavailableException ex) when (ex.HttpStatusCode == 403)
            {
                Log.Error($"[YouTube] [{videoId}] HTTP 403 Forbidden");
                NotifyStreamError(ex);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"[YouTube] [{videoId}] Stream manifest failed: {ex.Message}");
            }

            // STEP 2: HLS Fallback (IOS в приоритете!)

            Log.Info($"[YouTube] [{videoId}] Falling back to HLS (IOS priority)...");

            try
            {
                var hlsUrl = await GetHlsManifestAsync(videoId, ct);

                if (!string.IsNullOrEmpty(hlsUrl))
                {
                    // Помечаем трек как HLS-only
                    track.IsHlsOnly = true;
                    track.HlsManifestUrl = hlsUrl;
                    track.StreamUrl = hlsUrl;
                    track.TransientContainer = "m3u8";
                    track.CachedCodec = "HLS";
                    track.CachedBitrate = 128;
                    track.CachedContainer = "m3u8";

                    sw.Stop();
                    NotifyStatus($"[YouTube] [{videoId}] ⚠️ HLS-only track in {sw.ElapsedMilliseconds}ms");
                    Log.Warn($"[YouTube] [{videoId}] Track marked as HLS-only — normal streams unavailable");

                    return (hlsUrl, 0, 128, "HLS", "m3u8");
                }
            }
            catch (StreamUnavailableException ex) when (ex.WasHlsFallback)
            {
                // HLS тоже 403 — показываем специальную ошибку
                Log.Error($"[YouTube] [{videoId}] HLS fallback also failed with 403");
                NotifyStreamError(ex);
                return null;
            }
            catch (BotDetectionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn($"[YouTube] [{videoId}] HLS fallback failed: {ex.Message}");
            }

            // ВСЕ МЕТОДЫ ПРОВАЛИЛИСЬ

            Log.Error($"[YouTube] [{videoId}] No streams available (including HLS)");

            var allFailedException = new StreamUnavailableException(
                $"Could not get any stream for video {videoId}",
                videoId,
                StreamUnavailableReason.AllClientsFailed);

            NotifyStreamError(allFailedException);
            return null;
        }
        catch (BotDetectionException)
        {
            // Уже обработано выше
            return null;
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

    private static void NotifyStreamError(StreamUnavailableException exception)
    {
        Log.Error($"[YouTube] Stream error: {exception.Message}");

        var dialogService = Program.Services.GetService<IDialogService>();
        if (dialogService != null)
        {
            _ = dialogService.ShowStreamUnavailableAsync(exception);
        }
    }

    private static bool IsBotDetectionError(string error)
    {
        return error.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Выполните вход", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Войдите", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Получает HLS манифест URL (IOS в приоритете).
    /// </summary>
    private async Task<string?> GetHlsManifestAsync(string videoId, CancellationToken ct)
    {
        try
        {
            var vId = VideoId.Parse(videoId);

            // Используем VideoController для получения HLS
            var controller = new VideoController(_currentHttpClient!);
            return await controller.GetHlsManifestUrlAsync(vId, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] [{videoId}] GetHlsManifest failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Получает HLS манифест для видео.
    /// </summary>
    private async Task<(string Url, long Size, int Bitrate, string Codec, string Container)?> TryGetHlsStreamAsync(
        VideoId videoId, CancellationToken ct)
    {
        try
        {
            Log.Debug($"[YouTube] [{videoId}] Trying HLS...");

            // Получаем PlayerResponse с HLS URL
            var playerResponse = await _youtube.GetPlayerResponseAsync(videoId, ct);
            var hlsUrl = playerResponse.HlsManifestUrl;

            if (string.IsNullOrEmpty(hlsUrl))
            {
                Log.Debug($"[YouTube] [{videoId}] No HLS manifest available");
                return null;
            }

            Log.Info($"[YouTube] [{videoId}] ✓ HLS manifest found");

            // Для HLS размер неизвестен заранее, ставим 0
            // Bitrate примерный для аудио
            return (hlsUrl, 0, 128, "HLS", "m3u8");
        }
        catch (Exception ex)
        {
            Log.Warn($"[YouTube] [{videoId}] HLS lookup failed: {ex.Message}");
            return null;
        }
    }

    private AudioOnlyStreamInfo? SelectBestStream(
        List<AudioOnlyStreamInfo> streams,
        string? preferredContainer,
        int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        // Если указан предпочтительный контейнер — ищем его
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

        // Fallback: по настройкам качества
        var qualityPref = _libraryService?.Settings.QualityPreference ?? AudioQualityPreference.BestAvailable;

        if (qualityPref == AudioQualityPreference.Standard)
        {
            // Предпочитаем mp4/m4a для совместимости
            for (int i = 0; i < streams.Count; i++)
            {
                if (streams[i].Container.Name is "mp4" or "m4a")
                    return streams[i];
            }
        }

        // По умолчанию — лучший битрейт (первый в списке, т.к. отсортирован)
        return streams.Count > 0 ? streams[0] : null;
    }

    #endregion

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
    /// Возвращает доступные форматы для трека.
    /// Для HLS-only треков возвращает только HLS.
    /// </summary>
    public async Task<List<StreamOption>> GetStreamOptionsAsync(string videoId)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId)) return [];

        try
        {
            // Проверяем, HLS-only ли этот трек
            var track = _trackRegistry.TryGet($"yt_{videoId}");
            if (track?.IsHlsOnly == true)
            {
                // Для HLS-only треков — только один вариант
                return
                [
                    new StreamOption
                {
                    Container = "m3u8",
                    Bitrate = 128,
                    Codec = "HLS (Adaptive)",
                    SizeMb = 0,
                    IsActive = true
                }
                ];
            }

            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId);

            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
            {
                // Нет стримов — только HLS
                return
                [
                    new StreamOption
                {
                    Container = "m3u8",
                    Bitrate = 128,
                    Codec = "HLS (Adaptive)",
                    SizeMb = 0
                }
                ];
            }

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

            // При ошибке — только HLS
            return
            [
                new StreamOption
                {
                    Container = "m3u8",
                    Bitrate = 128,
                    Codec = "HLS (Adaptive)",
                    SizeMb = 0
                }
            ];
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
     SearchFilter filter = SearchFilter.None,
     [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            yield break;

        // Проверка cooldown
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

            // Проверяем cooldown между батчами
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
     SearchFilter filter = SearchFilter.None,
     CancellationToken ct = default)
    {
        // ─── Проверка cooldown ───
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
            // Уже обработано
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
        SearchFilter filter = SearchFilter.None)
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
        SearchFilter filter = SearchFilter.None,
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
        SearchFilter filter = SearchFilter.None,
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

        // ─── Проверка cooldown ───
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
        // ─── Проверка cooldown ───
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
                // Проверяем cooldown между запросами
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

    public static async Task<List<Playlist>> GetUserPlaylistsByAuthAsync()
    {
        var userDataService = Program.Services.GetRequiredService<YoutubeUserDataService>();
        return await userDataService.GetMyPlaylistsAsync();
    }

    public async Task<Playlist?> ImportPlaylistAsync(
     string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        // ─── Проверка cooldown ───
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
            var playlist = await _youtube.Playlists.GetAsync(plId, ct);

            playlist.SyncMode = isAccountSync ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.CloudPublic;

            var tracks = await _youtube.Playlists.GetVideosAsync(plId, ct).CollectAsync();

            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (_libraryService != null)
                    await _libraryService.AddOrUpdateTrackAsync(track, ct);
                playlist.TrackIds.Add(track.Id);
            }
            return playlist;
        }
        catch (BotDetectionException)
        {
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"Error importing playlist {playlistId}: {ex.Message}");
            return null;
        }
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

        // ─── Проверка cooldown ───
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
        // ─── Проверка cooldown ───
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

        // ─── Проверка cooldown ───
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
    private static string? GetBestThumbnailUrl(IReadOnlyList<Thumbnail> thumbnails)
    {
        if (thumbnails.Count == 0) return null;

        string? best = null;
        int bestArea = -1;

        for (int i = 0; i < thumbnails.Count; i++)
        {
            int area = thumbnails[i].Resolution.Area;
            if (area > bestArea)
            {
                bestArea = area;
                best = thumbnails[i].Url;
            }
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyStatus(string message)
    {
        Log.Info(message);
        OnStatusChanged?.Invoke(message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyError(string message)
    {
        Log.Error(message);
        OnError?.Invoke(message);
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
        VideoController.OnBotDetectionCooldown -= HandleBotDetectionCooldown;
        VideoController.OnStreamUnavailable -= HandleStreamUnavailable;

        DisposeCurrentClient();

        _currentHandler?.Dispose();
        _currentHandler = null;

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
    [Reactive] public bool IsActive { get; set; }
}

public class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}