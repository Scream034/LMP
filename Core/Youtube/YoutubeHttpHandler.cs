using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube;

public partial class YoutubeHttpHandler(HttpClient http, CookieAuthService? authService, bool disposeClient = false)
    : ClientDelegatingHandler(http, disposeClient)
{
    public const string MusicClientVersion = "1.20260126.03.00";
    public const string MusicClientName = "67";
    public const string WebClientVersion = "2.20260126.01.00";
    public const string WebClientName = "1";

    public const string MusicOrigin = "https://music.youtube.com";
    public const string YoutubeOrigin = "https://www.youtube.com";

    public static readonly HttpRequestOptionsKey<string> VisitorDataKey = new("VisitorData");
    public static readonly HttpRequestOptionsKey<bool> IsPlayerContext = new("IsPlayerContext");

    // Кэшированные байты для SAPISIDHASH — избегаем повторной аллокации
    private static readonly byte[] SapisidPrefix = " "u8.ToArray();

    public static string GetHl() => LocalizationService.Instance.CurrentLanguageCode ?? "en";

    public static string GetGl()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName ?? "US"; }
        catch { return "US"; }
    }

    /// <summary>
    /// Генерация SAPISIDHASH с использованием Span и stackalloc.
    /// </summary>
    private static string? GetAuthHeader(string? sapisid, string origin)
    {
        if (string.IsNullOrWhiteSpace(sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Формируем payload для хеширования
        var timestampStr = timestamp.ToString(CultureInfo.InvariantCulture);

        // Используем stackalloc для промежуточного буфера
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

        // UTF-8 encode в stackalloc буфер
        int utf8Len = Encoding.UTF8.GetByteCount(payloadChars);
        Span<byte> utf8Bytes = utf8Len <= 512
            ? stackalloc byte[utf8Len]
            : new byte[utf8Len];
        Encoding.UTF8.GetBytes(payloadChars, utf8Bytes);

        // SHA1 hash
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(utf8Bytes, hash);
        var hashHex = Convert.ToHexStringLower(hash);

        return string.Concat("SAPISIDHASH ", timestampStr, "_", hashHex);
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        var host = request.RequestUri.Host;
        bool isMusic = host.Contains("music.youtube.com");

        bool isPlayerRequest = request.Options.TryGetValue(IsPlayerContext, out var p) && p;

        // User-Agent
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");

        if (isPlayerRequest)
        {
            request.Headers.Add("User-Agent", YoutubeClientUtils.UserAgent);
            if (!YoutubeClientUtils.RequiresAuth)
                return request;
        }
        else
        {
            request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);
        }

        // Accept-Language
        if (!request.Headers.Contains("Accept-Language"))
            request.Headers.Add("Accept-Language", "en,ru;q=0.9");

        // Cookies
        if (authService is { IsAuthenticated: true })
        {
            var cookieHeader = authService.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", cookieHeader);
            }
        }

        // Music headers
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
        else if (!isPlayerRequest)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (!request.Headers.Contains("Origin"))
                    request.Headers.Add("Origin", YoutubeOrigin);
            }
        }

        // Visitor Data
        if (request.Options.TryGetValue(VisitorDataKey, out var visitorData) && !string.IsNullOrEmpty(visitorData))
        {
            if (request.Headers.Contains("X-Goog-Visitor-Id"))
                request.Headers.Remove("X-Goog-Visitor-Id");
            request.Headers.Add("X-Goog-Visitor-Id", visitorData);
        }

        // SAPISIDHASH Authorization
        bool isYoutubeApi = host.Contains("youtube.com") &&
                            request.RequestUri.AbsolutePath.Contains("/youtubei/v1/");

        if (request.Method == HttpMethod.Post && isYoutubeApi && !isPlayerRequest)
        {
            var origin = isMusic ? MusicOrigin : YoutubeOrigin;
            var sapisid = authService?.GetCookieValue("SAPISID");

            if (!string.IsNullOrWhiteSpace(sapisid))
            {
                var authHeader = GetAuthHeader(sapisid, origin);
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (request.Headers.Contains("Authorization"))
                        request.Headers.Remove("Authorization");
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

            var response = await base.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

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

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var requestClone = await CloneRequestAsync(request);
            var processedRequest = HandleRequest(requestClone);

            try
            {
                var response = await base.SendAsync(processedRequest, cancellationToken);

                bool cookiesUpdated = false;
                if (authService != null && response.Headers.TryGetValues("Set-Cookie", out var newCookies))
                    cookiesUpdated = authService.UpdateCookies(newCookies);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"[YouTube] 401 Unauthorized (Попытка {i + 1}).");

                    if (cookiesUpdated)
                    {
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    if (authService != null)
                    {
                        var newVisitorData = await TryRefreshSessionAsync(cancellationToken);
                        if (!string.IsNullOrEmpty(newVisitorData))
                            request.Options.Set(VisitorDataKey, newVisitorData);
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

    /// <summary>
    /// Оптимизированное клонирование запроса.
    /// Для Content используем ReadAsByteArrayAsync (одна копия) вместо CopyToAsync + MemoryStream.
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
            // ReadAsByteArrayAsync буферизует контент внутри — одна копия вместо MemoryStream
            var bytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);

            if (request.Content.Headers.ContentType != null)
                clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }

        return clone;
    }

    [GeneratedRegex(@"Cg[A-Za-z0-9%_\-]{40,}")]
    private static partial Regex VisitorExtractRegex();
}