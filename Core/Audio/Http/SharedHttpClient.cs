using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Http;

public static class SharedHttpClient
{
    private static readonly Lazy<HttpClient> _instance = new(CreateClient);

    public static HttpClient Instance => _instance.Value;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            
            // Сброшено в Zero для мгновенной отмены сетевых потоков.
            // Предотвращает удержание потоков ThreadPool и исключает микро-лаги UI при быстром скраббинге.
            ResponseDrainTimeout = TimeSpan.Zero,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25),

            // Использование HTTP/2.0 позволяет мультиплексировать запросы, 
            // сохраняя SSL-соединения живыми при частых отменах Seek-запросов.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        client.DefaultRequestHeaders.Add("Accept", "*/*");

        Log.Debug($"[SharedHttpClient] Created: HTTP/{client.DefaultRequestVersion}, " +
                  $"policy={client.DefaultVersionPolicy}");

        return client;
    }

    /// <summary>
    /// Создаёт Range request с User-Agent, соответствующим клиенту из URL.
    /// </summary>
    public static HttpRequestMessage CreateRangeRequest(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Version = HttpVersion.Version20;

        // UA из параметра c= в URL
        ApplyUserAgentFromUrl(request, url);

        return request;
    }

    /// <summary>
    /// Определяет User-Agent по параметру c= в URL (WEB_REMIX, ANDROID_VR и т.д.).
    /// Если c= отсутствует — используем WebRemix UA как безопасный дефолт.
    /// </summary>
    public static void ApplyUserAgentFromUrl(HttpRequestMessage request, string url)
    {
        var clientParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var ua = clientParam is not null
            ? YoutubeClientUtils.GetUserAgentForClient(clientParam)
            : YoutubeClientUtils.UaWebRemix; // безопасный дефолт

        request.Headers.TryAddWithoutValidation("User-Agent", ua);
    }

    public static async Task<long> GetContentLengthAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Version = HttpVersion.Version20;
            ApplyUserAgentFromUrl(request, url);

            using var response = await Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    public static async Task<string?> GetContentTypeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Version = HttpVersion.Version20;
            ApplyUserAgentFromUrl(request, url);

            using var response = await Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.Content.Headers.ContentType?.MediaType;
        }
        catch
        {
            return null;
        }
    }
}