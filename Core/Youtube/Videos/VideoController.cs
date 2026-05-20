using System.Net.Http.Headers;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Единый контроллер для взаимодействия с API YouTube (получение данных о видео, PlayerResponse, DASH/HLS манифестов).
/// </summary>
internal partial class VideoController(HttpClient http, PlayerContextManager playerManager)
{
    #region Bot Detection State

    private static DateTime _lastBotDetection = DateTime.MinValue;
    private static int _consecutiveFailures;
    private static readonly SemaphoreSlim _requestThrottle = new(1, 1);
    private static readonly Lock _stateLock = new();

    public static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(2);

    public static bool IsInCooldown
    {
        get
        {
            lock (_stateLock)
            {
                if (_consecutiveFailures < 3) return false;
                var elapsed = DateTime.UtcNow - _lastBotDetection;
                return elapsed < CooldownDuration;
            }
        }
    }

    public static TimeSpan GetRemainingCooldown()
    {
        lock (_stateLock)
        {
            if (_consecutiveFailures < 3) return TimeSpan.Zero;

            var elapsed = DateTime.UtcNow - _lastBotDetection;
            var remaining = CooldownDuration - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public static void ResetBotDetectionState()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _lastBotDetection = DateTime.MinValue;
            Log.Info("[VideoController] Bot detection state reset");
        }
    }

    public static void ThrowIfInCooldown()
    {
        if (IsInCooldown)
        {
            var remaining = GetRemainingCooldown();
            throw new BotDetectionException(
                $"Rate limited by YouTube. Please wait {remaining.TotalSeconds:F0} seconds.",
                remaining);
        }
    }

    #endregion

    #region Throttle

    private static TimeSpan GetThrottleDelay()
    {
        int failures;
        lock (_stateLock) failures = _consecutiveFailures;

        return failures switch
        {
            0 => TimeSpan.FromMilliseconds(150),
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(3),
            _ => TimeSpan.FromSeconds(5)
        };
    }

    #endregion

    private readonly PlayerContextManager _playerManager = playerManager;
    private string? _visitorData;
    private string? _signatureTimestamp;

    protected HttpClient Http { get; } = http;

    #region Visitor Data

    private async ValueTask<string> ResolveVisitorDataAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_visitorData))
            return _visitorData;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data");
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
            throw new YoutubeExplodeException("Failed to resolve visitor data.");

        return _visitorData = value;
    }

    #endregion

    #region Watch Page

    public async ValueTask<VideoWatchPage> GetVideoWatchPageAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        for (var retriesRemaining = 5; ; retriesRemaining--)
        {
            var watchPage = VideoWatchPage.TryParse(
                await Http.GetStringAsync(
                    $"https://www.youtube.com/watch?v={videoId}&bpctr=9999999999",
                    cancellationToken));

            if (watchPage is null)
            {
                if (retriesRemaining > 0) continue;
                throw new YoutubeExplodeException("Video watch page is broken. Please try again in a few minutes.");
            }

            if (!watchPage.IsAvailable)
                throw new VideoUnavailableException($"Video '{videoId}' is not available.");

            return watchPage;
        }
    }

    #endregion

    #region GetPlayerResponse

    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var clientName = YoutubeClientUtils.CurrentProfile.ToString().ToUpperInvariant();
        if (clientName == "ANDROIDVR") clientName = "ANDROID_VR";
        return await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);
    }

    public async ValueTask<PlayerResponse> GetPlayerResponseWithClientAsync(
        VideoId videoId,
        string clientName,
        CancellationToken cancellationToken,
        string? signatureTimestamp = null)
    {
        ThrowIfInCooldown();

        await _requestThrottle.WaitAsync(cancellationToken);
        try
        {
            var delay = GetThrottleDelay();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
        }
        finally { _requestThrottle.Release(); }

        Log.Info($"GetPlayerResponse START ({clientName}): {videoId}");

        var visitorData = await ResolveVisitorDataAsync(cancellationToken);

        if (signatureTimestamp == null && clientName is "WEB" or "WEB_REMIX" or "TVHTML5_SIMPLY_EMBEDDED_PLAYER")
        {
            signatureTimestamp = await ResolveSignatureTimestampAsync(cancellationToken);
        }

        var playerUrl = clientName == "WEB_REMIX"
            ? "https://music.youtube.com/youtubei/v1/player"
            : "https://www.youtube.com/youtubei/v1/player";

        using var request = new HttpRequestMessage(HttpMethod.Post, playerUrl);
        request.Headers.Add("User-Agent", YoutubeClientUtils.GetUserAgentForClient(clientName));

        bool isMobileClient = clientName is "ANDROID_VR" or "ANDROID_MUSIC" or "IOS" or
                      "TVHTML5_SIMPLY_EMBEDDED_PLAYER" or "ANDROID_TESTSUITE";

        if (isMobileClient)
        {
            request.Options.Set(YoutubeHttpHandler.IsMobileClient, true);
            request.Options.Set(YoutubeHttpHandler.IsPlayerContext, true);
        }
        else
        {
            request.Options.Set(YoutubeHttpHandler.IsMobileClient, false);
        }

        string jsonBody = YoutubeClientUtils.GeneratePlayerContextForClient(
            clientName, videoId.Value, visitorData, signatureTimestamp);

        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            Log.Warn($"[VideoController] [{videoId}] {clientName} HTTP {statusCode}");

            if (statusCode == 403)
            {
                throw new StreamUnavailableException(
                    $"HTTP 403 Forbidden for video {videoId} via {clientName}",
                    videoId.Value,
                    StreamUnavailableReason.Forbidden403,
                    httpStatusCode: 403);
            }

            throw new YoutubeExplodeException($"HTTP {response.StatusCode} for client {clientName}");
        }

        var playerResponse = PlayerResponse.Parse(content);

        if (playerResponse.IsLoginRequired)
        {
            Log.Warn($"[VideoController] [{videoId}] LOGIN_REQUIRED via {clientName}: {playerResponse.LoginRequiredReason}");

            throw new LoginRequiredException(
                $"Video {videoId} requires login: {playerResponse.LoginRequiredReason}",
                videoId.Value,
                playerResponse.LoginRequiredReason);
        }

        TrackBotDetection(playerResponse, videoId.Value);

        return playerResponse;
    }

    private async ValueTask<string?> ResolveSignatureTimestampAsync(CancellationToken ct)
    {
        if (_signatureTimestamp != null) return _signatureTimestamp;

        try
        {
            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
            _signatureTimestamp = YoutubeAstSolver.ExtractSts(context.BaseJs);
            return _signatureTimestamp;
        }
        catch (Exception ex)
        {
            Log.Warn($"[VideoController] Failed to get signatureTimestamp: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Bot Detection Tracking

    private static void TrackBotDetection(PlayerResponse response, string videoId)
    {
        if (IsBotDetectionResponse(response))
        {
            lock (_stateLock)
            {
                _consecutiveFailures++;
                _lastBotDetection = DateTime.UtcNow;

                if (_consecutiveFailures == 1)
                {
                    Log.Warn("[VideoController] ⚠️ Bot detection triggered — slowing down requests");
                }
                else if (_consecutiveFailures >= 3)
                {
                    Log.Error($"[VideoController] ❌ Multiple bot detections ({_consecutiveFailures}) — cooldown active");
                }
            }
        }
        else if (response.IsPlayable)
        {
            lock (_stateLock)
            {
                if (_consecutiveFailures > 0)
                {
                    Log.Info($"[VideoController] ✓ Bot detection cleared after {_consecutiveFailures} failures");
                    _consecutiveFailures = 0;
                }
            }
        }
    }

    private static bool IsBotDetectionResponse(PlayerResponse response)
    {
        var error = response.PlayabilityError;
        if (string.IsNullOrEmpty(error)) return false;

        return error.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Выполните вход", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Войдите", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("confirm you're not", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("подтвердить", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Fallback & DASH Methods

    public async ValueTask<(PlayerResponse Response, string ClientName)> GetPlayerResponseWithFallbackAsync(
        VideoId videoId,
        CancellationToken cancellationToken,
        bool isAuthenticated = false)
    {
        var clients = YoutubeClientUtils.GetStreamFallbackClients(isAuthenticated);
        var errors = new List<string>();
        var allBotDetection = true;
        bool hasLoginRequired = false;
        LoginRequiredException? loginException = null;

        foreach (var clientName in clients)
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

                if (!IsBotDetectionResponse(response))
                    allBotDetection = false;
            }
            catch (LoginRequiredException ex)
            {
                if (!hasLoginRequired)
                {
                    hasLoginRequired = true;
                    loginException = ex;
                }
                errors.Add($"{clientName}: LOGIN_REQUIRED");
                allBotDetection = false;
            }
            catch (StreamUnavailableException) { throw; }
            catch (BotDetectionException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"[VideoController] [{videoId}] {clientName} exception: {ex.Message}");
                errors.Add($"{clientName}: {ex.Message}");
                allBotDetection = false;
            }
        }

        if (hasLoginRequired && loginException != null)
        {
            Log.Error($"[VideoController] [{videoId}] All clients require login: {loginException.Reason}");
            throw loginException;
        }

        var allErrors = string.Join("; ", errors);

        if (allBotDetection && IsInCooldown)
        {
            throw new BotDetectionException(
                $"All clients blocked by bot detection for {videoId}",
                GetRemainingCooldown());
        }

        throw new VideoUnplayableException(
            $"Video {videoId} is not available through any client. Errors: {allErrors}");
    }

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
            catch (StreamUnavailableException ex) when (ex.HttpStatusCode == 403)
            {
                Log.Warn($"[VideoController] [{videoId}] HLS via {clientName} got 403");

                throw new StreamUnavailableException(
                    $"HLS stream returned 403 for video {videoId}",
                    videoId.Value,
                    StreamUnavailableReason.Forbidden403,
                    httpStatusCode: 403,
                    wasHlsFallback: true);
            }
            catch (BotDetectionException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"[VideoController] [{videoId}] HLS via {clientName} failed: {ex.Message}");
            }
        }

        Log.Warn($"[VideoController] [{videoId}] No HLS manifest available from any client");
        return null;
    }

    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        string? signatureTimestamp,
        CancellationToken cancellationToken = default)
    {
        return await GetPlayerResponseWithClientAsync(
            videoId, "TVHTML5_SIMPLY_EMBEDDED_PLAYER", cancellationToken, signatureTimestamp);
    }

    public async ValueTask<DashManifest> GetDashManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
        => DashManifest.Parse(await Http.GetStringAsync(url, cancellationToken));

    #endregion
}