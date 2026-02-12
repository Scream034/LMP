using System.Net.Http.Headers;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Videos;

internal class VideoController(HttpClient http)
{
    private string? _visitorData;

    protected HttpClient Http { get; } = http;

    private async ValueTask<string> ResolveVisitorDataAsync(
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_visitorData))
            return _visitorData;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://www.youtube.com/sw.js_data"
        );

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("User-Agent", YoutubeClientUtils.UaWeb);

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        if (jsonString.StartsWith(")]}'"))
            jsonString = jsonString[4..];

        var json = Json.Parse(jsonString);

        var value = json[0][2][0][0][13].GetStringOrNull();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new YoutubeExplodeException("Failed to resolve visitor data.");
        }

        return _visitorData = value;
    }

    public async ValueTask<VideoWatchPage> GetVideoWatchPageAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        for (var retriesRemaining = 5; ; retriesRemaining--)
        {
            var watchPage = VideoWatchPage.TryParse(
                await Http.GetStringAsync(
                    $"https://www.youtube.com/watch?v={videoId}&bpctr=9999999999",
                    cancellationToken
                )
            );

            if (watchPage is null)
            {
                if (retriesRemaining > 0)
                    continue;

                throw new YoutubeExplodeException(
                    "Video watch page is broken. Please try again in a few minutes."
                );
            }

            if (!watchPage.IsAvailable)
                throw new VideoUnavailableException($"Video '{videoId}' is not available.");

            return watchPage;
        }
    }

    /// <summary>
    /// Стандартный запрос с текущим профилем клиента.
    /// </summary>
    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var clientName = YoutubeClientUtils.CurrentProfile.ToString().ToUpperInvariant();
        if (clientName == "ANDROIDVR") clientName = "ANDROID_VR";

        return await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);
    }

    /// <summary>
    /// Запрос с конкретным клиентом.
    /// </summary>
    public async ValueTask<PlayerResponse> GetPlayerResponseWithClientAsync(
        VideoId videoId,
        string clientName,
        CancellationToken cancellationToken = default,
        string? signatureTimestamp = null)
    {
        Log.Info($"GetPlayerResponse START ({clientName}): {videoId}");

        var visitorData = await ResolveVisitorDataAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/player"
        );

        // Флаг для Handler'а
        request.Options.Set(YoutubeHttpHandler.IsPlayerContext, true);

        // Устанавливаем User-Agent для конкретного клиента
        request.Headers.Remove("User-Agent");
        request.Headers.Add("User-Agent", YoutubeClientUtils.GetUserAgentForClient(clientName));

        string jsonBody = YoutubeClientUtils.GeneratePlayerContextForClient(
            clientName,
            videoId.Value,
            visitorData,
            signatureTimestamp
        );

        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"[VideoController] [{videoId}] {clientName} HTTP {(int)response.StatusCode}");
            throw new YoutubeExplodeException($"HTTP {response.StatusCode} for client {clientName}");
        }

        return PlayerResponse.Parse(content);
    }

    /// <summary>
    /// Пробует получить PlayerResponse с работающими стримами.
    /// НЕ использует IOS для обычных стримов.
    /// </summary>
    public async ValueTask<(PlayerResponse Response, string ClientName)> GetPlayerResponseWithFallbackAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Пробуем клиенты для обычных стримов (без IOS!)
        foreach (var clientName in YoutubeClientUtils.StreamFallbackClients)
        {
            try
            {
                var response = await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);

                if (response.IsPlayable && response.Streams.Any())
                {
                    Log.Info($"[VideoController] [{videoId}] SUCCESS with {clientName}");
                    return (response, clientName);
                }

                var error = response.PlayabilityError ?? "Not playable / No streams";
                Log.Warn($"[VideoController] [{videoId}] {clientName}: {error}");
                errors.Add($"{clientName}: {error}");
            }
            catch (Exception ex)
            {
                Log.Warn($"[VideoController] [{videoId}] {clientName} exception: {ex.Message}");
                errors.Add($"{clientName}: {ex.Message}");
            }
        }

        var allErrors = string.Join("; ", errors);
        throw new VideoUnplayableException($"All stream clients failed for {videoId}: {allErrors}");
    }

    /// <summary>
    /// Получает HLS манифест URL. IOS в приоритете.
    /// </summary>
    public async ValueTask<string?> GetHlsManifestUrlAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        foreach (var clientName in YoutubeClientUtils.HlsFallbackClients)
        {
            try
            {
                var response = await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);

                var hlsUrl = response.HlsManifestUrl;
                if (!string.IsNullOrEmpty(hlsUrl))
                {
                    Log.Info($"[VideoController] [{videoId}] HLS found via {clientName}");
                    return hlsUrl;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[VideoController] [{videoId}] HLS via {clientName} failed: {ex.Message}");
            }
        }

        Log.Warn($"[VideoController] [{videoId}] No HLS manifest available from any client");
        return null;
    }

    /// <summary>
    /// Fallback с signatureTimestamp (для age-restricted).
    /// </summary>
    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        string? signatureTimestamp,
        CancellationToken cancellationToken = default)
    {
        // Используем TVHTML5 для age-restricted
        return await GetPlayerResponseWithClientAsync(
            videoId,
            "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
            cancellationToken,
            signatureTimestamp
        );
    }
}