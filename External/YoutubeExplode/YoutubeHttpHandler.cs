using System.Net;
using System.Security.Cryptography;
using System.Text;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;

namespace YoutubeExplode;

public class YoutubeHttpHandler : ClientDelegatingHandler
{
    private readonly CookieContainer _cookieContainer;
    
    // КОНСТАНТЫ ИЗ MUZZA
    private const string MuzzaUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
    public const string MusicClientVersion = "1.20251227.01.00";
    public const string MusicClientName = "67";
    private const string MusicOrigin = "https://music.youtube.com";

    public YoutubeHttpHandler(
       HttpClient http,
       CookieContainer cookieContainer,
       bool disposeClient = false
   )
       : base(http, disposeClient)
    {
        _cookieContainer = cookieContainer;
    }

    private string? GetAuthHeader()
    {
        // 1. Пытаемся найти SAPISID во всех доменах, где он может быть
        // YouTube может обновить куку на домене .youtube.com, а мы шлем запрос на music.youtube.com
        var sapisid = GetCookieValue("SAPISID");

        if (string.IsNullOrWhiteSpace(sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        // Формула: timestamp + пробел + sapisid + пробел + origin
        // Важно: Origin строго https://music.youtube.com (без слеша в конце)
        var payload = $"{timestamp} {sapisid} {MusicOrigin}";

        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        return $"SAPISIDHASH {timestamp}_{hashHex}";
    }

    // Хелпер для поиска куки по разным доменам
    private string? GetCookieValue(string name)
    {
        // Сначала ищем на music.youtube.com
        var c = _cookieContainer.GetCookies(new Uri(MusicOrigin))[name]?.Value;
        if (!string.IsNullOrWhiteSpace(c)) return c;

        // Если нет, ищем на .youtube.com (глобальный домен)
        c = _cookieContainer.GetCookies(new Uri("https://youtube.com"))[name]?.Value;
        if (!string.IsNullOrWhiteSpace(c)) return c;

        return null;
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        // 1. Принудительно ставим User-Agent как в Muzza (Firefox)
        // Это важно, так как Client Version 1.2025... соответствует именно десктопному браузеру
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", MuzzaUserAgent);

        // 2. Origin
        if (!request.Headers.Contains("Origin"))
            request.Headers.Add("Origin", MusicOrigin);

        // 3. Логика Music
        bool isMusic = request.RequestUri.Host.Contains("music.youtube.com");
        if (isMusic)
        {
            // Чистим заголовки
            request.Headers.Remove("x-youtube-client-name");
            request.Headers.Remove("x-youtube-client-version");
            request.Headers.Remove("Referer");
            request.Headers.Remove("X-Goog-Api-Format-Version");
            request.Headers.Remove("X-Goog-AuthUser");

            // Ставим правильные заголовки
            request.Headers.Add("Referer", "https://music.youtube.com/");
            request.Headers.Add("x-youtube-client-name", MusicClientName);
            request.Headers.Add("x-youtube-client-version", MusicClientVersion);
            request.Headers.Add("X-Goog-Api-Format-Version", "1");

            // Visitor Data
            if (request.Properties.TryGetValue("VisitorData", out var visitorDataObj) && visitorDataObj is string visitorData)
            {
                 if (!request.Headers.Contains("X-Goog-Visitor-Id"))
                    request.Headers.Add("X-Goog-Visitor-Id", visitorData);
            }

            // Авторизация
            if (request.Method == HttpMethod.Post)
            {
                var authHeader = GetAuthHeader();
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (request.Headers.Contains("Authorization")) request.Headers.Remove("Authorization");
                    request.Headers.Add("Authorization", authHeader);
                }
            }
        }

        return request;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                var response = await base.SendAsync(HandleRequest(request), cancellationToken);
                
                if ((int)response.StatusCode == 429)
                    throw new RequestLimitExceededException("YouTube returned 429.");
                
                return response;
            }
            catch (HttpRequestException) when (i < 2)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
        throw new YoutubeExplodeException("Failed to send request.");
    }
}