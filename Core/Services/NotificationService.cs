using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using LMP.Core.Data.Entities;
using LMP.Core.Data.Repositories;
using LMP.Core.Helpers;
using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

/// <summary>
/// Сервис уведомлений: история, toast-очередь, авто-очистка.
/// <para>
/// Счётчик непрочитанных — O(1) через <see cref="_unreadCount"/>,
/// авто-очистка — <see cref="PeriodicTimer"/> в фоновом потоке.
/// </para>
/// </summary>
public sealed class NotificationService : ReactiveObject, IDisposable
{
    private readonly LibraryService _libraryService;
    private readonly INotificationRepository _repository;

    private const int DefaultToastDuration = 4000;

    // O(1) счётчик, обновляется при каждой мутации коллекции
    private int _unreadCount;

    public ObservableCollection<Notification> Notifications { get; } = [];

    /// <inheritdoc cref="_unreadCount"/>
    public int UnreadCount => _unreadCount;
    public bool HasUnread => _unreadCount > 0;

    private Notification? _currentToast;
    public Notification? CurrentToast
    {
        get => _currentToast;
        private set => this.RaiseAndSetIfChanged(ref _currentToast, value);
    }

    public bool IsToastVisible => CurrentToast != null;

    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _cleanupCts;
    private bool _isInitialized;

    /// <summary>
    /// Максимум уведомлений в памяти. Берётся из <see cref="AppSettings"/>.
    /// </summary>
    private int MaxNotifications => _libraryService.Settings.Notifications.MaxInPanelCount;

    public NotificationService(LibraryService libraryService, INotificationRepository repository)
    {
        _libraryService = libraryService;
        _repository = repository;
        Log.Info("[NotificationService] Initialized");
    }

    #region Initialization

    /// <summary>
    /// Загружает историю из БД и запускает фоновую авто-очистку.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isInitialized) return;

        try
        {
            var entities = await _repository.GetRecentAsync(MaxNotifications, ct);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var entity in entities)
                {
                    var n = EntityToModel(entity);
                    Notifications.Add(n);
                    if (!n.IsRead) _unreadCount++;
                }

                RaiseUnreadProperties();
            });

            _isInitialized = true;
            Log.Info($"[NotificationService] Loaded {entities.Count} notifications from DB");

            StartAutoCleanup();
        }
        catch (Exception ex)
        {
            Log.Error($"[NotificationService] Failed to load history: {ex.Message}");
            _isInitialized = true;
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Показывает toast-уведомление и добавляет его в историю.
    /// </summary>
    public async Task ShowToastAsync(
        string titleKey,
        string messageKey,
        NotificationSeverity severity = NotificationSeverity.Info,
        int durationMs = 0,
        object[]? messageArgs = null,
        CancellationToken ct = default,
        string? trackId = null,
        string? trackTitle = null,
        string? exceptionDetails = null,
        string? recommendationKey = null)
    {
        if (durationMs <= 0) durationMs = DefaultToastDuration;

        var notification = new Notification
        {
            TitleKey = titleKey,
            MessageKey = messageKey,
            MessageArgs = messageArgs,
            Severity = severity,
            TrackId = trackId,
            TrackTitle = trackTitle,
            ExceptionDetails = exceptionDetails,
            RecommendationKey = recommendationKey
        };

        await AddToHistoryAsync(notification);
        await PersistAsync(notification);
        await ShowToastInternalAsync(notification, durationMs, ct);
    }

    /// <summary>
    /// Показывает детализированный toast об ошибке воспроизведения с попытками.
    /// </summary>
    public async Task ShowPlaybackErrorAsync(
        string titleKey,
        string messageKey,
        string? trackId,
        string? trackTitle,
        IEnumerable<AttemptRecord>? attempts,
        string? exceptionDetails,
        NotificationSeverity severity = NotificationSeverity.Error,
        int durationMs = 0,
        string? recommendationKey = null,
        object[]? messageArgs = null,
        CancellationToken ct = default)
    {
        if (durationMs <= 0) durationMs = DefaultToastDuration;

        var notification = new Notification
        {
            TitleKey = titleKey,
            MessageKey = messageKey,
            MessageArgs = messageArgs,
            Severity = severity,
            TrackId = trackId,
            TrackTitle = trackTitle,
            Attempts = attempts != null ? new ObservableCollection<AttemptRecord>(attempts) : null,
            ExceptionDetails = exceptionDetails,
            RecommendationKey = recommendationKey
        };

        await AddToHistoryAsync(notification);
        await PersistAsync(notification);
        await ShowToastInternalAsync(notification, durationMs, ct);
    }

    public static async Task ShowOsNotificationAsync(
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Info)
    {
        await OsNotificationHelper.ShowAsync(title, message, severity);
    }

    public void PlayErrorSound()
    {
        if (_libraryService.Settings.Audio.PlayErrorSound)
            ErrorSoundPlayer.PlayError();
    }

    public static void PlaySuccessSound() => ErrorSoundPlayer.PlaySuccess();

    #endregion

    #region Auto-Cleanup

    /// <summary>
    /// Запускает фоновый таймер авто-очистки уведомлений.
    /// Работает в <see cref="ThreadPool"/>, не блокирует UI.
    /// </summary>
    private void StartAutoCleanup()
    {
        var settings = _libraryService.Settings.Notifications;
        if (!settings.AutoCleanupEnabled) return;

        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(() => RunCleanupLoopAsync(settings, token), token);
    }

    private async Task RunCleanupLoopAsync(NotificationSettings settings, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(settings.CleanupCheckIntervalMinutes);
        using var timer = new PeriodicTimer(interval);

        try
        {
            // Первый прогон — сразу при старте
            await CleanupOldNotificationsAsync(settings, ct);

            while (await timer.WaitForNextTickAsync(ct))
                await CleanupOldNotificationsAsync(settings, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[NotificationService] Cleanup loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// Удаляет уведомления старше <see cref="NotificationSettings.AutoCleanupAfterHours"/> часов.
    /// UI-операции выполняются через <see cref="Dispatcher.UIThread"/>.
    /// </summary>
    private async Task CleanupOldNotificationsAsync(NotificationSettings settings, CancellationToken ct)
    {
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(settings.AutoCleanupAfterHours);

        // Собираем кандидатов вне UI-потока
        List<Notification>? toRemove = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            toRemove = [.. Notifications.Where(n => n.Timestamp < threshold)];
        });

        if (toRemove is not { Count: > 0 }) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var n in toRemove)
            {
                if (Notifications.Remove(n) && !n.IsRead)
                    _unreadCount = Math.Max(0, _unreadCount - 1);
            }

            RaiseUnreadProperties();
        });

        try
        {
            await _repository.DeleteOlderThanAsync(threshold, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"[NotificationService] Cleanup DB error: {ex.Message}");
        }

        Log.Info($"[NotificationService] Auto-cleanup removed {toRemove.Count} old notifications");
    }

    #endregion

    #region Persistence

    private async Task PersistAsync(Notification notification)
    {
        try
        {
            await _repository.AddAsync(ModelToEntity(notification));
            await _repository.PruneAsync(MaxNotifications * 2);
        }
        catch (Exception ex)
        {
            Log.Warn($"[NotificationService] Failed to persist: {ex.Message}");
        }
    }

    private static NotificationEntity ModelToEntity(Notification n)
    {
        string? attemptsJson = null;
        if (n.Attempts is { Count: > 0 })
        {
            var records = n.Attempts.Select(a => new AttemptDto(a.ClientName, a.Success, a.ErrorMessage, a.Timestamp));
            attemptsJson = JsonSerializer.Serialize(records);
        }

        string? argsJson = null;
        if (n.MessageArgs is { Length: > 0 })
            argsJson = JsonSerializer.Serialize(n.MessageArgs.Select(a => a?.ToString()).ToArray());

        return new NotificationEntity
        {
            Id = n.Id.ToString(),
            TitleKey = n.TitleKey,
            TitleRaw = n.TitleRaw,
            MessageKey = n.MessageKey,
            MessageRaw = n.MessageRaw,
            MessageArgsJson = argsJson,
            RecommendationKey = n.RecommendationKey,
            Severity = (int)n.Severity,
            IsRead = n.IsRead,
            TrackId = n.TrackId,
            TrackTitle = n.TrackTitle,
            ExceptionDetails = n.ExceptionDetails,
            AttemptsJson = attemptsJson,
            CreatedAt = n.Timestamp
        };
    }

    private static Notification EntityToModel(NotificationEntity e)
    {
        ObservableCollection<AttemptRecord>? attempts = null;
        if (!string.IsNullOrEmpty(e.AttemptsJson))
        {
            try
            {
                var dtos = JsonSerializer.Deserialize<List<AttemptDto>>(e.AttemptsJson);
                if (dtos is { Count: > 0 })
                    attempts = new ObservableCollection<AttemptRecord>(
                        dtos.Select(d => new AttemptRecord(d.ClientName, d.Success, d.ErrorMessage, d.Timestamp)));
            }
            catch { /* ignore corrupt data */ }
        }

        object[]? args = null;
        if (!string.IsNullOrEmpty(e.MessageArgsJson))
        {
            try
            {
                args = JsonSerializer.Deserialize<string[]>(e.MessageArgsJson)?
                    .Cast<object>().ToArray();
            }
            catch { /* ignore */ }
        }

        return new Notification
        {
            Id = Guid.TryParse(e.Id, out var guid) ? guid : Guid.NewGuid(),
            Timestamp = e.CreatedAt,
            TitleKey = e.TitleKey,
            TitleRaw = e.TitleRaw,
            MessageKey = e.MessageKey,
            MessageRaw = e.MessageRaw,
            MessageArgs = args,
            RecommendationKey = e.RecommendationKey,
            Severity = (NotificationSeverity)e.Severity,
            IsRead = e.IsRead,
            TrackId = e.TrackId,
            TrackTitle = e.TrackTitle,
            ExceptionDetails = e.ExceptionDetails,
            Attempts = attempts
        };
    }

    private sealed record AttemptDto(string ClientName, bool Success, string? ErrorMessage, DateTime Timestamp);

    #endregion

    #region Internal Methods

    private async Task AddToHistoryAsync(Notification notification)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Notifications.Insert(0, notification);

            // Поддерживаем O(1) счётчик
            if (!notification.IsRead) _unreadCount++;

            // Вытесняем самые старые, поддерживая счётчик
            while (Notifications.Count > MaxNotifications)
            {
                var last = Notifications[^1];
                if (!last.IsRead) _unreadCount = Math.Max(0, _unreadCount - 1);
                Notifications.RemoveAt(Notifications.Count - 1);
            }

            RaiseUnreadProperties();
        });
    }

    private async Task ShowToastInternalAsync(Notification notification, int durationMs, CancellationToken ct)
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        _toastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var localToken = _toastCts.Token;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CurrentToast = notification;
            this.RaisePropertyChanged(nameof(IsToastVisible));
        });

        _ = AutoDismissToastAsync(notification, durationMs, localToken);
    }

    private async Task AutoDismissToastAsync(Notification notification, int durationMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(durationMs, ct);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CurrentToast == notification)
                {
                    CurrentToast = null;
                    this.RaisePropertyChanged(nameof(IsToastVisible));
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region UI Commands

    /// <summary>
    /// Отмечает все уведомления как прочитанные и сохраняет в БД.
    /// </summary>
    public void MarkAllAsRead()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var notification in Notifications)
                notification.IsRead = true;

            _unreadCount = 0;
            RaiseUnreadProperties();
        });

        _ = Task.Run(async () =>
        {
            try { await _repository.MarkAllAsReadAsync(); }
            catch (Exception ex) { Log.Warn($"[NotificationService] MarkAllAsRead DB error: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Удаляет все уведомления из памяти и БД.
    /// </summary>
    public void ClearAll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Notifications.Clear();
            _unreadCount = 0;
            RaiseUnreadProperties();
        });

        _ = Task.Run(async () =>
        {
            try { await _repository.ClearAllAsync(); }
            catch (Exception ex) { Log.Warn($"[NotificationService] ClearAll DB error: {ex.Message}"); }
        });
    }

    public void Remove(Notification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Notifications.Remove(notification))
            {
                if (!notification.IsRead) _unreadCount = Math.Max(0, _unreadCount - 1);
                RaiseUnreadProperties();
            }
        });
    }

    public void DismissToast()
    {
        _toastCts?.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            CurrentToast = null;
            this.RaisePropertyChanged(nameof(IsToastVisible));
        });
    }

    public void MarkAsRead(Notification notification)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (notification.IsRead) return;

            notification.IsRead = true;
            _unreadCount = Math.Max(0, _unreadCount - 1);
            RaiseUnreadProperties();
        });
    }

    /// <summary>
    /// Поднимает PropertyChanged для счётчика непрочитанных.
    /// Вызывается только после мутаций — не в горячем пути биндинга.
    /// </summary>
    private void RaiseUnreadProperties()
    {
        this.RaisePropertyChanged(nameof(UnreadCount));
        this.RaisePropertyChanged(nameof(HasUnread));
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        Notifications.Clear();
        CurrentToast = null;
        Log.Info("[NotificationService] Disposed");
    }

    #endregion
}