using System.Net;
using System.Security.Cryptography;
using System.Text;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Utils;

namespace YoutubeExplode;

internal class YoutubeHttpHandler : ClientDelegatingHandler
{
    private readonly string _manualCookieHeader;
    private readonly string? _sapisid;
    private readonly bool _hasUserCookies;
    private readonly string _userAgent; // Поле для хранения UA

    private const string MusicClientVersion = "1.20260121.03.00";

    public YoutubeHttpHandler(
       HttpClient http,
       IReadOnlyList<Cookie> initialCookies,
       string userAgent,
       bool disposeClient = false
   )
       : base(http, disposeClient)
    {
        _userAgent = userAgent; // Сохраняем переданный UA

        var sb = new StringBuilder();
        var baseCookies = new Dictionary<string, string>
        {
            { "SOCS", "CAISEwgDEgk4MTM4MzYzNTIaAmVuIAEaBgiApPzGBg" },
            { "CONSENT", "YES+" }
        };

        foreach (var cookie in initialCookies)
        {
            baseCookies[cookie.Name] = cookie.Value;
            if (cookie.Name.Equals("__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase))
                _sapisid = cookie.Value;
            else if (_sapisid == null && cookie.Name.Equals("SAPISID", StringComparison.OrdinalIgnoreCase))
                _sapisid = cookie.Value;
        }

        foreach (var kvp in baseCookies)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"{kvp.Key}={kvp.Value}");
        }

        _manualCookieHeader = sb.ToString();
        _hasUserCookies = initialCookies.Count > 0;
    }

    private string? GenerateAuthHeader(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(_sapisid)) return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Убедитесь, что системное время на компьютере верное! 
        // SAPISIDHASH очень чувствителен к времени.

        var origin = $"{uri.Scheme}://{uri.Host}";
        var payload = $"{timestamp} {_sapisid} {origin}";

        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        return $"SAPISIDHASH {timestamp}_{hashHex}";
    }

    private HttpRequestMessage HandleRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return request;

        // 1. Cookies
        if (request.Headers.Contains("Cookie")) request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", _manualCookieHeader);

        // 2. API Key
        if (request.RequestUri.AbsolutePath.StartsWith("/youtubei/", StringComparison.Ordinal)
            && !UrlEx.ContainsQueryParameter(request.RequestUri.Query, "key"))
        {
            var uriBuilder = new UriBuilder(request.RequestUri);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            query["key"] = "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w";
            uriBuilder.Query = query.ToString();
            request.RequestUri = uriBuilder.Uri;
        }

        // 3. Origin
        if (!request.Headers.Contains("Origin"))
            request.Headers.Add("Origin", $"{request.RequestUri.Scheme}://{request.RequestUri.Host}");

        // 4. User-Agent (Динамический)
        if (request.Headers.Contains("User-Agent")) request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", _userAgent); // Используем поле класса

        // 5. Music API Logic
        bool isMusic = request.RequestUri.Host.Contains("music.youtube.com");
        if (isMusic)
        {
            request.Headers.Remove("x-youtube-client-name");
            request.Headers.Remove("x-youtube-client-version");
            request.Headers.Remove("Referer");

            request.Headers.Add("Referer", "https://music.youtube.com/");
            request.Headers.Add("x-youtube-client-name", "67");
            request.Headers.Add("x-youtube-client-version", MusicClientVersion);

            if (_hasUserCookies && request.Method == HttpMethod.Post)
            {
                if (!request.Headers.Contains("X-Goog-AuthUser"))
                    request.Headers.Add("X-Goog-AuthUser", "0");

                var authHeader = GenerateAuthHeader(request.RequestUri);
                if (!string.IsNullOrWhiteSpace(authHeader))
                {
                    if (request.Headers.Contains("Authorization")) request.Headers.Remove("Authorization");
                    request.Headers.Add("Authorization", authHeader);
                }
            }
        }

        return request;
    }

    private HttpResponseMessage HandleResponse(HttpResponseMessage response)
    {
        if ((int)response.StatusCode == 429)
            throw new RequestLimitExceededException("YouTube returned 429 (Too Many Requests).");
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                return HandleResponse(await base.SendAsync(HandleRequest(request), cancellationToken));
            }
            catch (HttpRequestException) when (i < 2)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
        throw new YoutubeExplodeException("Failed to send request after retries.");
    }
}