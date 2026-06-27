using System.Collections.Concurrent;
using System.Diagnostics;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Спекулятивный прогрев TCP+TLS-соединений к YouTube CDN нодам.
/// <para>
/// YouTube раздаёт аудио с CDN-нод вида <c>rr{N}---{sn-*}.googlevideo.com</c>.
/// Первое TLS-рукопожатие к новой ноде стоит 0.5–3.2 с (DNS + TCP + TLS + server cold-start).
/// Последующие запросы к тому же хосту мультиплексируются через HTTP/2 с TTFB ~100 мс.
/// </para>
/// <para>
/// <b>Стратегия прогрева:</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Спекулятивный (перед YouTube API call):</b> <see cref="PreWarmRecentHostsAsync"/>
///     открывает соединения к последним известным CDN-хостам.
///     Если следующий трек попадёт на тот же хост — TTFB ≈ 100 мс вместо ~3 с.
///   </item>
///   <item>
///     <b>Точечный (после получения URL):</b> <see cref="PreWarmHostAsync"/>
///     открывает соединение к конкретному хосту из URL.
///     Даёт фору 30–50 мс перед <c>InitializeAsync</c>.
///   </item>
/// </list>
/// <para>
/// Прогрев выполняется через HTTP HEAD к корневому URL хоста.
/// Ответ (404/403/etc) не имеет значения — важно только установление TCP+TLS
/// и регистрация соединения в пуле <see cref="HttpClient"/>.
/// HTTP/2 мультиплексирует все последующие запросы через это соединение.
/// </para>
/// </summary>
internal static class CdnConnectionPreWarmer
{
    /// <summary>
    /// Суффикс YouTube CDN хостов. Используется для фильтрации —
    /// прогреваем только googlevideo.com, не трогая другие домены.
    /// </summary>
    private const string GoogleVideoCdnSuffix = ".googlevideo.com";

    private const string GenerateEndpoint = "/generate_204";

    /// <summary>
    /// Максимальное количество запоминаемых CDN-хостов.
    /// YouTube обычно использует 2–4 ноды для одного региона.
    /// </summary>
    private const int MaxTrackedHosts = 4;

    /// <summary>
    /// Интервал, в течение которого повторный прогрев одного хоста пропускается.
    /// <see cref="SocketsHttpHandler"/> удерживает idle-соединения ~2 минут,
    /// поэтому 60 с — безопасный порог для повторного прогрева.
    /// </summary>
    private static readonly TimeSpan WarmCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Таймаут одного спекулятивного соединения.
    /// Если TLS не завершился за это время — хост слишком медленный,
    /// и прогрев не даст выигрыша (реальный запрос всё равно будет ждать).
    /// </summary>
    private static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Минимальный интервал между вызовами спекулятивного прогрева.
    /// Предотвращает шторм HEAD-запросов при быстрых переключениях фокуса окна
    /// или повторных <see cref="ISuspendable.OnResume"/> без реальной смены трека.
    /// </summary>
    private static readonly TimeSpan SpeculativeThrottle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Момент последнего спекулятивного прогрева.
    /// Защита от шторма при быстром suspend/resume или повторных focus-переключениях.
    /// </summary>
    private static DateTime _lastSpeculativeWarmTime = DateTime.MinValue;

    private static readonly Lock _lock = new();
    private static readonly LinkedList<(string Host, DateTime WarmTime)> _recentHosts = new();

    /// <summary>
    /// Регистрирует CDN-хост после успешного HTTP-ответа.
    /// Вызывается из <see cref="Audio.Sources.CachingStreamSource"/> после каждого успешного range-запроса.
    /// </summary>
    /// <param name="url">URL завершённого запроса.</param>
    public static void RecordHost(string url)
    {
        if (!TryExtractHost(url, out var host))
            return;

        // Регистрируем хит в персистентной статистике
        CdnHostStatsStore.RecordHit(host);

        lock (_lock)
        {
            var node = _recentHosts.First;
            while (node != null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Host, host, StringComparison.OrdinalIgnoreCase))
                    _recentHosts.Remove(node);
                node = next;
            }

            _recentHosts.AddFirst((host, DateTime.UtcNow));

            while (_recentHosts.Count > MaxTrackedHosts)
                _recentHosts.RemoveLast();
        }
    }

    /// <summary>
    /// Спекулятивный прогрев соединений к последним известным CDN-хостам.
    /// <para>
    /// Вызывается <b>перед</b> YouTube API call при подготовке к воспроизведению нового трека,
    /// а также при выходе из suspend. Встроен глобальный throttle: повторные вызовы
    /// чаще <see cref="SpeculativeThrottle"/> пропускаются, чтобы не генерировать
    /// бесполезный трафик при стабильном воспроизведении.
    /// </para>
    /// </summary>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="ct">Токен отмены (lifetime плеера, не трека).</param>
    public static void PreWarmRecentHosts(HttpClient httpClient, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            if (now - _lastSpeculativeWarmTime < SpeculativeThrottle)
                return;

            if (_recentHosts.Count == 0)
                return;

            _lastSpeculativeWarmTime = now;
        }

        string[] hostsToWarm;

        lock (_lock)
        {
            hostsToWarm = new string[_recentHosts.Count];
            int count = 0;

            foreach (var (host, warmTime) in _recentHosts)
            {
                if (now - warmTime < WarmCooldown)
                    continue;

                hostsToWarm[count++] = host;
            }

            if (count == 0)
                return;

            hostsToWarm = hostsToWarm[..count];
        }

        for (int i = 0; i < hostsToWarm.Length; i++)
            _ = WarmHostCoreAsync(httpClient, hostsToWarm[i], ct);

        Log.Debug($"[CdnPreWarmer] Speculative warm-up fired for {hostsToWarm.Length} recent CDN host(s)");
    }

    /// <summary>
    /// Точечный прогрев соединения к конкретному CDN-хосту из URL.
    /// <para>
    /// Вызывается сразу после получения stream URL от YouTube API,
    /// <b>до</b> создания <see cref="Audio.Sources.CachingStreamSource"/>.
    /// Даёт фору ~30–50 мс перед первым real GET в <c>InitializeAsync</c>.
    /// </para>
    /// </summary>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="url">Stream URL, содержащий CDN hostname.</param>
    /// <param name="ct">Токен отмены.</param>
    public static void PreWarmHost(HttpClient httpClient, string url, CancellationToken ct)
    {
        if (!TryExtractHost(url, out var host))
            return;

        lock (_lock)
        {
            foreach (var (trackedHost, warmTime) in _recentHosts)
            {
                if (string.Equals(trackedHost, host, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - warmTime < WarmCooldown)
                {
                    Log.Debug($"[CdnPreWarmer] Host {host[..Math.Min(host.Length, 30)]}... still warm, skipping");
                    return;
                }
            }
        }

        _ = WarmHostCoreAsync(httpClient, host, ct);
        Log.Debug($"[CdnPreWarmer] Targeted warm-up fired for {host[..Math.Min(host.Length, 40)]}...");
    }

    /// <summary>
    /// Выполняет TCP+TLS прогрев CDN-ноды через <c>GET /generate_204</c>.
    /// <para>
    /// <c>/generate_204</c> — нативный Google connectivity-check endpoint.
    /// Возвращает HTTP 204 No Content с гарантированно пустым телом,
    /// что исключает аллокации на парсинг error-body (в отличие от HEAD /).
    /// Легитимный паттерн: браузерный YouTube player использует его для CDN pre-connect.
    /// </para>
    /// <para>
    /// После успешного прогрева TTFB записывается в <see cref="CdnHostStatsStore"/>
    /// для адаптивного probe-timeout в <see cref="SessionCacheStore"/>.
    /// </para>
    /// </summary>
    private static async Task WarmHostCoreAsync(HttpClient httpClient, string host, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(WarmTimeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://{host}{GenerateEndpoint}");

            request.Headers.ConnectionClose = false;
            SharedHttpClient.ApplyUserAgentFromUrl(request, $"https://{host}/");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            sw.Stop();

            CdnHostStatsStore.RecordTtfb(host, sw.ElapsedMilliseconds);
            CdnHostStatsStore.FlushIfNeeded();

            Log.Debug(
                $"[CdnPreWarmer] {host[..Math.Min(host.Length, 30)]}... " +
                $"warm in {sw.ElapsedMilliseconds}ms (HTTP {(int)response.StatusCode})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log.Debug($"[CdnPreWarmer] {host[..Math.Min(host.Length, 30)]}... timed out ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Debug($"[CdnPreWarmer] {host[..Math.Min(host.Length, 30)]}... failed ({sw.ElapsedMilliseconds}ms): {ex.Message}");
        }
    }

    /// <summary>
    /// Прогревает один конкретный хост. Используется из <see cref="CdnHostStatsStore.PreWarmTopClustersAsync"/>.
    /// </summary>
    /// <param name="httpClient">HTTP-клиент с общим connection pool.</param>
    /// <param name="host">CDN hostname.</param>
    /// <param name="ct">Токен отмены.</param>
    internal static Task WarmSingleHostAsync(HttpClient httpClient, string host, CancellationToken ct)
        => WarmHostCoreAsync(httpClient, host, ct);

    /// <summary>
    /// Извлекает hostname из URL, если это YouTube CDN.
    /// </summary>
    private static bool TryExtractHost(string url, out string host)
    {
        host = string.Empty;

        if (string.IsNullOrEmpty(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.EndsWith(GoogleVideoCdnSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        return true;
    }
}