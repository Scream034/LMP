using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Youtube.Utils; // Добавьте если используете UserAgent из Utils

namespace LMP.Core.Audio.Http;

/// <summary>
/// Shared HttpClient для всей аудио-системы.
/// </summary>
public static class SharedHttpClient
{
    private static readonly Lazy<HttpClient> _instance = new(CreateClient);

    private const int PooledConnectionLifetimeMinutes = 15;
    private const int PooledConnectionIdleTimeoutMinutes = 5;
    private const int MaxConnectionsPerServer = 10;
    private const int TimeoutSeconds = 30;

    public static HttpClient Instance => _instance.Value;

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            EnableMultipleHttp2Connections = false,
            ResponseDrainTimeout = TimeSpan.Zero,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var client = new HttpClient(handler)
        {
            // ═══ TIMEOUT вместо CancellationToken ═══
            // Это graceful timeout — не вызывает unhandled exceptions
            Timeout = TimeSpan.FromSeconds(30),

            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "*/*");

        return client;
    }

    /// <summary>
    /// Создаёт Range request для частичной загрузки.
    /// </summary>
    public static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        return request;
    }

    /// <summary>
    /// Получает Content-Length для URL.
    /// </summary>
    public static async Task<long> GetContentLengthAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);

            // Здесь тоже HTTP/1.1 для надежности
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

            using var response = await Instance.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Получает Content-Type для URL.
    /// </summary>
    public static async Task<string?> GetContentTypeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);

            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

            using var response = await Instance.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Content.Headers.ContentType?.MediaType;
        }
        catch
        {
            return null;
        }
    }
}