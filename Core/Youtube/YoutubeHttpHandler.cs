using System.Globalization;
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

    // --- Константы ---
    // Chrome 142 (Контекст Web)
    public const string UserAgentWeb = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    // Android 11 (Контекст плеера)
    public const string UserAgentAndroid = "com.google.android.youtube/20.10.38 (Linux; U; ANDROID 11) gzip";

    // Клиенты
    public const string MusicClientVersion = "1.20260126.03.00";
    public const string MusicClientName = "67";
    public const string WebClientVersion = "2.20260126.01.00";
    public const string WebClientName = "1";

    private const string MusicOrigin = "https://music.youtube.com";
    private const string YoutubeOrigin = "https://www.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");
    // Маркер для обработчика, указывающий, что это запрос к Android API (не отправлять хеш SAPISID)
    public static readonly HttpRequestOptionsKey<bool> IsAndroidContextKey = new("IsAndroidContext");

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

        // Проверка, помечен ли запрос как Android (Плеер/Стрим)
        bool isAndroid = request.Options.TryGetValue(IsAndroidContextKey, out var androidVal) && androidVal;

        // 1. User-Agent
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", isAndroid ? UserAgentAndroid : UserAgentWeb);

        if (!request.Headers.Contains("Accept-Language"))
            request.Headers.Add("Accept-Language", "en,ru;q=0.9");

        // 2. Куки (внедряем глобально, но осторожно с Android)
        // Android-клиент обычно использует OAuth, но передача куки часто позволяет проверить статус Premium.
        // Если это сломает воспроизведение, возможно, придется исключить их для isAndroid.
        // На данный момент оставляем, так как они нужны для истории просмотров.
        if (_authService != null && _authService.IsAuthenticated)
        {
            var cookieHeader = _authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // 3. Заголовки специфичные для Web/Music
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

            if (_authService?.IsAuthenticated == true)
                request.Headers.Add("X-Goog-AuthUser", "0");
        }
        else if (!isAndroid) // Стандартный контекст WEB
        {
            // Опционально: Добавляем Origin для стандартного YouTube
            if (request.Method == HttpMethod.Post)
            {
                if (!request.Headers.Contains("Origin")) request.Headers.Add("Origin", YoutubeOrigin);
            }
        }

        // 4. Visitor Data
        if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
        {
            if (request.Headers.Contains("X-Goog-Visitor-Id")) request.Headers.Remove("X-Goog-Visitor-Id");
            request.Headers.Add("X-Goog-Visitor-Id", visitorData);
        }

        // 5. Хеш авторизации (SAPISIDHASH)
        // КРИТИЧНО: НЕ отправлять это для Android-запросов, это вызывает ошибку 400 Bad Request
        bool isYoutubeApi = host.Contains("youtube.com") && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        if (request.Method == HttpMethod.Post && isYoutubeApi && !isAndroid)
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var sapisid = _authService?.GetCookieValue("SAPISID");

            // Добавляем заголовок авторизации только если есть кука SAPISID
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
        if (_authService == null || !_authService.IsAuthenticated) return null;

        Log.Info("[YouTube] Попытка восстановления сессии через sw.js_data...");

        try
        {
            // Целевой URL — основной YouTube, как в логе
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");

            // Заголовки в точности из лога
            request.Headers.Add("User-Agent", UserAgentWeb);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("Accept-Language", "ru,en;q=0.9");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");
            request.Headers.Add("Priority", "u=1, i");
            // Важно: Referer для восстановления на основном домене
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

            // Внедряем куки для восстановления (исключая просроченные токены сессии)
            var resurrectionCookies = _authService.GetResurrectionCookieHeader();
            if (!string.IsNullOrEmpty(resurrectionCookies))
            {
                request.Headers.Add("Cookie", resurrectionCookies);
            }

            // Отправляем запрос напрямую (в обход HandleRequest, чтобы избежать дублирования заголовков)
            var response = await base.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            bool sessionRefreshed = false;
            if (response.Headers.TryGetValues("Set-Cookie", out var newCookies))
            {
                sessionRefreshed = _authService.UpdateCookies(newCookies);
            }

            // Извлечение VisitorData
            string? visitorData = null;
            // Формат в логе — JSON-массив, начинающийся с )]}'
            // Ищем паттерн Cgt..., который обычно является base64-строкой visitor data protobuf
            var match = Regex.Match(content, @"Cg[A-Za-z0-9%_\-]{40,}");
            if (match.Success) visitorData = match.Value;

            if (sessionRefreshed || !string.IsNullOrEmpty(visitorData))
            {
                Log.Info("[YouTube] Сессия успешно восстановлена.");
                return visitorData;
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
                if (_authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                {
                    cookiesUpdated = _authService.UpdateCookies(newCookies);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Попытка {i + 1}).");

                    if (cookiesUpdated)
                    {
                        // Куки обновлены самим ответом 401? Повторяем немедленно.
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }
                    else if (_authService != null)
                    {
                        // Сессия мертва. Пробуем восстановление.
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