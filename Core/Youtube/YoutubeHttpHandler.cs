using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

public partial class YoutubeHttpHandler : ClientDelegatingHandler
{
    private readonly CookieAuthService? _authService;
    
    // Используем константы Muzza, но структуру заголовков Браузера
    public const string MuzzaUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
    // Версия клиента чуть новее (из твоего лога браузера), но можно оставить и Muzza.
    // Лучше использовать Muzza версию, так как мы эмулируем ее поведение.
    public const string MusicClientVersion = "1.20251227.01.00"; 
    public const string MusicClientName = "67"; 
    
    private const string MusicOrigin = "https://music.youtube.com";
    private const string MusicReferer = "https://music.youtube.com/";
    private const string YoutubeOrigin = "https://www.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");

    public YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false) 
        : base(http, disposeClient)
    {
        _authService = authService;
    }

    /// <summary>
    /// Генерирует полный заголовок Authorization как в браузере:
    /// SAPISIDHASH ... SAPISID1PHASH ... SAPISID3PHASH ...
    /// </summary>
    private string? GetFullAuthHeader(string origin)
    {
        if (_authService == null || !_authService.IsAuthenticated) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sb = new StringBuilder();

        // Helper для хеширования
        string Hash(string cookieName, string prefix)
        {
            var value = _authService.GetCookieValue(cookieName);
            if (string.IsNullOrEmpty(value)) return "";

            // Payload: TIMESTAMP + " " + VALUE + " " + ORIGIN
            var payload = $"{timestamp} {value} {origin}";
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var hashHex = Convert.ToHexStringLower(hashBytes);
            
            return $"{prefix} {timestamp}_{hashHex}";
        }

        // 1. SAPISID (Standard)
        var sapisidHash = Hash("SAPISID", "SAPISIDHASH");
        if (!string.IsNullOrEmpty(sapisidHash)) sb.Append(sapisidHash);

        // 2. 1PAPISID (Secure 1P)
        var onePHash = Hash("__Secure-1PAPISID", "SAPISID1PHASH");
        if (!string.IsNullOrEmpty(onePHash)) 
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(onePHash);
        }

        // 3. 3PAPISID (Secure 3P)
        var threePHash = Hash("__Secure-3PAPISID", "SAPISID3PHASH");
        if (!string.IsNullOrEmpty(threePHash))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(threePHash);
        }

        return sb.ToString();
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        var host = request.RequestUri.Host;
        bool isMusic = host.Contains("music.youtube.com");
        bool isYoutubeApi = host.Contains("youtube.com") && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        // --- 1. Basic Headers ---
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", MuzzaUserAgent);
        
        if (!request.Headers.Contains("Accept-Language"))
             request.Headers.Add("Accept-Language", "ru,en;q=0.9"); // Как в браузере

        // --- 2. Cookies ---
        if (_authService != null && _authService.IsAuthenticated)
        {
            var cookieHeader = _authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // --- 3. Music API Headers ---
        if (isMusic)
        {
            // Clean
            request.Headers.Remove("X-YouTube-Client-Name");
            request.Headers.Remove("X-YouTube-Client-Version");
            request.Headers.Remove("Referer");
            request.Headers.Remove("Origin");
            request.Headers.Remove("X-Origin");
            request.Headers.Remove("X-Goog-AuthUser");

            // Set
            request.Headers.Add("Referer", MusicReferer);
            request.Headers.Add("X-YouTube-Client-Name", MusicClientName);
            request.Headers.Add("X-YouTube-Client-Version", MusicClientVersion);
            request.Headers.Add("X-Goog-Api-Format-Version", "1");
            request.Headers.Add("Origin", MusicOrigin);
            request.Headers.Add("X-Origin", MusicOrigin);
            
            // Critical for auth stability
            if (_authService?.IsAuthenticated == true)
            {
                request.Headers.Add("X-Goog-AuthUser", "0");
            }

            // Visitor Data
            if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
            {
                if (request.Headers.Contains("X-Goog-Visitor-Id")) request.Headers.Remove("X-Goog-Visitor-Id");
                request.Headers.Add("X-Goog-Visitor-Id", visitorData);
            }
        }

        // --- 4. Authorization Hash ---
        if (request.Method == HttpMethod.Post && isYoutubeApi)
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var authHeader = GetFullAuthHeader(origin); // Генерируем полный хедер
            
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                if (request.Headers.Contains("Authorization")) request.Headers.Remove("Authorization");
                request.Headers.Add("Authorization", authHeader);
            }
        }

        return request;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var requestClone = await CloneRequestAsync(request);
            var processedRequest = HandleRequest(requestClone);

            try
            {
                var response = await base.SendAsync(processedRequest, cancellationToken);

                // Проверяем обновление кук
                bool cookiesUpdated = false;
                if (_authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                {
                    cookiesUpdated = _authService.UpdateCookies(newCookies);
                }

                // === 401 HANDLING ===
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Attempt {i+1}/3).");

                    if (cookiesUpdated)
                    {
                        Log.Info("[YouTube] New cookies received. Retrying immediately...");
                        await Task.Delay(200, cancellationToken);
                        continue; 
                    }
                    else if (_authService != null)
                    {
                        // !!! КЛЮЧЕВОЙ МОМЕНТ !!!
                        // Если 401 и нет новых кук, значит наши TS-токены протухли,
                        // и сервер отказался их обновлять автоматически.
                        // Мы удаляем их вручную и пробуем снова. Сервер увидит SID/SSID и выдаст новые.
                        Log.Warn("[YouTube] No new cookies. Stripping session tokens (__Secure-1PSIDTS) to force refresh...");
                        
                        _authService.RemoveSessionTokens();
                        
                        // Даем чуть больше времени перед повтором
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }
                }

                if ((int)response.StatusCode == 429)
                {
                    Log.Warn("[YouTube] 429 Too Many Requests. Retrying...");
                    await Task.Delay(2000, cancellationToken);
                    continue; 
                }

                return response;
            }
            catch (HttpRequestException ex) when (i < 2)
            {
                Log.Warn($"[YouTube] Network error: {ex.Message}. Retrying...");
                await Task.Delay(1000 * (i + 1), cancellationToken);
            }
        }
        
        throw new YoutubeExplodeException("Failed to complete request after retries (Auth failed).");
    }

    private async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            
        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object>(option.Key), option.Value);

        if (request.Content != null)
        {
            var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            if (request.Content.Headers.ContentType != null)
                clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }

        return clone;
    }

    [GeneratedRegex(@"(?:^|;\s*)SAPISID=([^;]+)")]
    private static partial Regex SapisidRegex();
}