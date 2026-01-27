using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

public partial class YoutubeHttpHandler : ClientDelegatingHandler
{
    private readonly string _rawCookieString; // Храним строку как константу для сессии
    private readonly string? _sapisid;        // Храним распарсенный ключ

    // КОНСТАНТЫ ИЗ MUZZA
    private const string MuzzaUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
    public const string MusicClientVersion = "1.20251227.01.00";
    public const string MusicClientName = "67";
    private const string MusicOrigin = "https://music.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");

    // Конструктор теперь принимает строку, а не контейнер
    public YoutubeHttpHandler(
       HttpClient http,
       string rawCookieString,
       bool disposeClient = false
   )
       : base(http, disposeClient)
    {
        _rawCookieString = rawCookieString;
        _sapisid = ParseSapisid(rawCookieString);
    }

    // Ручной парсинг SAPISID из строки
    private static string? ParseSapisid(string cookieString)
    {
        if (string.IsNullOrWhiteSpace(cookieString)) return null;
        
        // Ищем: либо в начале строки, либо после точки с запятой и пробела
        var match = SapisidRegex().Match(cookieString);
        return match.Success ? match.Groups[1].Value : null;
    }

    private string? GetAuthHeader()
    {
        if (string.IsNullOrWhiteSpace(_sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        // Формула авторизации Google
        var payload = $"{timestamp} {_sapisid} {MusicOrigin}";

        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        return $"SAPISIDHASH {timestamp}_{hashHex}";
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        // 1. User-Agent
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", MuzzaUserAgent);

        // 2. Origin
        if (!request.Headers.Contains("Origin"))
            request.Headers.Add("Origin", MusicOrigin);

        // 3. ВРУЧНУЮ добавляем Cookie
        // Мы делаем это здесь, потому что в HttpClientHandler.UseCookies будет false
        if (!string.IsNullOrWhiteSpace(_rawCookieString))
        {
            if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", _rawCookieString);
        }

        // 4. Логика для Music API
        bool isMusic = request.RequestUri.Host.Contains("music.youtube.com");
        if (isMusic)
        {
            request.Headers.Remove("x-youtube-client-name");
            request.Headers.Remove("x-youtube-client-version");
            request.Headers.Remove("Referer");
            request.Headers.Remove("X-Goog-Api-Format-Version");
            
            // Muzza headers
            request.Headers.Add("Referer", "https://music.youtube.com/");
            request.Headers.Add("x-youtube-client-name", MusicClientName);
            request.Headers.Add("x-youtube-client-version", MusicClientVersion);
            request.Headers.Add("X-Goog-Api-Format-Version", "1");

            // Visitor Data
            if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
            {
                if (!request.Headers.Contains("X-Goog-Visitor-Id"))
                    request.Headers.Add("X-Goog-Visitor-Id", visitorData);
            }

            // Авторизация (SAPISIDHASH)
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
                // Важно: мы не обрабатываем Set-Cookie в ответе, так как храним статичную сессию
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

    [GeneratedRegex(@"(?:^|;\s*)SAPISID=([^;]+)")]
    private static partial Regex SapisidRegex();
}