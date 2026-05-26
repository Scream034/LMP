using System.Net;
using System.Net.Http.Headers;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers;

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
            ResponseDrainTimeout = TimeSpan.Zero,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),

            // RequestVersionOrHigher + Version20 = ТРЕБУЕТ HTTP/2, фейлит если сервер не поддерживает.
            // RequestVersionOrLower + Version20 = ПРОБУЕТ HTTP/2, фоллбэк на HTTP/1.1.
            // Это критично: PlayerContextManager скачивает iframe_api через этот клиент,
            // и если HTTP/2 недоступен — n-token вообще не расшифровывается.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // UA должен соответствовать параметру c= в URL (WEB_REMIX → Chrome UA).
        // Статический UA из YoutubeClientUtils.UserAgent зависит от CurrentProfile,
        // который может быть ANDROID_VR (из-за fallback порядка), а URL — от WEB_REMIX.
        // UA теперь ставится per-request в CreateChunkRequest / CreateRangeRequest.
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
        request.Version = HttpVersion.Version11;

        // ═══ UA из параметра c= в URL ═══
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
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Version = HttpVersion.Version11;
            ApplyUserAgentFromUrl(request, url);

            using var response = await Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
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
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Version = HttpVersion.Version11;
            ApplyUserAgentFromUrl(request, url);

            using var response = await Instance.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.Content.Headers.ContentType?.MediaType;
        }
        catch
        {
            return null;
        }
    }
}