using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

public partial class YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false) : ClientDelegatingHandler(http, disposeClient)
{
    // Клиенты
    public const string MusicClientVersion = "1.20260126.03.00";
    public const string MusicClientName = "67";
    public const string WebClientVersion = "2.20260126.01.00";
    public const string WebClientName = "1";

    public const string MusicOrigin = "https://music.youtube.com";
    public const string YoutubeOrigin = "https://www.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");
    public static readonly HttpRequestOptionsKey<bool> IsPlayerContext = new("IsPlayerContext");

    public static string GetHl() => LocalizationService.Instance.CurrentLanguageCode ?? "en";

    public static string GetGl()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName ?? "US"; }
        catch { return "US"; }
    }

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
        
        // Проверяем флаг, который мы ставим в VideoController (для плеера VR/TV)
        bool isPlayerRequest = request.Options.TryGetValue(IsPlayerContext, out var p) && p;

        // === 1. User-Agent ===
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");

        if (isPlayerRequest)
        {
            // Берем текущий UA из статического конфига (VR/TV/Web)
            request.Headers.Add("User-Agent", YoutubeClientUtils.UserAgent);

            // Для VR/TV клиентов — не отправляем куки и auth (они ломают запрос)
            if (!YoutubeClientUtils.RequiresAuth)
            {
                return request;
            }
        }
        else
        {
            // Обычные запросы (поиск, Music API, картинки) — всегда WEB UA
            request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);
        }

        // === 2. Accept-Language ===
        if (!request.Headers.Contains("Accept-Language"))
            request.Headers.Add("Accept-Language", "en,ru;q=0.9");

        // === 3. Куки (для авторизованных запросов) ===
        if (authService != null && authService.IsAuthenticated)
        {
            var cookieHeader = authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // === 4. Заголовки специфичные для Music ===
        if (isMusic)
        {
            request.Headers.Remove("X-YouTube-Client-Name");
            request.Headers.Remove("X-YouTube-Client-Version");
            request.Headers.Remove("Referer");
            request.Headers.Remove("Origin");
            request.Headers.Remove("X-Goog-AuthUser");

            request.Headers.Add("Referer", MusicOrigin);
            request.Headers.Add("X-YouTube-Client-Name", MusicClientName);
            request.Headers.Add("X-YouTube-Client-Version", MusicClientVersion);
            request.Headers.Add("X-Goog-Api-Format-Version", "1");
            request.Headers.Add("Origin", MusicOrigin);

            if (authService?.IsAuthenticated == true)
                request.Headers.Add("X-Goog-AuthUser", "0");
        }
        else if (!isPlayerRequest) // Стандартный WEB-контекст (не плеер)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (!request.Headers.Contains("Origin")) request.Headers.Add("Origin", YoutubeOrigin);
            }
        }

        // === 5. Visitor Data ===
        if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
        {
            if (request.Headers.Contains("X-Goog-Visitor-Id")) request.Headers.Remove("X-Goog-Visitor-Id");
            request.Headers.Add("X-Goog-Visitor-Id", visitorData);
        }

        // === 6. SAPISIDHASH Authorization ===
        // КРИТИЧНО: НЕ отправлять для Player-запросов (VR/TV), это ломает воспроизведение
        bool isYoutubeApi = host.Contains("youtube.com") && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        if (request.Method == HttpMethod.Post && isYoutubeApi && !isPlayerRequest)
        {
            // Выбираем правильный origin в зависимости от домена
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var sapisid = authService?.GetCookieValue("SAPISID");

            if (!string.IsNullOrWhiteSpace(sapisid))
            {
                var authHeader = GetAuthHeader(sapisid, origin);
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (request.Headers.Contains("Authorization")) request.Headers.Remove("Authorization");
                    request.Headers.Add("Authorization", authHeader);
                }
            }
        }

        return request;
    }

    private async Task<string?> TryRefreshSessionAsync(CancellationToken ct)
    {
        if (authService == null || !authService.IsAuthenticated) return null;

        Log.Info("[YouTube] Попытка восстановления сессии через sw.js_data...");

        try
        {
            // Целевой URL — основной YouTube
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");

            // Заголовки
            request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("Accept-Language", "ru,en;q=0.9");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");
            request.Headers.Add("Priority", "u=1, i");
            request.Headers.Add("Referer", "https://www.youtube.com/");

            // Sec заголовки
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            // Client Hints (Chrome 142)
            request.Headers.Add("sec-ch-ua", "\"Not_A Brand\";v=\"99\", \"Chromium\";v=\"142\"");
            request.Headers.Add("sec-ch-ua-mobile", "?0");
            request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");
            request.Headers.Add("sec-ch-ua-full-version", "\"142.0.0.0\"");

            // Куки для восстановления
            var resurrectionCookies = authService.GetResurrectionCookieHeader();
            if (!string.IsNullOrEmpty(resurrectionCookies))
            {
                request.Headers.Add("Cookie", resurrectionCookies);
            }

            // Отправляем напрямую (в обход HandleRequest)
            var response = await base.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            bool sessionRefreshed = false;
            if (response.Headers.TryGetValues("Set-Cookie", out var newCookies))
            {
                sessionRefreshed = authService.UpdateCookies(newCookies);
            }

            // Извлечение VisitorData
            string? newVisitorData = null;
            var match = VisitorExtractRegex().Match(content);
            if (match.Success) newVisitorData = match.Value;

            if (sessionRefreshed || !string.IsNullOrEmpty(newVisitorData))
            {
                Log.Info("[YouTube] Сессия успешно восстановлена.");
                return newVisitorData;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] Ошибка восстановления сессии: {ex.Message}");
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

                // Пассивное обновление куки
                bool cookiesUpdated = false;
                if (authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                {
                    cookiesUpdated = authService.UpdateCookies(newCookies);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Попытка {i + 1}).");

                    if (cookiesUpdated)
                    {
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }
                    else if (authService != null)
                    {
                        var newVisitorData = await TryRefreshSessionAsync(cancellationToken);
                        if (!string.IsNullOrEmpty(newVisitorData))
                        {
                            request.Options.Set(VisitorDataKey, newVisitorData);
                        }
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }
                }

                if ((int)response.StatusCode == 429)
                {
                    Log.Warn("[YouTube] 429 Too Many Requests. Ожидание...");
                    await Task.Delay(2000 + (i * 1000), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (i < 2)
            {
                Log.Warn($"[YouTube] Сетевая ошибка: {ex.Message}. Повтор...");
                await Task.Delay(1000 * (i + 1), cancellationToken);
            }
        }

        throw new YoutubeExplodeException("Не удалось выполнить запрос после нескольких попыток.");
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
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

    [GeneratedRegex(@"Cg[A-Za-z0-9%_\-]{40,}")]
    private static partial Regex VisitorExtractRegex();
}