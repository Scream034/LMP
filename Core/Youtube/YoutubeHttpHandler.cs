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
    
    // Константы Muzza
    public const string MuzzaUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";
    public const string MusicClientVersion = "1.20251227.01.00"; 
    public const string MusicClientName = "67"; 
    
    private const string MusicOrigin = "https://music.youtube.com";
    private const string MusicReferer = "https://music.youtube.com/";
    private const string YoutubeOrigin = "https://www.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");

    [GeneratedRegex("ytcfg\\.set\\s*\\(\\{.*\"VISITOR_DATA\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VisitorDataRegex();

    [GeneratedRegex("\"VISITOR_DATA\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VisitorDataSimpleRegex();

    public YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false) 
        : base(http, disposeClient)
    {
        _authService = authService;
    }

    // ВОЗВРАЩАЕМ КАК В MUZZA (Один хеш)
    private static string? GetAuthHeader(string? sapisid, string origin)
    {
        if (string.IsNullOrWhiteSpace(sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{timestamp} {sapisid} {origin}";
        
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hashHex = Convert.ToHexStringLower(hashBytes);

        return $"SAPISIDHASH {timestamp}_{hashHex}";
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        var host = request.RequestUri.Host;
        bool isMusic = host.Contains("music.youtube.com");
        bool isYoutubeApi = host.Contains("youtube.com") && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", MuzzaUserAgent);
        
        if (!request.Headers.Contains("Accept-Language"))
             request.Headers.Add("Accept-Language", "ru,en;q=0.9");

        // Cookies
        string? currentSapisid = null;
        if (_authService != null && _authService.IsAuthenticated)
        {
            var cookieHeader = _authService.GetCookieHeader();
            currentSapisid = _authService.GetCookieValue("SAPISID");

            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // Music Headers
        if (isMusic)
        {
            request.Headers.Remove("X-YouTube-Client-Name");
            request.Headers.Remove("X-YouTube-Client-Version");
            request.Headers.Remove("Referer");
            request.Headers.Remove("Origin");
            request.Headers.Remove("X-Origin");
            request.Headers.Remove("X-Goog-AuthUser");

            request.Headers.Add("Referer", MusicReferer);
            request.Headers.Add("X-YouTube-Client-Name", MusicClientName);
            request.Headers.Add("X-YouTube-Client-Version", MusicClientVersion);
            request.Headers.Add("X-Goog-Api-Format-Version", "1");
            request.Headers.Add("Origin", MusicOrigin);
            request.Headers.Add("X-Origin", MusicOrigin);
            
            if (_authService?.IsAuthenticated == true)
            {
                request.Headers.Add("X-Goog-AuthUser", "0");
            }

            if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
            {
                if (request.Headers.Contains("X-Goog-Visitor-Id")) request.Headers.Remove("X-Goog-Visitor-Id");
                request.Headers.Add("X-Goog-Visitor-Id", visitorData);
            }
        }

        // Authorization (Только один хеш, как в Muzza)
        if (request.Method == HttpMethod.Post && isYoutubeApi && !string.IsNullOrEmpty(currentSapisid))
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var authHeader = GetAuthHeader(currentSapisid, origin);
            
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                if (request.Headers.Contains("Authorization")) request.Headers.Remove("Authorization");
                request.Headers.Add("Authorization", authHeader);
            }
        }

        return request;
    }

    private async Task<string?> TryRefreshSessionAsync(CancellationToken ct)
    {
        if (_authService == null || !_authService.IsAuthenticated) return null;

        Log.Info("[YouTube] Attempting session resurrection (GET music.youtube.com)...");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.youtube.com/");
            
            request.Headers.Add("User-Agent", MuzzaUserAgent);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "ru,en;q=0.9");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");
            
            // ВАЖНО: Используем новый метод, который включает LOGIN_INFO
            var baseCookies = _authService.GetBaseCookieHeader();
            if (!string.IsNullOrEmpty(baseCookies))
            {
                request.Headers.Add("Cookie", baseCookies);
            }

            var response = await base.SendAsync(request, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            
            bool cookiesFound = false;
            if (response.Headers.TryGetValues("Set-Cookie", out var newCookies))
            {
                cookiesFound = _authService.UpdateCookies(newCookies);
            }

            if (cookiesFound) Log.Info("[YouTube] Session resurrected! New tokens received.");
            else Log.Warn($"[YouTube] Resurrection request finished, but NO cookies found.");

            var match = VisitorDataRegex().Match(html);
            if (!match.Success) match = VisitorDataSimpleRegex().Match(html);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] Session resurrection failed: {ex.Message}");
            return null;
        }
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

                bool cookiesUpdated = false;
                if (_authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                {
                    cookiesUpdated = _authService.UpdateCookies(newCookies);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Attempt {i+1}/3).");

                    if (cookiesUpdated)
                    {
                        Log.Info("[YouTube] New cookies received automatically. Retrying...");
                        await Task.Delay(200, cancellationToken);
                        continue; 
                    }
                    else if (_authService != null)
                    {
                        Log.Warn("[YouTube] No new cookies. Initiating recovery protocol...");
                        
                        _authService.RemoveSessionTokens();
                        var newVisitorData = await TryRefreshSessionAsync(cancellationToken);
                        
                        if (!string.IsNullOrEmpty(newVisitorData))
                        {
                            // Обновляем VisitorData для следующей попытки
                            request.Options.Set(VisitorDataKey, newVisitorData);
                        }

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
        
        throw new YoutubeExplodeException("Failed to complete request after retries.");
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