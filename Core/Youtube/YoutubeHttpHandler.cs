using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

public partial class YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false) : ClientDelegatingHandler(http, disposeClient)
{
    private readonly CookieAuthService? _authService = authService;
    
    // UPD: Данные взяты из твоего рабочего PowerShell лога (Chrome 142 + свежий клиент)
    public const string MuzzaUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    public const string MusicClientVersion = "1.20260126.03.00"; 
    public const string MusicClientName = "67"; 
    
    private const string MusicOrigin = "https://music.youtube.com";
    private const string MusicReferer = "https://music.youtube.com/";
    private const string YoutubeOrigin = "https://www.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");

    private static string? GetAuthHeader(string? sapisid, string origin)
    {
        if (string.IsNullOrWhiteSpace(sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{timestamp} {sapisid} {origin}";
        var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        var hashHex = Convert.ToHexStringLower(hashBytes);

        return $"SAPISIDHASH {timestamp}_{hashHex}";
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        var host = request.RequestUri.Host;
        bool isMusic = host.Contains("music.youtube.com");
        
        // Ставим User-Agent глобально
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", MuzzaUserAgent);
        
        if (!request.Headers.Contains("Accept-Language"))
             request.Headers.Add("Accept-Language", "ru,en;q=0.9");

        // Cookies
        if (_authService != null && _authService.IsAuthenticated)
        {
            var cookieHeader = _authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // Music Headers (WEB_REMIX)
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

        // Authorization Hash (только для API запросов)
        bool isYoutubeApi = host.Contains("youtube.com") && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");
        if (request.Method == HttpMethod.Post && isYoutubeApi)
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var sapisid = _authService?.GetCookieValue("SAPISID");
            var authHeader = GetAuthHeader(sapisid, origin);
            
            if (!string.IsNullOrWhiteSpace(authHeader))
            {
                if (request.Headers.Contains("Authorization")) request.Headers.Remove("Authorization");
                request.Headers.Add("Authorization", authHeader);
            }
        }

        return request;
    }

    /// <summary>
    /// Полная копия твоего PowerShell запроса к sw.js_data.
    /// Это гарантированно рабочий метод обновления сессии.
    /// </summary>
    private async Task<string?> TryRefreshSessionAsync(CancellationToken ct)
    {
        if (_authService == null || !_authService.IsAuthenticated) return null;

        Log.Info("[YouTube] Attempting session resurrection via sw.js_data (Chrome 142 emulation)...");

        try
        {
            // Используем эндпоинт из лога
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.youtube.com/sw.js_data");
            
            // 1. Chrome Headers (1-в-1 из твоего лога)
            request.Headers.Add("User-Agent", MuzzaUserAgent);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Accept-Language", "ru,en;q=0.9");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");
            request.Headers.Add("Priority", "u=1, i");
            request.Headers.Add("Referer", "https://music.youtube.com/sw.js"); // Важный реферер для этого эндпоинта
            
            // Sec headers
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            // Client Hints (Обязательны для Chrome 142)
            request.Headers.Add("sec-ch-ua", "\"Not_A Brand\";v=\"99\", \"Chromium\";v=\"142\"");
            request.Headers.Add("sec-ch-ua-mobile", "?0");
            request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");
            request.Headers.Add("sec-ch-ua-arch", "\"x86\"");
            request.Headers.Add("sec-ch-ua-bitness", "\"64\"");
            request.Headers.Add("sec-ch-ua-form-factors", "\"Desktop\"");
            request.Headers.Add("sec-ch-ua-full-version", "\"142.0.7444.243\""); // Из лога
            request.Headers.Add("sec-ch-ua-full-version-list", "\"Not_A Brand\";v=\"99.0.0.0\", \"Chromium\";v=\"142.0.7444.1338\"");

            // 2. Куки (Фильтрованные! Без старого 1PSIDTS)
            var resurrectionCookies = _authService.GetResurrectionCookieHeader();
            if (!string.IsNullOrEmpty(resurrectionCookies))
            {
                request.Headers.Add("Cookie", resurrectionCookies);
            }

            // 3. Отправка
            // Используем base.SendAsync, чтобы не сработал HandleRequest (он добавит лишние хедеры)
            var response = await base.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            
            // 4. Ловим новые куки
            bool sessionRefreshed = false;
            if (response.Headers.TryGetValues("Set-Cookie", out var newCookies))
            {
                // Тут должны прийти SIDCC, __Secure-1PSIDCC и т.д.
                sessionRefreshed = _authService.UpdateCookies(newCookies);
            }

            // 5. Парсим VisitorData из JSON ответа
            // Ответ начинается с )]}' поэтому мы ищем внутри
            string? visitorData = null;
            if (content.Contains("VISITOR_DATA") || content.Contains("visitorData"))
            {
                // Простая эвристика для поиска длинной base64 строки, похожей на VisitorData
                // В логе она: Cgt1SzNzT3dNUFphQSijx-bLBjIKCgJSVRIEGgAgM2Lf...
                var match = Regex.Match(content, @"Cg[A-Za-z0-9%_\-]{50,}");
                if (match.Success) visitorData = match.Value;
            }

            if (sessionRefreshed)
            {
                Log.Info("[YouTube] Session successfully resurrected via sw.js_data!");
                return visitorData;
            }
            else
            {
                // Если куки не пришли, но пришел VisitorData - это тоже может сработать
                if (!string.IsNullOrEmpty(visitorData))
                {
                    Log.Info("[YouTube] No new cookies, but fresh VisitorData obtained. Using it.");
                    return visitorData;
                }
                
                Log.Warn($"[YouTube] sw.js_data returned {response.StatusCode}, but no refresh tokens found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] Resurrection via sw.js_data failed: {ex.Message}");
        }

        return null;
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

                // Пассивное обновление
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
                        await Task.Delay(200, cancellationToken);
                        continue; 
                    }
                    else if (_authService != null)
                    {
                        Log.Warn("[YouTube] Initiating Chrome sw.js_data recovery...");
                        
                        var newVisitorData = await TryRefreshSessionAsync(cancellationToken);
                        
                        if (!string.IsNullOrEmpty(newVisitorData))
                        {
                            Log.Info($"[YouTube] Applying fresh VisitorData: {newVisitorData}");
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
            clone.Options.Set(new HttpRequestOptionsKey<object>(option.Key)!, option.Value);

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
}