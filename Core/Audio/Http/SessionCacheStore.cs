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
    private const int MinProbeTimeoutMs = 1000;
    private const int MaxProbeTimeoutMs = 3000;
    private const int DefaultProbeTimeoutMs = 2000;
    private const int TtlBypassProbeMinutes = 10;
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
    /// Возвращает дефолтный битрейт для заданного itag, если точные данные отсутствуют.
    /// </summary>
    private static int GetDefaultBitrateForItag(int itag) => itag switch
    {
        140 => 128000,
        249 => 50000,
        250 => 70000,
        251 => 160000,
        _ => 128000
    };

    /// <summary>
    /// Сохраняет videoplayback URL после успешного YouTube API resolve.
    /// </summary>
    public static void Record(
        string trackId,
        string url,
        string codec,
        string container,
        int bitrateKbps = 0,
        long clen = 0)
    {
        if (string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(url))
            return;

        if (!TryExtractCdnHost(url, out var host))
            return;

        var expireUtc = ParseExpireFromUrl(url);
        if (expireUtc <= DateTime.UtcNow)
            return;

        int itag = (int)ParseLongParam(url, "itag");
        int finalBitrate = bitrateKbps > 0
            ? bitrateKbps * 1000
            : (int)ParseLongParam(url, "bitrate");

        if (finalBitrate <= 0)
        {
            finalBitrate = GetDefaultBitrateForItag(itag);
        }

        var entry = new SessionEntry
        {
            TrackId = trackId,
            VideoplaybackUrl = url,
            CdnHost = host,
            ExpireUtc = expireUtc,
            Clen = clen > 0 ? clen : ParseLongParam(url, "clen"),
            Itag = itag,
            Bitrate = finalBitrate,
            Codec = codec,
            Container = container ?? string.Empty,
            SavedAtUtc = DateTime.UtcNow
        };

        lock (_lock)
        {
            for (int i = _data.Sessions.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal) &&
                    string.Equals(_data.Sessions[i].Container, container, StringComparison.OrdinalIgnoreCase))
                {
                    _data.Sessions.RemoveAt(i);
                    break;
                }
            }

            while (_data.Sessions.Count >= MaxSessions)
                EvictLeastRecentlyUsedMustHoldLock();

            _data.Sessions.Add(entry);
            _dirty = true;
        }

        _ = Task.Run(Save);
    }

    // --- Section: Probe ---

    /// <summary>
    /// Пытается получить и проверить cached URL для трека с учетом контейнера.
    /// <para>
    /// Алгоритм:
    /// </para>
    /// <list type="number">
    ///   <item>Поиск записи по <paramref name="trackId"/> и <paramref name="container"/>.</item>
    ///   <item>Проверка TTL: если <c>expireUtc - 30 мин &lt; now</c> — запись дропается.</item>
    ///   <item>
    ///     Probe HEAD к cached URL с адаптивным timeout
    ///     (<c>AvgTtfbMs × 4</c>, зажато в [300, 3000] мс).
    ///   </item>
    ///   <item>200/206 → возвращает entry. 403/404 → дропает запись.</item>
    /// </list>
    /// </summary>
    /// <param name="trackId">ID трека.</param>
    /// <param name="container">Контейнер потока (webm, mp4).</param>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>
    /// Валидная <see cref="SessionEntry"/> или <c>null</c> если кэш промахнулся/протух.
    /// </returns>
    public static async ValueTask<SessionEntry?> TryGetAndProbeAsync(
      string trackId,
      string container,
      HttpClient httpClient,
      CancellationToken ct)
    {
        SessionEntry? entry;

        lock (_lock)
        {
            entry = FindMustHoldLock(trackId, container);
            if (entry is null) return null;

            if (entry.ExpireUtc.AddMinutes(-TtlSafetyMarginMinutes) <= DateTime.UtcNow)
            {
                DropMustHoldLock(trackId, container);
                Log.Debug($"[SessionCache] TTL expired for {trackId} ({container}), dropping");
                return null;
            }

            // Мгновенный возврат без сетевого зонда, если ссылка получена менее 10 минут назад.
            // Предотвращает лаги и таймауты при быстрых перезапусках.
            if (DateTime.UtcNow - entry.SavedAtUtc < TimeSpan.FromMinutes(TtlBypassProbeMinutes))
            {
                Log.Debug($"[SessionCache] Probe bypassed (saved {(DateTime.UtcNow - entry.SavedAtUtc).TotalSeconds:F0}s ago) for {trackId} ({container})");
                return entry;
            }
        }

        var probeTimeoutMs = ComputeProbeTimeout(entry.CdnHost);

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(probeTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Head, entry.VideoplaybackUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            request.Version = System.Net.HttpVersion.Version20;
            SharedHttpClient.ApplyUserAgentFromUrl(request, entry.VideoplaybackUrl);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;

            if (status is 200 or 206)
            {
                Log.Debug($"[SessionCache] Probe OK ({status}) for {trackId} ({container})");
                return entry;
            }

            if (status is 401 or 403 or 404 or 410)
            {
                Log.Debug($"[SessionCache] Probe rejected (HTTP {status}) for {trackId}, dropping");
                lock (_lock) { DropMustHoldLock(trackId, container); }
                return null;
            }

            Log.Warn($"[SessionCache] Probe returned HTTP {status} for {trackId}, keeping entry for safety");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"[SessionCache] Probe timed out ({probeTimeoutMs}ms) for {trackId}, keeping entry");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Debug($"[SessionCache] Probe network error for {trackId}: {ex.Message}, keeping entry");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"[SessionCache] Probe failed for {trackId}: {ex.Message}, keeping entry");
            return null;
        }
    }

    /// <summary>
    /// Возвращает список всех валидных, неистекших сессий для указанного трека.
    /// Позволяет реконструировать манифест без обращения к сети.
    /// </summary>
    /// <param name="trackId">Идентификатор трека.</param>
    public static List<SessionEntry> GetValidSessions(string trackId)
    {
        var result = new List<SessionEntry>();
        lock (_lock)
        {
            var threshold = DateTime.UtcNow.AddMinutes(-TtlSafetyMarginMinutes);
            for (int i = 0; i < _data.Sessions.Count; i++)
            {
                var session = _data.Sessions[i];
                if (string.Equals(session.TrackId, trackId, StringComparison.Ordinal))
                {
                    if (session.ExpireUtc > threshold)
                    {
                        result.Add(session);
                    }
                }
            }
        }
        return result;
    }

    // --- Section: Private Helpers ---

    private static int ComputeProbeTimeout(string cdnHost)
    {
        var avgTtfb = CdnHostStatsStore.GetAvgTtfbMs(cdnHost);
        if (double.IsNaN(avgTtfb))
            return DefaultProbeTimeoutMs;

        return Math.Clamp((int)(avgTtfb * ProbeTtfbMultiplier), MinProbeTimeoutMs, MaxProbeTimeoutMs);
    }

    private static SessionEntry? FindMustHoldLock(string trackId, string container)
    {
        // Сначала ищем точное совпадение с контейнером
        for (int i = 0; i < _data.Sessions.Count; i++)
        {
            if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal) &&
                string.Equals(_data.Sessions[i].Container, container, StringComparison.OrdinalIgnoreCase))
            {
                return _data.Sessions[i];
            }
        }
        // Fallback для старых записей без указания контейнера
        for (int i = 0; i < _data.Sessions.Count; i++)
        {
            if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal))
            {
                return _data.Sessions[i];
            }
        }
        return null;
    }

    private static void DropMustHoldLock(string trackId, string container)
    {
        for (int i = _data.Sessions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_data.Sessions[i].TrackId, trackId, StringComparison.Ordinal) &&
               (string.IsNullOrEmpty(_data.Sessions[i].Container) || string.Equals(_data.Sessions[i].Container, container, StringComparison.OrdinalIgnoreCase)))
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