using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Models;

namespace LMP.Core.Audio.Http;

// --- Section: Public Models ---

/// <summary>
/// Одна закэшированная сессия воспроизведения YouTube CDN.
/// </summary>
public sealed class SessionEntry
{
    /// <summary>Идентификатор трека (с префиксом <c>yt_</c>).</summary>
    public required string TrackId { get; set; }

    /// <summary>Полный videoplayback URL.</summary>
    public required string VideoplaybackUrl { get; set; }

    /// <summary>CDN hostname, извлечённый из <see cref="VideoplaybackUrl"/>.</summary>
    public required string CdnHost { get; set; }

    /// <summary>
    /// Время истечения URL (из параметра <c>&amp;expire=</c>, UTC).
    /// За 30 минут до этого времени запись считается устаревшей.
    /// </summary>
    public required DateTime ExpireUtc { get; set; }

    /// <summary>Размер контента в байтах (<c>&amp;clen=</c>).</summary>
    public long Clen { get; set; }

    /// <summary>itag потока (<c>&amp;itag=</c>).</summary>
    public int Itag { get; set; }

    /// <summary>Битрейт в bps (<c>&amp;bitrate=</c>).</summary>
    public int Bitrate { get; set; }

    /// <summary>Кодек потока (например, <c>opus</c>).</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Контейнер потока (например, <c>webm</c>).</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Время записи в кэш (UTC). Используется для LRU eviction.</summary>
    public DateTime SavedAtUtc { get; set; }
}

/// <summary>
/// Конверт JSON-файла session-кэша.
/// </summary>
public sealed class SessionCacheEnvelope
{
    /// <summary>Закэшированные сессии (LRU, max 30 записей).</summary>
    public List<SessionEntry> Sessions { get; set; } = [];

    /// <summary>Время последней очистки устаревших записей (UTC).</summary>
    public DateTime LastCleanupUtc { get; set; }
}

// --- Section: Store ---

/// <summary>
/// Персистентный кэш videoplayback URL YouTube CDN с TTL и probe-based валидацией.
/// <para>
/// <b>Flow:</b>
/// </para>
/// <list type="number">
///   <item>
///     После каждого успешного YouTube API resolve вызывается <see cref="Record"/>
///     для сохранения URL и метаданных потока.
///   </item>
///   <item>
///     При следующем воспроизведении того же трека <see cref="TryGetAndProbeAsync"/>
///     проверяет TTL и делает HTTP HEAD к cached URL. Если CDN отвечает 200/206 —
///     YouTube API call пропускается (~500–3000 мс экономии).
///   </item>
/// </list>
/// <para>
/// Probe-timeout адаптируется к реальному TTFB CDN-ноды через
/// <see cref="CdnHostStatsStore.GetAvgTtfbMs"/>.
/// </para>
/// </summary>
internal static class SessionCacheStore
{
    private const int MaxSessions = 30;
    private const int TtlSafetyMarginMinutes = 30;
    private const int MinProbeTimeoutMs = 300;
    private const int MaxProbeTimeoutMs = 3000;
    private const int DefaultProbeTimeoutMs = 2000;
    private const double ProbeTtfbMultiplier = 4.0;

    private static readonly Lock _lock = new();
    private static SessionCacheEnvelope _data = new();
    private static bool _dirty;

    // --- Section: Load / Save ---

    /// <summary>
    /// Загружает session-кэш с диска. Вызывается однократно при старте.
    /// Автоматически очищает устаревшие записи при загрузке.
    /// </summary>
    public static void Load()
    {
        try
        {
            var path = G.FilePath.SessionCache;
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize(
                json,
                AppJsonContext.DefaultCompact.SessionCacheEnvelope);

            if (envelope is null)
                return;

            lock (_lock)
            {
                _data = envelope;
                EvictExpiredMustHoldLock();
            }

            Log.Debug($"[SessionCache] Loaded {_data.Sessions.Count} session(s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SessionCache] Load failed (starting fresh): {ex.Message}");
            lock (_lock) { _data = new SessionCacheEnvelope(); }
        }
    }

    /// <summary>
    /// Сохраняет session-кэш на диск. Вызывается при graceful shutdown.
    /// </summary>
    public static void Save()
    {
        SessionCacheEnvelope snapshot;

        lock (_lock)
        {
            if (!_dirty)
                return;

            EvictExpiredMustHoldLock();
            snapshot = _data;
            _dirty = false;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                snapshot,
                AppJsonContext.DefaultCompact.SessionCacheEnvelope);
            File.WriteAllText(G.FilePath.SessionCache, json);
            Log.Debug($"[SessionCache] Saved {snapshot.Sessions.Count} session(s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"[SessionCache] Save failed: {ex.Message}");
            lock (_lock) { _dirty = true; }
        }
    }

    // --- Section: Record ---

    /// <summary>
    /// Сохраняет videoplayback URL после успешного YouTube API resolve.
    /// </summary>
    /// <param name="trackId">ID трека (с префиксом <c>yt_</c>).</param>
    /// <param name="url">Полный videoplayback URL.</param>
    /// <param name="codec">Кодек потока.</param>
    /// <param name="container">Контейнер потока.</param>
    public static void Record(string trackId, string url, string codec, string container)
    {
        if (string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(url))
            return;

        if (!TryExtractCdnHost(url, out var host))
            return;

        var expireUtc = ParseExpireFromUrl(url);
        if (expireUtc <= DateTime.UtcNow)
            return;

        var entry = new SessionEntry
        {
            TrackId = trackId,
            VideoplaybackUrl = url,
            CdnHost = host,
            ExpireUtc = expireUtc,
            Clen = ParseLongParam(url, "clen"),
            Itag = (int)ParseLongParam(url, "itag"),
            Bitrate = (int)ParseLongParam(url, "bitrate"),
            Codec = codec,
            Container = container,
            SavedAtUtc = DateTime.UtcNow
        };

        lock (_lock)
        {
            // Удаляем предыдущую запись для того же трека
            for (int i = _data.Sessions.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal))
                {
                    _data.Sessions.RemoveAt(i);
                    break;
                }
            }

            // LRU eviction при переполнении
            while (_data.Sessions.Count >= MaxSessions)
                EvictLeastRecentlyUsedMustHoldLock();

            _data.Sessions.Add(entry);
            _dirty = true;
        }

        // Cold path: ~1 раз на трек. File.WriteAllText для ~3KB < 1ms на ThreadPool.
        _ = Task.Run(Save);
    }

    // --- Section: Probe ---

    /// <summary>
    /// Пытается получить и проверить cached URL для трека.
    /// <para>
    /// Алгоритм:
    /// </para>
    /// <list type="number">
    ///   <item>Поиск записи по <paramref name="trackId"/>.</item>
    ///   <item>Проверка TTL: если <c>expireUtc - 30 мин &lt; now</c> — запись дропается.</item>
    ///   <item>
    ///     Probe HEAD к cached URL с адаптивным timeout
    ///     (<c>AvgTtfbMs × 4</c>, зажато в [300, 3000] мс).
    ///   </item>
    ///   <item>200/206 → возвращает entry. 403/timeout → дропает запись.</item>
    /// </list>
    /// </summary>
    /// <param name="trackId">ID трека.</param>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// Валидная <see cref="SessionEntry"/> или <c>null</c> если кэш промахнулся/протух.
    /// </returns>
    public static async ValueTask<SessionEntry?> TryGetAndProbeAsync(
      string trackId,
      HttpClient httpClient,
      CancellationToken ct)
    {
        SessionEntry? entry;

        lock (_lock)
        {
            entry = FindMustHoldLock(trackId);
            if (entry is null) return null;

            if (entry.ExpireUtc.AddMinutes(-TtlSafetyMarginMinutes) <= DateTime.UtcNow)
            {
                DropMustHoldLock(trackId);
                Log.Debug($"[SessionCache] TTL expired for {trackId}, dropping");
                return null;
            }
        }

        // Async, вне I/O
        var probeTimeoutMs = ComputeProbeTimeout(entry.CdnHost);

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(probeTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Head, entry.VideoplaybackUrl);
            // Range: bytes=0-0 гарантирует что CDN реально проверяет доступность контента,
            // а не возвращает 200 на redirect/login страницу
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            request.Version = System.Net.HttpVersion.Version20;
            SharedHttpClient.ApplyUserAgentFromUrl(request, entry.VideoplaybackUrl);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;

            if (status is 200 or 206)
            {
                Log.Debug($"[SessionCache] Probe OK ({status}) for {trackId}");
                return entry;
            }

            Log.Debug($"[SessionCache] Probe rejected (HTTP {status}) for {trackId}, dropping");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"[SessionCache] Probe timed out ({probeTimeoutMs}ms) for {trackId}, dropping");
        }
        catch (Exception ex)
        {
            Log.Debug($"[SessionCache] Probe failed for {trackId}: {ex.Message}");
        }

        lock (_lock) { DropMustHoldLock(trackId); }
        return null;
    }

    // --- Section: Private Helpers ---

    private static int ComputeProbeTimeout(string cdnHost)
    {
        var avgTtfb = CdnHostStatsStore.GetAvgTtfbMs(cdnHost);
        if (double.IsNaN(avgTtfb))
            return DefaultProbeTimeoutMs;

        return Math.Clamp((int)(avgTtfb * ProbeTtfbMultiplier), MinProbeTimeoutMs, MaxProbeTimeoutMs);
    }

    private static SessionEntry? FindMustHoldLock(string trackId)
    {
        for (int i = 0; i < _data.Sessions.Count; i++)
        {
            if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal))
                return _data.Sessions[i];
        }
        return null;
    }

    private static void DropMustHoldLock(string trackId)
    {
        for (int i = _data.Sessions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal))
            {
                _data.Sessions.RemoveAt(i);
                _dirty = true;
                return;
            }
        }
    }

    private static void EvictLeastRecentlyUsedMustHoldLock()
    {
        if (_data.Sessions.Count == 0)
            return;

        int oldestIdx = 0;
        for (int i = 1; i < _data.Sessions.Count; i++)
        {
            if (_data.Sessions[i].SavedAtUtc < _data.Sessions[oldestIdx].SavedAtUtc)
                oldestIdx = i;
        }

        _data.Sessions.RemoveAt(oldestIdx);
    }

    private static void EvictExpiredMustHoldLock()
    {
        var threshold = DateTime.UtcNow.AddMinutes(-TtlSafetyMarginMinutes);
        for (int i = _data.Sessions.Count - 1; i >= 0; i--)
        {
            if (_data.Sessions[i].ExpireUtc <= threshold)
                _data.Sessions.RemoveAt(i);
        }
        _data.LastCleanupUtc = DateTime.UtcNow;
    }

    private static bool TryExtractCdnHost(string url, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        return true;
    }

    private static DateTime ParseExpireFromUrl(string url)
    {
        var expireStr = UrlEx.TryGetQueryParameterValue(url, "expire");
        if (expireStr is null || !long.TryParse(expireStr, out var unixSeconds))
            return DateTime.UtcNow.AddHours(6);

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
    }

    private static long ParseLongParam(string url, string paramName)
    {
        var value = UrlEx.TryGetQueryParameterValue(url, paramName);
        return value is not null && long.TryParse(value, out var result) ? result : 0;
    }
}