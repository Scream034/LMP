
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
    // КОНСТАНТЫ

    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    // ЗАВИСИМОСТИ

    private readonly YoutubeClient _youtube;
    private readonly string _downloadFolder;
    private readonly LibraryService? _libraryService;

    // КЭШИРОВАНИЕ ПОТОКОВ

    /// <summary>
    /// Кэш информации о потоках (URL, размер, битрейт, кодек, время получения)
    /// </summary>
    private readonly Dictionary<string, StreamCacheEntry> _streamCache = [];
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);

    /// <summary>
    /// Запись кэша потока
    /// </summary>
    private class StreamCacheEntry
    {
        public required string Url { get; init; }
        public long Size { get; init; }
        public int Bitrate { get; init; }
        public required string Codec { get; init; }
        public required string Container { get; init; }
        public DateTime Obtained { get; init; }
    }

    // ПУБЛИЧНЫЕ СВОЙСТВА

    /// <summary>
    /// Готов ли провайдер к работе
    /// </summary>
    public bool IsReady { get; private set; }

    // СОБЫТИЯ

    /// <summary>
    /// Изменился статус (для логирования)
    /// </summary>
    public event Action<string>? OnStatusChanged;

    /// <summary>
    /// Произошла ошибка
    /// </summary>
    public event Action<string>? OnError;

    // РЕГУЛЯРНЫЕ ВЫРАЖЕНИЯ

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    // КОНСТРУКТОРЫ

    /// <summary>
    /// Создает провайдер YouTube (базовый конструктор)
    /// </summary>
    public YoutubeProvider() : this(null)
    {
    }

    /// <summary>
    /// Создает провайдер YouTube с доступом к настройкам библиотеки
    /// </summary>
    /// <param name="libraryService">Сервис библиотеки для доступа к настройкам качества</param>
    public YoutubeProvider(LibraryService? libraryService)
    {
        _youtube = new YoutubeClient();
        _libraryService = libraryService;

        // Настройка папки загрузок
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _downloadFolder = Path.Combine(appFolder, "Downloads");

        Directory.CreateDirectory(_downloadFolder);
    }

    /// <summary>
    /// Инициализирует провайдер
    /// </summary>
    public Task InitializeAsync()
    {
        IsReady = true;
        NotifyStatus("[YouTube] Initialized");
        return Task.CompletedTask;
    }

    // ПОЛУЧЕНИЕ ПОТОКА

    #region RefreshStreamUrlAsync

    /// <summary>
    /// Получает или обновляет URL аудио потока для трека
    /// </summary>
    /// <param name="track">Трек для которого нужен поток</param>
    /// <param name="ct">Токен отмены</param>
    /// <returns>Кортеж с информацией о потоке или null при ошибке</returns>
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

        // ОПРЕДЕЛЕНИЕ ФОРМАТА:
        // 1. Сначала проверяем временный выбор (если пользователь только что переключил качество вручную)
        string? targetContainer = track.TransientContainer;
        int targetBitrate = track.TransientBitrate;

        // 2. Если временного нет, проверяем сохраненный, НО только если включена настройка
        if (string.IsNullOrEmpty(targetContainer))
        {
            if (_libraryService?.Data.RememberTrackFormat == true)
            {
                targetContainer = track.PreferredContainer;
                targetBitrate = track.PreferredBitrate;
            }
            // Если RememberTrackFormat == false, то targetContainer останется null,
            // что приведет к использованию Auto режима (SelectBestStream выберет по умолчанию)
        }

        // Генерируем ключ кэша с учетом выбранного (или авто) контейнера
        string cacheKey = GenerateCacheKey(videoId, targetContainer, targetBitrate);

        // Проверяем кэш
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

            // Получаем манифест потоков
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, cts.Token);
            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
            {
                NotifyError($"[YouTube] [{videoId}] No audio streams found");
                return null;
            }

            // Передаем определенный нами targetContainer (который может быть null для Авто)
            AudioOnlyStreamInfo? selectedStream = SelectBestStream(audioStreams, targetContainer, targetBitrate);

            if (selectedStream == null)
            {
                NotifyError($"[YouTube] [{videoId}] Could not select audio stream");
                return null;
            }

            // Извлекаем информацию о потоке
            var url = selectedStream.Url;
            var size = selectedStream.Size.Bytes;
            var bitrate = (int)selectedStream.Bitrate.KiloBitsPerSecond;
            var container = selectedStream.Container.Name;
            var codec = DetermineCodec(container, selectedStream);

            sw.Stop();
            NotifyStatus($"[YouTube] [{videoId}] Got stream: {codec}/{bitrate}kbps ({container}) in {sw.ElapsedMilliseconds}ms");

            // Сохраняем в кэш
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

    /// <summary>
    /// Выбирает лучший аудио поток на основе предпочтений
    /// </summary>
    private AudioOnlyStreamInfo? SelectBestStream(
        List<AudioOnlyStreamInfo> streams,
        string? preferredContainer,
        int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        // 1. Если есть ручной выбор контейнера - ищем его
        if (!string.IsNullOrEmpty(preferredContainer))
        {
            var containerStreams = streams.Where(s =>
                s.Container.Name.Equals(preferredContainer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (containerStreams.Count > 0)
            {
                // Если указан конкретный битрейт - ищем ближайший
                if (preferredBitrate > 0)
                {
                    return containerStreams.MinBy(s => Math.Abs(s.Bitrate.KiloBitsPerSecond - preferredBitrate));
                }

                // Иначе берем лучший (первый, так как streams отсортированы)
                Log.Info($"[YouTube] Using preferred container: {preferredContainer}");
                return containerStreams.First();
            }
        }

        // 2. Смотрим глобальную настройку качества
        var qualityPref = _libraryService?.Data.QualityPreference ?? AudioQualityPreference.BestAvailable;

        return qualityPref switch
        {
            AudioQualityPreference.BestAvailable => streams.FirstOrDefault(),// Лучший битрейт (обычно Opus/WebM)
            AudioQualityPreference.Standard => streams.FirstOrDefault(s => s.Container.Name == "mp4")
                                ?? streams.FirstOrDefault(),// Ищем MP4 (AAC) для совместимости
            _ => streams.FirstOrDefault(),
        };
    }

    /// <summary>
    /// Определяет кодек по контейнеру
    /// </summary>
    private static string DetermineCodec(string container, AudioOnlyStreamInfo stream)
    {
        // Пытаемся определить по AudioCodec если доступен
        var codecStr = stream.AudioCodec;

        if (!string.IsNullOrEmpty(codecStr))
        {
            if (codecStr.Contains("opus", StringComparison.OrdinalIgnoreCase))
                return "Opus";
            if (codecStr.Contains("aac", StringComparison.OrdinalIgnoreCase))
                return "AAC";
            if (codecStr.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
                return "AAC";
            if (codecStr.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
                return "Vorbis";
        }

        // Fallback по контейнеру
        return container.ToLower() switch
        {
            "webm" => "Opus",
            "mp4" => "AAC",
            "m4a" => "AAC",
            _ => container.ToUpper()
        };
    }

    /// <summary>
    /// Получает список доступных форматов для трека
    /// </summary>
    /// <param name="videoId">ID видео YouTube</param>
    /// <returns>Список доступных форматов</returns>
    public async Task<List<StreamOption>> GetStreamOptionsAsync(string videoId)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId))
            return [];

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

    /// <summary>
    /// Определяет локализованное название кодека
    /// </summary>
    private string DetermineCodecDisplayName(string container, AudioOnlyStreamInfo stream)
    {
        var codecStr = stream.AudioCodec;

        if (!string.IsNullOrEmpty(codecStr))
        {
            if (codecStr.Contains("opus", StringComparison.OrdinalIgnoreCase))
                return "Opus";
            if (codecStr.Contains("aac", StringComparison.OrdinalIgnoreCase) ||
                codecStr.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
                return "AAC";
            if (codecStr.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
                return "Vorbis";
        }

        return container.ToLower() switch
        {
            "webm" => "Opus",
            "mp4" or "m4a" => "AAC",
            _ => container.ToUpper()
        };
    }

    #endregion

    // КЭШИРОВАНИЕ

    #region Cache

    /// <summary>
    /// Генерирует ключ кэша с учетом контейнера
    /// </summary>
    private static string GenerateCacheKey(string videoId, string? container, int bitrate = 0)
    {
        var key = string.IsNullOrEmpty(container) ? videoId : $"{videoId}_{container}";
        if (bitrate > 0) key += $"_{bitrate}";
        return key;
    }

    /// <summary>
    /// Пытается получить данные из кэша
    /// </summary>
    private bool TryGetFromCache(string cacheKey, out StreamCacheEntry result)
    {
        if (_streamCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Obtained < _streamCacheLifetime)
            {
                result = cached;
                return true;
            }

            // Удаляем устаревшую запись
            _streamCache.Remove(cacheKey);
        }

        result = null!;
        return false;
    }

    /// <summary>
    /// Сохраняет информацию о потоке в кэш
    /// </summary>
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

        // Очистка устаревших записей при переполнении
        if (_streamCache.Count > MaxCacheSize)
        {
            CleanupExpiredCache();
        }
    }

    /// <summary>
    /// Очищает устаревшие записи кэша
    /// </summary>
    private void CleanupExpiredCache()
    {
        var expired = _streamCache
            .Where(kv => DateTime.UtcNow - kv.Value.Obtained > _streamCacheLifetime)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _streamCache.Remove(key);
        }

        Log.Debug($"[YouTube] Cache cleanup: removed {expired.Count} expired entries");
    }

    /// <summary>
    /// Очищает весь кэш (например, при смене настроек качества)
    /// </summary>
    public void ClearCache()
    {
        _streamCache.Clear();
        Log.Info("[YouTube] Stream cache cleared");
    }

    #endregion

    // ПОИСК И ПЛЕЙЛИСТЫ

    #region Search, Playlist, etc.

    /// <summary>
    /// Определяет тип запроса (URL видео, плейлист или поисковый запрос)
    /// </summary>
    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return QueryType.None;

        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query))
            return QueryType.Playlist;

        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return QueryType.DirectUrl;
        }

        return QueryType.Search;
    }

    /// <summary>
    /// Извлекает ID видео из URL
    /// </summary>
    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var match = YoutubeVideoRegex.Match(url);
        if (match.Success)
            return match.Groups[1].Value;

        try
        {
            return VideoId.TryParse(url)?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Извлекает ID видео из трека
    /// </summary>
    private static string? ExtractVideoIdFromTrack(TrackInfo track)
    {
        string cleanId = track.Id?.Trim() ?? "";

        // Если ID начинается с yt_ - извлекаем реальный ID
        if (cleanId.StartsWith("yt_"))
        {
            var rawId = cleanId[3..];
            var safeId = new string(rawId
                .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
                .ToArray());

            if (ValidYoutubeId.IsMatch(safeId))
                return safeId;
        }

        // Пробуем извлечь из URL
        if (!string.IsNullOrWhiteSpace(track.Url))
        {
            return ExtractVideoId(track.Url);
        }

        return null;
    }

    /// <summary>
    /// Получает информацию о треке по URL
    /// </summary>
    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            var video = await _youtube.Videos.GetAsync(videoId);

            return ConvertToTrackInfo(video);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Выполняет поиск треков
    /// </summary>
    /// <param name="query">Поисковый запрос</param>
    /// <param name="maxResults">Максимальное количество результатов</param>
    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 20)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return [];

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
            NotifyStatus($"[YouTube] Search '{query}': {results.Count} results in {sw.ElapsedMilliseconds}ms");

            return results;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] SearchAsync error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Получает треки из плейлиста
    /// </summary>
    /// <param name="url">URL плейлиста</param>
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

    /// <summary>
    /// Получает похожие треки (YouTube Mix)
    /// </summary>
    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url))
            return [];

        try
        {
            var videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) return [];

            var mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";
            var result = await GetPlaylistAsync(mixUrl);

            var tracks = result?.Tracks.Take(count).ToList() ?? [];

            foreach (var t in tracks)
            {
                t.RadioSeedId = sourceTrack.Id;
            }

            return tracks;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Получает популярные треки
    /// </summary>
    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            // YouTube Music Top Charts
            var url = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(url);

            return result?.Tracks.Take(count).ToList() ?? await SearchAsync("top music 2024", count);
        }
        catch
        {
            return await SearchAsync("top music 2024", count);
        }
    }

    /// <summary>
    /// Заглушка для пользовательских плейлистов
    /// </summary>
    public static Task<List<Playlist>> GetUserPlaylistsAsync() =>
        Task.FromResult(new List<Playlist>());

    /// <summary>
    /// Заглушка для персональных рекомендаций
    /// </summary>
    public Task<List<TrackInfo>> GetPersonalRecommendationsAsync(int count = 20) =>
        GetTrendingAsync(count);

    /// <summary>
    /// Скачивает трек на диск
    /// </summary>
    public async Task<string?> DownloadTrackAsync(
        TrackInfo track,
        IProgress<float>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url))
            return null;

        try
        {
            var videoId = ExtractVideoId(track.Url);
            if (string.IsNullOrEmpty(videoId)) return null;

            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(_downloadFolder, fileName);

            var prog = progress != null
                ? new Progress<double>(p => progress.Report((float)p))
                : null;

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

    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ

    #region Helpers

    /// <summary>
    /// Конвертирует Video в TrackInfo
    /// </summary>
    private static TrackInfo ConvertToTrackInfo(Video video)
    {
        var thumb = video.Thumbnails
            .OrderByDescending(t => t.Resolution.Width)
            .FirstOrDefault();

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

    /// <summary>
    /// Конвертирует результат поиска в TrackInfo
    /// </summary>
    private static TrackInfo ConvertSearchResultToTrackInfo(VideoSearchResult video)
    {
        // Берем второй по размеру thumbnail (обычно оптимальный для UI)
        var thumb = video.Thumbnails
            .OrderByDescending(t => t.Resolution.Width)
            .Skip(1)
            .FirstOrDefault();

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

    /// <summary>
    /// Конвертирует видео из плейлиста в TrackInfo
    /// </summary>
    private static TrackInfo ConvertPlaylistVideoToTrackInfo(PlaylistVideo video)
    {
        var thumb = video.Thumbnails
            .OrderByDescending(t => t.Resolution.Width)
            .Skip(1)
            .FirstOrDefault();

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

    /// <summary>
    /// Очищает имя файла от недопустимых символов
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());

        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    /// <summary>
    /// Уведомляет о статусе
    /// </summary>
    private void NotifyStatus(string message)
    {
        Log.Info(message);
        OnStatusChanged?.Invoke(message);
    }

    /// <summary>
    /// Уведомляет об ошибке
    /// </summary>
    private void NotifyError(string message)
    {
        Log.Error(message);
        OnError?.Invoke(message);
    }

    // GENERATED REGEX

    [GeneratedRegex(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "ru-RU")]
    private static partial Regex _YoutubeVideoRegex();

    [GeneratedRegex(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "ru-RU")]
    private static partial Regex _YoutubePlaylistRegex();

    [GeneratedRegex(
        @"^[a-zA-Z0-9_-]{11}$",
        RegexOptions.Compiled)]
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