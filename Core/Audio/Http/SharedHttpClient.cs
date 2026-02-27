using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Shared HttpClient для аудио-системы.
/// Singleton с конфигурацией для стриминга: keep-alive, connection pooling, compression.
/// </summary>
public static class SharedHttpClient
{
    private static readonly Lazy<HttpClient> _instance = new(CreateClient);

    /// <summary>Shared HttpClient instance.</summary>
    public static HttpClient Instance => _instance.Value;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // ── Connection pooling ──
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true,

            // ── Keep-alive ──
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),

            // ── Timeouts ──
            ConnectTimeout = TimeSpan.FromSeconds(10),
            
            // ── КРИТИЧНО для стабильности при seek/cancel ──
            // При отмене HTTP запроса .NET пытается "дочитать" response body
            // чтобы переиспользовать HTTP/2 соединение.
            // Без ограничения это может зависнуть на SslStream.ReadAsync
            // и вызвать IOException в IO completion thread.
            ResponseDrainTimeout = TimeSpan.FromSeconds(2),

            // ── Compression & cookies ──
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.Add("User-Agent", YoutubeClientUtils.UserAgent);
        client.DefaultRequestHeaders.Add("Accept", "*/*");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        return client;
    }

    /// <summary>
    /// Создаёт HTTP Range request для частичной загрузки.
    /// </summary>
    public static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        return request;
    }

    /// <summary>
    /// Получает Content-Length через HEAD request.
    /// </summary>
    public static async Task<long> GetContentLengthAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch
        {
            return -1;
        }
    }
}