using Avalonia.Threading;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Services;

/// <summary>
/// Оркестратор обработки ошибок воспроизведения.
/// Единый центр принятия решений.
/// Полностью неблокирующий — использует toast-уведомления вместо модальных диалогов.
/// </summary>
/// <remarks>
/// <para><b>Recovery-first:</b></para>
/// <para>Для фатальных ошибок playback-flow сначала переводится в корректное состояние
/// (stop/skip), и только затем выполняется пользовательское уведомление.
/// Это устраняет гонку между поздними async-продолжениями и UI-обработкой ошибки.</para>
/// </remarks>
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

    /// <summary>
    /// Длительность toast для стратегии Dialog.
    /// </summary>
    private const int DialogToastDurationMs = 8000;

    /// <summary>
    /// Длительность toast для стратегии ToastAndSkip.
    /// </summary>
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

        Log.Info("[PlaybackErrorOrchestrator] Subscribed to AudioEngine.OnErrorOccurred (handler count verification)");
        Log.Info("[PlaybackErrorOrchestrator] Initialized and ready");
    }

    #region Error Handling

    /// <summary>
    /// Информационное уведомление о потере устройства.
    /// Показывает toast и OS-уведомление без эскалации как ошибки.
    /// Плеер уже на паузе — пользователю достаточно нажать Play после reconnect.
    /// </summary>
    private void HandleDeviceLost()
    {
        if (_disposed) return;

        string errorKey = "device_lost";
        if (!TryRegisterError(errorKey)) return;

        Log.Info("[Orchestrator] Device lost — showing informational toast");

        _ = ShowDeviceLostToastAsync();
    }

    /// <summary>
    /// Показывает info-toast о потере устройства и OS-уведомление.
    /// </summary>
    private async Task ShowDeviceLostToastAsync()
    {
        try
        {
            var L = LocalizationService.Instance;
            string title = L.Get("Notification_DeviceLost_Title",
                "Audio device disconnected");
            string message = L.Get("Notification_DeviceLost_Message",
                "Playback paused. Connect an audio device and press Play to resume.");

            await _notificationService.ShowToastAsync(
                titleKey: "Notification_DeviceLost_Title",
                messageKey: "Notification_DeviceLost_Message",
                severity: NotificationSeverity.Warning,
                durationMs: SkipToastDurationMs);

            await NotificationService.ShowOsNotificationAsync(
                title, message, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Orchestrator] Failed to show device lost toast: {ex.Message}");
        }
    }

    /// <summary>
    /// Информационное уведомление о восстановлении устройства.
    /// Показывает success-toast. Воспроизведение уже возобновлено автоматически.
    /// </summary>
    private void HandleDeviceRestored()
    {
        if (_disposed) return;

        Log.Info("[Orchestrator] Device restored — showing success toast");

        _ = ShowDeviceRestoredToastAsync();
    }

    /// <summary>
    /// Показывает success-toast о восстановлении устройства.
    /// </summary>
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

        if (IsCancellationLike(exception))
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

            if (actualException is OperationCanceledException)
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
                OperationCanceledException => Task.CompletedTask,
                _ => HandleGenericErrorAsync(actualException)
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[Orchestrator] Error in handler: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Определяет, содержит ли исключение признак отмены операции.
    /// </summary>
    public static bool IsCancellationLike(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TaskCanceledException)
                return true;
        }
        return false;
    }

    #endregion

    #region Specific Error Handlers

    /// <summary>
    /// Подавляет ошибки от устаревших операций, которые уже не относятся к текущему треку.
    /// </summary>
    private bool IsStalePlaybackError(Exception exception)
    {
        var current = _audioEngine.CurrentTrack;
        if (current == null)
            return true;

        var currentRaw = current.GetRawIdSpan();

        return exception switch
        {
            StreamUnavailableException stream =>
                !currentRaw.SequenceEqual(stream.VideoId.AsSpan()),

            LoginRequiredException login =>
                !currentRaw.SequenceEqual(login.VideoId.AsSpan()),

            ChunkDownloadFatalException chunk =>
                !string.Equals(current.Id, chunk.TrackId, StringComparison.Ordinal),

            _ => false
        };
    }

    /// <summary>
    /// Bot Detection — показываем диалог с таймером.
    /// </summary>
    private async Task HandleBotDetectionAsync(BotDetectionException exception)
    {
        Log.Warn($"[Orchestrator] Bot detection: {exception.FormatRemainingTime()}");

        await InvokeOnUIAsync(_audioEngine.Stop);

        await _dialogService.ShowBotDetectionCooldownAsync(exception.RemainingCooldown);
    }

    /// <summary>
    /// Login Required — toast с инструкцией + паузой.
    /// </summary>
    private async Task HandleLoginRequiredAsync(LoginRequiredException exception)
    {
        Log.Warn($"[Orchestrator] Login required: {exception.Reason} for {exception.VideoId}");

        var settings = _libraryService.Settings.Audio;

        await InvokeOnUIAsync(() => _audioEngine.SetPlaybackStateAsync(false));

        if (settings.PlayErrorSound)
            _notificationService.PlayErrorSound();

        var message = GetLoginRequiredMessage(exception);
        var recommendation = GetRecommendation(exception);
        var (Id, Title) = GetCurrentTrackInfo();

        await _notificationService.ShowPlaybackErrorAsync(
            LocalizationService.Instance["Error_Playback_Title"],
            message,
            Id,
            Title,
            null,
            exception.ToString(),
            NotificationSeverity.Error,
            durationMs: DialogToastDurationMs,
            recommendationKey: recommendation);

        await NotificationService.ShowOsNotificationAsync(
            LocalizationService.Instance["Error_Playback_Title"],
            message,
            NotificationSeverity.Error);
    }

    /// <summary>
    /// Stream Unavailable — поведение зависит от настроек.
    /// </summary>
    private async Task HandleStreamUnavailableAsync(StreamUnavailableException exception)
    {
        Log.Error($"[Orchestrator] Stream unavailable: {exception.Reason} for {exception.VideoId}");

        var behavior = GetErrorBehavior();
        var settings = _libraryService.Settings.Audio;
        var trackInfo = GetCurrentTrackInfo();
        var attempts = ExtractAttemptsFromException(exception);

        var messageKey = GetStreamErrorMessageKey(exception);
        var recommendationKey = GetRecommendationKey(exception);

        switch (behavior)
        {
            case PlaybackErrorBehavior.Dialog:
                await HandleWithPauseAndToastAsync(
                    "Error_StreamUnavailable_Title",
                    messageKey, trackInfo.Id, trackInfo.Title, attempts,
                    exception.ToString(), settings.PlayErrorSound, recommendationKey);
                break;

            case PlaybackErrorBehavior.ToastAndSkip:
                await HandleWithToastAndSkipAsync(
                    "Error_StreamUnavailable_Title",
                    messageKey, trackInfo.Id, trackInfo.Title, attempts,
                    exception.ToString(), settings.PlayErrorSound, recommendationKey);
                break;

            case PlaybackErrorBehavior.Ignore:
                await HandleWithSkipOnlyAsync();
                break;
        }
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

    private string? GetRecommendationKey(Exception exception)
    {
        var isAuthenticated = _youtube.AuthService.IsAuthenticated;

        return exception switch
        {
            LoginRequiredException login => login.Reason switch
            {
                LoginRequiredReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
                LoginRequiredReason.MembersOnly => "Recommendation_MembersOnly",
                _ => "Recommendation_Login"
            },

            StreamUnavailableException stream => stream.Reason switch
            {
                StreamUnavailableReason.Forbidden403 => isAuthenticated ? "Recommendation_ChangeClient" : "Recommendation_Login_403",
                StreamUnavailableReason.AllClientsFailed => isAuthenticated ? "Recommendation_AllClientsFailed_Auth" : "Recommendation_Login_403",
                StreamUnavailableReason.RegionBlocked => "Recommendation_UseVPN",
                StreamUnavailableReason.AgeRestricted => "Recommendation_Login_AgeRestricted",
                StreamUnavailableReason.Private => "Recommendation_Private",
                StreamUnavailableReason.Removed => "Recommendation_Removed",
                StreamUnavailableReason.PaymentRequired => "Recommendation_Payment",
                StreamUnavailableReason.LiveStream => "Recommendation_LiveStream",
                _ => "Recommendation_ContactDev"
            },

            ChunkDownloadFatalException chunk => chunk.Reason switch
            {
                ChunkDownloadFailureReason.Forbidden403 => isAuthenticated ? "Recommendation_ChangeClient" : "Recommendation_Login_403",
                ChunkDownloadFailureReason.NetworkError => "Recommendation_CheckNetwork",
                ChunkDownloadFailureReason.UmpFormat => "Recommendation_ChangeClient",
                _ => "Recommendation_ContactDev"
            },

            _ => null
        };
    }

    private async Task HandleChunkFatalAsync(ChunkDownloadFatalException exception)
    {
        Log.Error($"[Orchestrator] Chunk fatal: {exception.Reason} at chunk {exception.ChunkIndex}");

        var behavior = GetErrorBehavior();
        var settings = _libraryService.Settings.Audio;
        var (Id, Title) = GetCurrentTrackInfo();
        var message = GetChunkErrorMessage(exception);
        var recommendation = GetRecommendation(exception);

        var attempts = new List<AttemptRecord>
        {
            new(
                $"Chunk {exception.ChunkIndex}",
                false,
                $"{exception.Reason}: {exception.Message}",
                DateTime.UtcNow)
        };

        switch (behavior)
        {
            case PlaybackErrorBehavior.Dialog:
                await HandleWithPauseAndToastAsync(
                    LocalizationService.Instance["Error_Playback_Title"],
                    message, Id, Title, attempts,
                    exception.ToString(), settings.PlayErrorSound, recommendation);
                break;

            case PlaybackErrorBehavior.ToastAndSkip:
                await HandleWithToastAndSkipAsync(
                    LocalizationService.Instance["Error_Playback_Title"],
                    message, Id, Title, attempts,
                    exception.ToString(), settings.PlayErrorSound, recommendation);
                break;

            case PlaybackErrorBehavior.Ignore:
                await HandleWithSkipOnlyAsync();
                break;
        }
    }

    /// <summary>
    /// Generic Error — для неизвестных типов ошибок.
    /// </summary>
    private async Task HandleGenericErrorAsync(Exception exception)
    {
        Log.Error($"[Orchestrator] Generic error: {exception.Message}");

        var behavior = GetErrorBehavior();
        var settings = _libraryService.Settings.Audio;
        var (Id, Title) = GetCurrentTrackInfo();

        await RecoverFromFatalPlaybackErrorAsync();

        if (behavior != PlaybackErrorBehavior.Ignore)
        {
            if (settings.PlayErrorSound)
                _notificationService.PlayErrorSound();

            await _notificationService.ShowPlaybackErrorAsync(
                LocalizationService.Instance["Error_Playback_Title"],
                exception.Message,
                Id,
                Title,
                null,
                exception.ToString(),
                NotificationSeverity.Error);

            await NotificationService.ShowOsNotificationAsync(
                LocalizationService.Instance["Error_Playback_Title"],
                exception.Message,
                NotificationSeverity.Error);
        }
    }

    #endregion

    #region Behavior Strategies

    /// <summary>
    /// Выполняет recovery после фатальной ошибки воспроизведения.
    /// Для очереди длиной 1 используется terminal-stop path.
    /// </summary>
    private Task RecoverFromFatalPlaybackErrorAsync()
    {
        if (_audioEngine.Queue.Count <= 1)
            return InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError);

        return InvokeOnUIAsync(() => _audioEngine.PlayNextAsync());
    }

    /// <summary>
    /// Для стратегии Dialog: при очереди длиной 1 выполняется terminal-stop,
    /// иначе воспроизведение ставится на паузу.
    /// </summary>
    private Task PauseOrStopForDialogAsync()
    {
        if (_audioEngine.Queue.Count <= 1)
            return InvokeOnUIAsync(_audioEngine.StopAfterFatalPlaybackError);

        return InvokeOnUIAsync(() => _audioEngine.SetPlaybackStateAsync(false));
    }

    /// <summary>
    /// Стратегия Dialog: сначала recovery, затем длинный toast.
    /// </summary>
    private async Task HandleWithPauseAndToastAsync(
        string titleKey,
        string messageKey,
        string? trackId,
        string? trackTitle,
        List<AttemptRecord>? attempts,
        string? exceptionDetails,
        bool playSound,
        string? recommendationKey = null)
    {
        await PauseOrStopForDialogAsync();

        if (playSound)
            _notificationService.PlayErrorSound();

        await _notificationService.ShowPlaybackErrorAsync(
            titleKey, messageKey, trackId, trackTitle, attempts, exceptionDetails,
            NotificationSeverity.Error,
            durationMs: DialogToastDurationMs,
            recommendationKey: recommendationKey);

        var L = LocalizationService.Instance;
        await NotificationService.ShowOsNotificationAsync(L[titleKey], L[messageKey], NotificationSeverity.Error);
    }

    /// <summary>
    /// Стратегия ToastAndSkip: сначала recovery, затем toast и OS notification.
    /// </summary>
    private async Task HandleWithToastAndSkipAsync(
        string titleKey,
        string messageKey,
        string? trackId,
        string? trackTitle,
        List<AttemptRecord>? attempts,
        string? exceptionDetails,
        bool playSound,
        string? recommendationKey = null)
    {
        await RecoverFromFatalPlaybackErrorAsync();

        if (playSound)
            _notificationService.PlayErrorSound();

        await _notificationService.ShowPlaybackErrorAsync(
            titleKey, messageKey, trackId, trackTitle, attempts, exceptionDetails,
            NotificationSeverity.Warning,
            durationMs: SkipToastDurationMs,
            recommendationKey: recommendationKey);

        var L = LocalizationService.Instance;
        await NotificationService.ShowOsNotificationAsync(L[titleKey], L[messageKey], NotificationSeverity.Warning);
    }

    /// <summary>
    /// Стратегия Ignore: только recovery, без уведомлений.
    /// </summary>
    private async Task HandleWithSkipOnlyAsync()
    {
        Log.Debug("[Orchestrator] Ignoring error, applying fatal recovery");
        await RecoverFromFatalPlaybackErrorAsync();
    }

    #endregion

    #region Helpers

    private PlaybackErrorBehavior GetErrorBehavior()
    {
        return _libraryService.Settings.Audio.CriticalErrorBehavior;
    }

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

    private static string GetChunkErrorMessage(ChunkDownloadFatalException exception)
    {
        var L = LocalizationService.Instance;

        return exception.Reason switch
        {
            ChunkDownloadFailureReason.Forbidden403 => L["Error_Stream_Forbidden"],
            ChunkDownloadFailureReason.UmpFormat => L["Error_Stream_UmpFormat"],
            ChunkDownloadFailureReason.MaxRetriesExceeded => L["Error_Stream_MaxRetries"],
            ChunkDownloadFailureReason.NetworkError => L["Error_Stream_Network"],
            _ => L["Error_Stream_Unknown"]
        };
    }

    private static string GetLoginRequiredMessage(LoginRequiredException exception)
    {
        var L = LocalizationService.Instance;

        return exception.Reason switch
        {
            LoginRequiredReason.AgeRestricted => L["Error_Login_AgeRestricted"],
            LoginRequiredReason.Private => L["Error_Login_Private"],
            LoginRequiredReason.MembersOnly => L["Error_Login_MembersOnly"],
            _ => L["Error_Login_Required"]
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
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task InvokeOnUIAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            await action();
        else
            await Dispatcher.UIThread.InvokeAsync(action);
    }

    #endregion

    #region Recommendations

    /// <summary>
    /// Формирует рекомендацию по исправлению ошибки.
    /// Учитывает статус авторизации пользователя.
    /// </summary>
    private string? GetRecommendation(Exception exception)
    {
        var L = LocalizationService.Instance;
        var isAuthenticated = _youtube.AuthService.IsAuthenticated;

        return exception switch
        {
            LoginRequiredException login => login.Reason switch
            {
                LoginRequiredReason.AgeRestricted => L["Recommendation_Login_AgeRestricted"],
                LoginRequiredReason.MembersOnly => L["Recommendation_MembersOnly"],
                _ => L["Recommendation_Login"]
            },

            StreamUnavailableException stream => GetStreamRecommendation(stream, isAuthenticated, L),

            ChunkDownloadFatalException chunk => GetChunkRecommendation(chunk, isAuthenticated, L),

            _ => null
        };
    }

    private static string GetStreamRecommendation(
        StreamUnavailableException stream,
        bool isAuthenticated,
        LocalizationService L)
    {
        if (stream.Reason == StreamUnavailableReason.Forbidden403)
        {
            return isAuthenticated
                ? L["Recommendation_ChangeClient"]
                : L["Recommendation_Login_403"];
        }

        if (stream.Reason == StreamUnavailableReason.AllClientsFailed)
        {
            return isAuthenticated
                ? L["Recommendation_AllClientsFailed_Auth"]
                : L["Recommendation_Login_403"];
        }

        return stream.Reason switch
        {
            StreamUnavailableReason.RegionBlocked => L["Recommendation_UseVPN"],
            StreamUnavailableReason.AgeRestricted => L["Recommendation_Login_AgeRestricted"],
            StreamUnavailableReason.Private => L["Recommendation_Private"],
            StreamUnavailableReason.Removed => L["Recommendation_Removed"],
            StreamUnavailableReason.PaymentRequired => L["Recommendation_Payment"],
            StreamUnavailableReason.LiveStream => L["Recommendation_LiveStream"],
            _ => L["Recommendation_ContactDev"]
        };
    }

    private static string GetChunkRecommendation(
        ChunkDownloadFatalException chunk,
        bool isAuthenticated,
        LocalizationService L)
    {
        if (chunk.Reason == ChunkDownloadFailureReason.Forbidden403)
        {
            return isAuthenticated
                ? L["Recommendation_ChangeClient"]
                : L["Recommendation_Login_403"];
        }

        return chunk.Reason switch
        {
            ChunkDownloadFailureReason.NetworkError => L["Recommendation_CheckNetwork"],
            ChunkDownloadFailureReason.UmpFormat => L["Recommendation_ChangeClient"],
            _ => L["Recommendation_ContactDev"]
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

        lock (_recentlyShownErrors)
        {
            _recentlyShownErrors.Clear();
        }

        Log.Info("[PlaybackErrorOrchestrator] Disposed");
    }

    #endregion
}