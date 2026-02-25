using System.Net;
using System.Net.Http.Headers;

namespace LMP.Core.Audio.Http;

/// <summary>
/// Shared HttpClient для всей аудио-системы.
/// Singleton с правильной конфигурацией для стриминга.
/// </summary>
public static class SharedHttpClient
{
    private static readonly Lazy<HttpClient> _instance = new(CreateClient);
    
    private const int PooledConnectionLifetimeMinutes = 15;
    private const int PooledConnectionIdleTimeoutMinutes = 5;
    private const int MaxConnectionsPerServer = 10;
    private const int TimeoutSeconds = 30;
    
    private const string UserAgent = 
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    
    /// <summary>
    /// Shared HttpClient instance.
    /// </summary>
    public static HttpClient Instance => _instance.Value;
    
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(PooledConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(PooledConnectionIdleTimeoutMinutes),
            MaxConnectionsPerServer = MaxConnectionsPerServer,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            
            // Отключаем cookies для стриминга
            UseCookies = false
        };
        
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        };
        
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("Accept", "*/*");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        
        return client;
    }
    
    /// <summary>
    /// Создаёт Range request для частичной загрузки.
    /// </summary>
    public static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
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
            using var response = await Instance.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Content.Headers.ContentType?.MediaType;
        }
        catch
        {
            return null;
        }
    }
}