using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

/// <summary>
/// Делегирующий обработчик HTTP-запросов для интеграции с YouTube API.
/// Настраивает заголовки авторизации, куки и контексты запросов.
/// </summary>
public partial class YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false)
    : ClientDelegatingHandler(http, disposeClient)
{
    /// <summary>Версия клиента YouTube Music.</summary>
    public const string MusicClientVersion = "1.20260126.03.00";
    
    /// <summary>Идентификатор клиента YouTube Music.</summary>
    public const string MusicClientName = "67";
    
    /// <summary>Версия клиента YouTube Web.</summary>
    public const string WebClientVersion = "2.20260126.01.00";
    
    /// <summary>Идентификатор клиента YouTube Web.</summary>
    public const string WebClientName = "1";

    /// <summary>Origin заголовок для YouTube Music.</summary>
    public const string MusicOrigin = "https://music.youtube.com";
    
    /// <summary>Origin заголовок для основного YouTube.</summary>
    public const string YoutubeOrigin = "https://www.youtube.com";

    /// <summary>Ключ параметров для передачи Visitor Data в запросе.</summary>
    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");
    
    /// <summary>Ключ параметров, указывающий на контекст запроса плеера.</summary>
    public static readonly HttpRequestOptionsKey<bool> IsPlayerContext = new("IsPlayerContext");
    
    /// <summary>Ключ параметров, указывающий на мобильный или ТВ клиент (без авторизации SAPISIDHASH).</summary>
    public static readonly HttpRequestOptionsKey<bool> IsMobileClient = new("IsMobileClient");

    /// <summary>Возвращает текущую языковую локаль системы.</summary>
    public static string GetHl() => LocalizationService.Instance.CurrentLanguageCode ?? "en";

    /// <summary>Возвращает двухбуквенный ISO-код региона системы.</summary>
    public static string GetGl()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName ?? "US"; }
        catch { return "US"; }
    }

    /// <summary>
    /// Генерация заголовка авторизации SAPISIDHASH с использованием Span и stackalloc для минимизации аллокаций.
    /// </summary>
    private static string? GetAuthHeader(string? sapisid, string origin)
    {
        if (string.IsNullOrWhiteSpace(sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestampStr = timestamp.ToString(CultureInfo.InvariantCulture);

        int payloadLen = timestampStr.Length + 1 + sapisid.Length + 1 + origin.Length;
        Span<char> payloadChars = payloadLen <= 256
            ? stackalloc char[payloadLen]
            : new char[payloadLen];

        int pos = 0;
        timestampStr.AsSpan().CopyTo(payloadChars[pos..]);
        pos += timestampStr.Length;
        payloadChars[pos++] = ' ';
        sapisid.AsSpan().CopyTo(payloadChars[pos..]);
        pos += sapisid.Length;
        payloadChars[pos++] = ' ';
        origin.AsSpan().CopyTo(payloadChars[pos..]);

        int utf8Len = Encoding.UTF8.GetByteCount(payloadChars);
        Span<byte> utf8Bytes = utf8Len <= 512
            ? stackalloc byte[utf8Len]
            : new byte[utf8Len];
        Encoding.UTF8.GetBytes(payloadChars, utf8Bytes);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(utf8Bytes, hash);
        var hashHex = Convert.ToHexStringLower(hash);

        return string.Concat("SAPISIDHASH ", timestampStr, "_", hashHex);
    }

    /// <summary>
    /// Настраивает заголовки, куки и параметры безопасности для исходящего запроса.
    /// </summary>
    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        var host = request.RequestUri.Host;
        bool isMusic = host.Contains("music.youtube.com");
        bool isPlayerRequest = request.Options.TryGetValue(IsPlayerContext, out var p) && p;
        bool isMobileClient = request.Options.TryGetValue(IsMobileClient, out var m) && m;

        // Определяем, относится ли домен к YouTube API (исключаем googlevideo.com)
        bool isYoutubeDomain = host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase);

        // User-Agent: НЕ добавляем если уже есть
        if (!request.Headers.Contains("User-Agent"))
        {
            request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);
        }

        if (!request.Headers.Contains("Accept-Language"))
            request.Headers.Add("Accept-Language", "en,ru;q=0.9");

        // ═══ КРИТИЧНОЕ Куки передаются ТОЛЬКО на домены *.youtube.com ═══
        // Это предотвращает отправку приватных кук на CDN-серверы googlevideo.com, 
        // убирая ложные 403 Forbidden блокировки при проверочных HEAD-запросах.
        if (isYoutubeDomain && !isMobileClient && authService is { IsAuthenticated: true })
        {
            var cookieHeader = authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie"))
                    request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // Заголовки для YouTube Music — только для НЕ-player запросов
        if (isMusic && !isPlayerRequest)
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
        else if (!isPlayerRequest && request.Method == HttpMethod.Post)
        {
            if (!request.Headers.Contains("Origin"))
                request.Headers.Add("Origin", YoutubeOrigin);
        }

        if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
        {
            if (request.Headers.Contains("X-Goog-Visitor-Id"))
                request.Headers.Remove("X-Goog-Visitor-Id");
            request.Headers.Add("X-Goog-Visitor-Id", visitorData);
        }

        // SAPISIDHASH — только для API на доменах *.youtube.com
        bool isYoutubeApi = isYoutubeDomain && request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        if (request.Method == HttpMethod.Post && 
            isYoutubeApi && 
            !isMobileClient && 
            authService?.IsAuthenticated == true)
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var sapisid = authService.GetCookieValue("SAPISID");

            if (!string.IsNullOrWhiteSpace(sapisid))
            {
                var authHeader = GetAuthHeader(sapisid, origin);
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (request.Headers.Contains("Authorization"))
                        request.Headers.Remove("Authorization");
                    request.Headers.Add("Authorization", authHeader);

                    if (request.Headers.Contains("Origin"))
                        request.Headers.Remove("Origin");
                    request.Headers.Add("Origin", origin);

                    if (request.Headers.Contains("X-Origin"))
                        request.Headers.Remove("X-Origin");
                    request.Headers.Add("X-Origin", origin);

                    if (!request.Headers.Contains("X-Goog-AuthUser"))
                        request.Headers.Add("X-Goog-AuthUser", "0");
                }
            }
        }

        return request;
    }

    /// <summary>
    /// Попытка асинсохронного обновления/восстановления протухшей сессии авторизации.
    /// </summary>
    private async Task<string?> TryRefreshSessionAsync(CancellationToken ct)
    {
        if (authService == null || !authService.IsAuthenticated) return null;

        Log.Info("[YouTube] Попытка восстановления сессии через sw.js_data...");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");

            request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("Accept-Language", "ru,en;q=0.9");
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");
            request.Headers.Add("Priority", "u=1, i");
            request.Headers.Add("Referer", "https://www.youtube.com/");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("sec-ch-ua", "\"Not_A Brand\";v=\"99\", \"Chromium\";v=\"142\"");
            request.Headers.Add("sec-ch-ua-mobile", "?0");
            request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");
            request.Headers.Add("sec-ch-ua-full-version", "\"142.0.0.0\"");

            var resurrectionCookies = authService.GetResurrectionCookieHeader();
            if (!string.IsNullOrEmpty(resurrectionCookies))
                request.Headers.Add("Cookie", resurrectionCookies);

            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            bool sessionRefreshed = false;
            if (response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                sessionRefreshed = authService.UpdateCookies(newCookies);

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

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var requestClone = await CloneRequestAsync(request).ConfigureAwait(false);
            var processedRequest = HandleRequest(requestClone);

            try
            {
                var response = await base.SendAsync(processedRequest, cancellationToken).ConfigureAwait(false);

                bool cookiesUpdated = false;
                if (authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                    cookiesUpdated = authService.UpdateCookies(newCookies);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Попытка {i + 1}).");

                    if (cookiesUpdated)
                    {
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (authService != null)
                    {
                        var newVisitorData = await TryRefreshSessionAsync(cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(newVisitorData))
                            request.Options.Set(VisitorDataKey, newVisitorData);
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                if ((int)response.StatusCode == 429)
                {
                    Log.Warn("[YouTube] 429 Too Many Requests. Ожидание...");
                    await Task.Delay(2000 + (i * 1000), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (i < 2)
            {
                Log.Warn($"[YouTube] Сетевая ошибка: {ex.Message}. Повтор...");
                await Task.Delay(1000 * (i + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new YoutubeExplodeException("Не удалось выполнить запрос после нескольких попыток.");
    }

    /// <summary>
    /// Клонирует HTTP-запрос для безопасных повторных отправлений при сбоях сети.
    /// </summary>
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
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);

            if (request.Content.Headers.ContentType != null)
                clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }

        return clone;
    }

    [GeneratedRegex(@"Cg[A-Za-z0-9%_\-]{40,}")]
    private static partial Regex VisitorExtractRegex();
}