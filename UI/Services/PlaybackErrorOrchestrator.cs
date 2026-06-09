using Avalonia.Threading;
using LMP.Core.Exceptions;
using LMP.Core.Services;
using LMP.Core.Youtube.Exceptions;

namespace LMP.UI.Services;

/// <summary>
/// Оркестратор обработки ошибок воспроизведения.
/// Единый центр принятия решений.
/// Полностью неблокирующий — использует toast-уведомления вместо модальных диалогов.
/// </summary>
public sealed class PlaybackErrorOrchestrator : IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audioEngine;
    private readonly DialogService _dialogService;
    private readonly NotificationService _notificationService;
    private readonly LibraryService _libraryService;

    private readonly HashSet<string> _recentlyShownErrors = new(StringComparer.Ordinal);
    private DateTime _lastErrorCleanup = DateTime.MinValue;

    private static readonly TimeSpan ErrorCacheCleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorDeduplicationWindow = TimeSpan.FromSeconds(10);

    private const int DialogToastDurationMs = 8000;
    private const int SkipToastDurationMs = 5000;

    private volatile bool _disposed;

    public PlaybackErrorOrchestrator(
         YoutubeProvider youtube,
         AudioEngine audioEngine,
         DialogService dialogService,
         NotificationService notificationService,
         LibraryService libraryService)
    {
        _youtube = youtube ?? throw new ArgumentNullException(nameof(youtube));
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));

        _audioEngine.OnErrorOccurred += HandleError;
        _audioEngine.OnDeviceLost += HandleDeviceLost;
        _audioEngine.OnDeviceRestored += HandleDeviceRestored;

        // Subscribe to low-level early-warning network events
        Core.Audio.Sources.CachingStreamSource.OnSourceWarning += HandleSourceWarning;

        Log.Info("[PlaybackErrorOrchestrator] Initialized and ready");
    }

    #region Error Handling

    private void HandleSourceWarning(string trackId, Exception exception)
    {
        if (_disposed) return;

        // Deduplicate early-warnings to avoid UI clutter
        string warningKey = $"warning_{trackId}_{exception.GetType().Name}";
        if (!TryRegisterError(warningKey)) return;

        Log.Warn($"[Orchestrator] Non-fatal stream warning for track {trackId}: {exception.Message}");

        _ = ShowStreamWarningToastAsync();
    }

    private async Task ShowStreamWarningToastAsync()
    {
        try
        {
            // Apologize for the delay without premature assumptions about DPI blocking
            await _notificationService.ShowToastAsync(
                titleKey: "Notification_PlaybackDelay_Title",
                messageKey: "Notification_PlaybackDelay_Message",
                severity: NotificationSeverity.Warning,
                durationMs: SkipToastDurationMs);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show warning toast: {ex.Message}");
        }
    }

    private void HandleDeviceLost()
    {
        if (_disposed || !TryRegisterError("device_lost")) return;
        Log.Info("[Orchestrator] Device lost — showing informational toast");
        _ = ShowDeviceLostToastAsync();
    }

    private async Task ShowDeviceLostToastAsync()
    {
        try
        {
            var L = LocalizationService.Instance;
            string title = L.Get("Notification_DeviceLost_Title", "Audio device disconnected");
            string message = L.Get("Notification_DeviceLost_Message", "Playback paused. Connect an audio device and press Play to resume.");

            await _notificationService.ShowToastAsync(
                titleKey: "Notification_DeviceLost_Title",
                messageKey: "Notification_DeviceLost_Message",
                severity: NotificationSeverity.Warning,
                durationMs: SkipToastDurationMs);

            await NotificationService.ShowOsNotificationAsync(title, message, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show device lost toast: {ex.Message}");
        }
    }

    private void HandleDeviceRestored()
    {
        if (_disposed) return;
        Log.Info("[Orchestrator] Device restored — showing success toast");
        _ = ShowDeviceRestoredToastAsync();
    }

    private async Task ShowDeviceRestoredToastAsync()
    {
        try
        {
            await _notificationService.ShowToastAsync(
                titleKey: "Notification_DeviceRestored_Title",
                messageKey: "Notification_DeviceRestored_Message",
                severity: NotificationSeverity.Success,
                durationMs: SkipToastDurationMs);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show device restored toast: {ex.Message}");
        }
    }

    private void HandleError(Exception exception)
    {
        if (_disposed) return;
        Log.Info($"[Orchestrator] ◆ Received error event: {exception.GetType().Name}: {exception.Message}");
        _ = HandleErrorAsync(exception);
    }

    public async Task HandleErrorAsync(Exception exception)
    {
        if (_disposed) return;

        if (NetworkErrorHelper.IsCancellationLike(exception))
        {
            Log.Debug($"[Orchestrator] Suppressing cancelled error: {exception.GetType().Name}");
            return;
        }

        if (IsStalePlaybackError(exception))
        {
            Log.Debug($"[Orchestrator] Suppressing stale error: {exception.GetType().Name}");
            return;
        }

        string errorKey = GetErrorDeduplicationKey(exception);
        if (!TryRegisterError(errorKey))
        {
            Log.Debug($"[Orchestrator] Skipping duplicate error: {errorKey}");
            return;
        }

        Log.Info($"[Orchestrator] Handling: {exception.GetType().Name}");

        try
        {
            var actualException = UnwrapException(exception);

            if (actualException is OperationCanceledException oce && NetworkErrorHelper.IsCancellationLike(oce))
            {
                Log.Debug("[Orchestrator] Suppressing unwrapped cancellation");
                return;
            }

            await (actualException switch
            {
                BotDetectionException botEx => HandleBotDetectionAsync(botEx),
                LoginRequiredException loginEx => HandleLoginRequiredAsync(loginEx),
                StreamUnavailableException streamEx => HandleStreamUnavailableAsync(streamEx),
                ChunkDownloadFatalException chunkEx => HandleChunkFatalAsync(chunkEx),
                OperationCanceledException oce2 when NetworkErrorHelper.IsCancellationLike(oce2) => Task.CompletedTask,
                _ => HandleGenericErrorAsync(actualException)
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[Orchestrator] Error in handler: {ex.Message}", ex);
        }
    }

    #endregion

    #region Specific Error Handlers

    private bool IsStalePlaybackError(Exception exception)
    {
        var current = _audioEngine.CurrentTrack;
        if (current == null) return true;
        var currentRaw = current.GetRawIdSpan();

        return exception switch
        {
            StreamUnavailableException stream => !currentRaw.SequenceEqual(stream.VideoId.AsSpan()),
            LoginRequiredException login => !currentRaw.SequenceEqual(login.VideoId.AsSpan()),
            ChunkDownloadFatalException chunk => !string.Equals(current.Id, chunk.TrackId, StringComparison.Ordinal),
            _ => false
        };
    }

    private async Task HandleBotDetectionAsync(BotDetectionException exception)
    {
        Log.Warn($"[Orchestrator] Bot detection: {exception.FormatRemainingTime()}");
        await InvokeOnUIAsync(_audioEngine.Stop);
        await _dialogService.ShowBotDetectionCooldownAsync(exception.RemainingCooldown);
    }

    private async Task HandleLoginRequiredAsync(LoginRequiredException exception)
    {
        Log.Warn($"[Orchestrator] Login required: {exception.Reason} for {exception.VideoId}");
        var settings = _libraryService.Settings.Audio;

        await InvokeOnUIAsync(() => _audioEngine.SetPlaybackStateAsync(false));

        if (settings.PlayErrorSound)
            _notificationService.PlayErrorSound();

        var messageKey = GetLoginRequiredMessageKey(exception);
        var recommendationKey = GetRecommendation(exception);
        var (Id, Title) = GetCurrentTrackInfo();

        await _notificationService.ShowPlaybackErrorAsync(
            "Error_Playback_Title", messageKey, Id, Title, null, exception.ToString(),
            NotificationSeverity.Error, DialogToastDurationMs, recommendationKey);

        await NotificationService.ShowOsNotificationAsync(
            LocalizationService.Instance["Error_Playback_Title"],
            LocalizationService.Instance[messageKey],
            NotificationSeverity.Error);
    }

    private async Task HandleStreamUnavailableAsync(StreamUnavailableException exception)
    {
        Log.Error($"[Orchestrator] Stream unavailable: {exception.Reason} for {exception.VideoId}");
        var attempts = ExtractAttemptsFromException(exception);
        var messageKey = GetStreamErrorMessageKey(exception);
        await DispatchPlaybackErrorAsync(exception, messageKey, attempts);
    }

    private async Task HandleChunkFatalAsync(ChunkDownloadFatalException exception)
    {
        Log.Error($"[Orchestrator] Chunk fatal: {exception.Reason} at chunk {exception.ChunkIndex}");
        var attempts = new List<AttemptRecord> {
            new($"Chunk {exception.ChunkIndex}", false, $"{exception.Reason}: {exception.Message}", DateTime.UtcNow)
        };
        var messageKey = GetChunkErrorMessageKey(exception);
        await DispatchPlaybackErrorAsync(exception, messageKey, attempts);
    }

    private async Task HandleGenericErrorAsync(Exception exception)
    {
        Log.Error($"[Orchestrator] Generic error: {exception.Message}");
        await DispatchPlaybackErrorAsync(exception, exception.Message, null);
    }

    #endregion

    #region Behavior Strategies (Deduplicated)

    /// <summary>
    /// Универсальный диспетчер, обрабатывающий поведение на основе настроек `PlaybackErrorBehavior`.
    /// Полностью устраняет дублирование между типами ошибок.
    /// </summary>
    private async Task DispatchPlaybackErrorAsync(
        Exception exception,
        string messageOrKey,
        List<AttemptRecord>? attempts = null)
    {
        var behavior = _libraryService.Settings.Audio.CriticalErrorBehavior;

        if (behavior == PlaybackErrorBehavior.Ignore)
        {
            Log.Debug("[Orchestrator] Ignoring error, applying fatal recovery");
            await RecoverFromFatalPlaybackErrorAsync();
            return;
        }

        var settings = _libraryService.Settings.Audio;
        var (trackId, trackTitle) = GetCurrentTrackInfo();

        bool isSslFailure = NetworkErrorHelper.IsSslOrTlsHandshakeFailure(exception);
        string finalMessageOrKey = isSslFailure ? "Error_SslHandshake_Failed" : messageOrKey;

        string? recommendationKey = isSslFailure
            ? "Recommendation_DpiBlocked"
            : GetRecommendation(exception);

        if (behavior == PlaybackErrorBehavior.Dialog)
            await PauseOrStopForDialogAsync();
        else
            await RecoverFromFatalPlaybackErrorAsync();

        if (settings.PlayErrorSound)
            _notificationService.PlayErrorSound();

        // Playback failure is ALWAYS shown as a critical Error (red toast) to the user.
        var severity = NotificationSeverity.Error;
        int duration = behavior == PlaybackErrorBehavior.Dialog ? DialogToastDurationMs : SkipToastDurationMs;

        // Показ внутреннего тоста
        await _notificationService.ShowPlaybackErrorAsync(
            "Error_Playback_Title", finalMessageOrKey, trackId, trackTitle, attempts, exception.ToString(),
            severity, durationMs: duration, recommendationKey: recommendationKey);

        // Показ системного уведомления (переводим на лету)
        string localizedTitle = LocalizationService.Instance["Error_Playback_Title"];
        string localizedMessage = finalMessageOrKey.StartsWith("Error_")
            ? LocalizationService.Instance[finalMessageOrKey]
            : finalMessageOrKey;

        await NotificationService.ShowOsNotificationAsync(localizedTitle, localizedMessage, severity);
    }

    private Task RecoverFromFatalPlaybackErrorAsync()
    {
        if (_audioEngine.Queue.Count <= 1)
            return InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError);
        return InvokeOnUIAsync(() => _audioEngine.PlayNextAsync());
    }

    private Task PauseOrStopForDialogAsync()
    {
        if (_audioEngine.Queue.Count <= 1)
            return InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError);
        return InvokeOnUIAsync(() => _audioEngine.SetPlaybackStateAsync(false));
    }

    #endregion

    #region Helpers

    private (string Id, string Title) GetCurrentTrackInfo()
    {
        var track = _audioEngine.CurrentTrack;
        return (track?.Id ?? "unknown", track?.Title ?? "Unknown Track");
    }

    private static List<AttemptRecord> ExtractAttemptsFromException(StreamUnavailableException exception)
    {
        return
        [
            new AttemptRecord(
                exception.WasHlsFallback ? "HLS Fallback" : "Stream Request",
                false,
                $"{exception.Reason}: {exception.Message}",
                DateTime.UtcNow)
        ];
    }

    private static string GetChunkErrorMessageKey(ChunkDownloadFatalException exception)
    {
        return exception.Reason switch
        {
            ChunkDownloadFailureReason.Forbidden403 => "Error_Stream_Forbidden",
            ChunkDownloadFailureReason.UmpFormat => "Error_Stream_UmpFormat",
            ChunkDownloadFailureReason.MaxRetriesExceeded => "Error_Stream_MaxRetries",
            ChunkDownloadFailureReason.NetworkError => "Error_Stream_Network",
            _ => "Error_Stream_Unknown"
        };
    }

    private static string GetStreamErrorMessageKey(StreamUnavailableException exception)
    {
        return exception.Reason switch
        {
            StreamUnavailableReason.Forbidden403 => "Error_Stream_Forbidden",
            StreamUnavailableReason.RegionBlocked => "Error_Stream_RegionBlocked",
            StreamUnavailableReason.AgeRestricted => "Error_Stream_AgeRestricted",
            StreamUnavailableReason.Private => "Error_Stream_Private",
            StreamUnavailableReason.AllClientsFailed => "Error_Stream_AllClientsFailed",
            StreamUnavailableReason.LiveStream => "Error_Stream_LiveStream",
            StreamUnavailableReason.Removed => "Error_Stream_Removed",
            StreamUnavailableReason.PaymentRequired => "Error_Stream_PaymentRequired",
            _ => "Error_Stream_Unknown"
        };
    }

    private static string GetLoginRequiredMessageKey(LoginRequiredException exception)
    {
        return exception.Reason switch
        {
            LoginRequiredReason.AgeRestricted => "Error_Login_AgeRestricted",
            LoginRequiredReason.Private => "Error_Login_Private",
            LoginRequiredReason.MembersOnly => "Error_Login_MembersOnly",
            _ => "Error_Login_Required"
        };
    }

    private static Exception UnwrapException(Exception exception)
    {
        if (exception is AggregateException agg && agg.InnerExceptions.Count > 0)
            return UnwrapException(agg.InnerExceptions[0]);

        var inner = exception.InnerException;
        while (inner != null)
        {
            if (inner is BotDetectionException or
                LoginRequiredException or
                StreamUnavailableException or
                ChunkDownloadFatalException)
            {
                return inner;
            }
            inner = inner.InnerException;
        }

        return exception;
    }

    private static string GetErrorDeduplicationKey(Exception exception)
    {
        if (NetworkErrorHelper.IsSslOrTlsHandshakeFailure(exception))
            return "ssl_handshake_failure";

        return exception switch
        {
            BotDetectionException => "bot_detection",
            LoginRequiredException login => $"login_{login.VideoId}",
            StreamUnavailableException stream => $"stream_{stream.VideoId}_{stream.Reason}",
            ChunkDownloadFatalException chunk => $"chunk_{chunk.TrackId}_{chunk.Reason}",
            _ => $"generic_{exception.GetType().Name}_{exception.Message.GetHashCode()}"
        };
    }

    private bool TryRegisterError(string errorKey)
    {
        if (DateTime.UtcNow - _lastErrorCleanup > ErrorCacheCleanupInterval)
        {
            lock (_recentlyShownErrors)
            {
                _recentlyShownErrors.Clear();
                _lastErrorCleanup = DateTime.UtcNow;
            }
        }

        lock (_recentlyShownErrors)
        {
            if (_recentlyShownErrors.Contains(errorKey))
                return false;

            _recentlyShownErrors.Add(errorKey);

            _ = Task.Delay(ErrorDeduplicationWindow).ContinueWith(_ =>
            {
                lock (_recentlyShownErrors)
                {
                    _recentlyShownErrors.Remove(errorKey);
                }
            });

            return true;
        }
    }

    private static async Task InvokeOnUIAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task InvokeOnUIAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) await action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }

    #endregion

    #region Recommendations

    private string? GetRecommendation(Exception exception)
    {
        var isAuthenticated = _youtube.AuthService.IsAuthenticated;

        if (NetworkErrorHelper.IsSslOrTlsHandshakeFailure(exception))
            return "Recommendation_DpiBlocked";

        return exception switch
        {
            LoginRequiredException login => login.Reason switch
            {
                LoginRequiredReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
                LoginRequiredReason.MembersOnly => "Recommendation_MembersOnly",
                _ => "Recommendation_Login"
            },
            StreamUnavailableException stream => GetStreamRecommendation(stream, isAuthenticated),
            ChunkDownloadFatalException chunk => GetChunkRecommendation(chunk, isAuthenticated),
            _ => null
        };
    }

    private static string GetStreamRecommendation(StreamUnavailableException stream, bool isAuthenticated)
    {
        if (stream.Reason == StreamUnavailableReason.Forbidden403)
            return isAuthenticated ? "Recommendation_ChangeClient" : "Recommendation_Login_403";

        if (stream.Reason == StreamUnavailableReason.AllClientsFailed)
            return isAuthenticated ? "Recommendation_AllClientsFailed_Auth" : "Recommendation_Login_403";

        return stream.Reason switch
        {
            StreamUnavailableReason.RegionBlocked => "Recommendation_UseVPN",
            StreamUnavailableReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
            StreamUnavailableReason.Private => "Recommendation_Private",
            StreamUnavailableReason.Removed => "Recommendation_Removed",
            StreamUnavailableReason.PaymentRequired => "Recommendation_Payment",
            StreamUnavailableReason.LiveStream => "Recommendation_LiveStream",
            _ => "Recommendation_ContactDev"
        };
    }

    private static string GetChunkRecommendation(ChunkDownloadFatalException chunk, bool isAuthenticated)
    {
        if (chunk.Reason == ChunkDownloadFailureReason.Forbidden403)
            return isAuthenticated ? "Recommendation_ChangeClient" : "Recommendation_Login_403";

        return chunk.Reason switch
        {
            ChunkDownloadFailureReason.NetworkError => "Recommendation_CheckNetwork",
            ChunkDownloadFailureReason.UmpFormat => "Recommendation_ChangeClient",
            _ => "Recommendation_ContactDev"
        };
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioEngine.OnErrorOccurred -= HandleError;
        _audioEngine.OnDeviceLost -= HandleDeviceLost;
        _audioEngine.OnDeviceRestored -= HandleDeviceRestored;

        // Unsubscribe safely to prevent GC leaks
        Core.Audio.Sources.CachingStreamSource.OnSourceWarning -= HandleSourceWarning;

        lock (_recentlyShownErrors) _recentlyShownErrors.Clear();

        Log.Info("[PlaybackErrorOrchestrator] Disposed");
    }

    #endregion
}