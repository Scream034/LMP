using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using LMP.Core.Data.Entities;
using LMP.Core.Data.Repositories;
using LMP.Core.Helpers;
using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

public sealed class NotificationService : ReactiveObject, IDisposable
{
    private readonly LibraryService _libraryService;
    private readonly INotificationRepository _repository;

    private const int MaxNotifications = 50;
    private const int DefaultToastDuration = 4000;

    public ObservableCollection<Notification> Notifications { get; } =[];

    public int UnreadCount => Notifications.Count(n => !n.IsRead);
    public bool HasUnread => UnreadCount > 0;

    private Notification? _currentToast;
    public Notification? CurrentToast
    {
        get => _currentToast;
        private set => this.RaiseAndSetIfChanged(ref _currentToast, value);
    }

    public bool IsToastVisible => CurrentToast != null;

    private CancellationTokenSource? _toastCts;
    private bool _isInitialized;

    public NotificationService(LibraryService libraryService, INotificationRepository repository)
    {
        _libraryService = libraryService;
        _repository = repository;
        Log.Info("[NotificationService] Initialized");
    }

    #region Initialization

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
                    Notifications.Add(EntityToModel(entity));
                }

                this.RaisePropertyChanged(nameof(UnreadCount));
                this.RaisePropertyChanged(nameof(HasUnread));
            });

            _isInitialized = true;
            Log.Info($"[NotificationService] Loaded {entities.Count} notifications from DB");
        }
        catch (Exception ex)
        {
            Log.Error($"[NotificationService] Failed to load history: {ex.Message}");
            _isInitialized = true;
        }
    }

    #endregion

    #region Public API

    public async Task ShowToastAsync(
        string titleKey,
        string messageKey,
        NotificationSeverity severity = NotificationSeverity.Info,
        int durationMs = 0,
        object[]? messageArgs = null,
        CancellationToken ct = default)
    {
        if (durationMs <= 0)
            durationMs = DefaultToastDuration;

        var notification = new Notification
        {
            TitleKey = titleKey,
            MessageKey = messageKey,
            MessageArgs = messageArgs,
            Severity = severity
        };

        await AddToHistoryAsync(notification);
        await PersistAsync(notification);
        await ShowToastInternalAsync(notification, durationMs, ct);
    }

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
        if (durationMs <= 0)
            durationMs = DefaultToastDuration;

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
        NotificationSeverity severity = NotificationSeverity.Info,
        CancellationToken ct = default)
    {
        await OsNotificationHelper.ShowAsync(title, message, severity);
    }

    public void PlayErrorSound()
    {
        var settings = _libraryService.Settings.Audio;
        if (!settings.PlayErrorSound)
            return;

        ErrorSoundPlayer.PlayError();
    }

    public static void PlaySuccessSound()
    {
        ErrorSoundPlayer.PlaySuccess();
    }

    #endregion

    #region Persistence

    private async Task PersistAsync(Notification notification)
    {
        try
        {
            var entity = ModelToEntity(notification);
            await _repository.AddAsync(entity);
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
        {
            argsJson = JsonSerializer.Serialize(n.MessageArgs.Select(a => a?.ToString()).ToArray());
        }

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
                {
                    attempts = new ObservableCollection<AttemptRecord>(
                        dtos.Select(d => new AttemptRecord(d.ClientName, d.Success, d.ErrorMessage, d.Timestamp)));
                }
            }
            catch { /* ignore */ }
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

            while (Notifications.Count > MaxNotifications)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }

            this.RaisePropertyChanged(nameof(UnreadCount));
            this.RaisePropertyChanged(nameof(HasUnread));
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

    public void MarkAllAsRead()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var notification in Notifications)
                notification.IsRead = true;

            this.RaisePropertyChanged(nameof(UnreadCount));
            this.RaisePropertyChanged(nameof(HasUnread));
        });

        _ = Task.Run(async () =>
        {
            try { await _repository.MarkAllAsReadAsync(); }
            catch (Exception ex) { Log.Warn($"[NotificationService] MarkAllAsRead DB error: {ex.Message}"); }
        });
    }

    public void ClearAll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Notifications.Clear();
            this.RaisePropertyChanged(nameof(UnreadCount));
            this.RaisePropertyChanged(nameof(HasUnread));
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
            Notifications.Remove(notification);
            this.RaisePropertyChanged(nameof(UnreadCount));
            this.RaisePropertyChanged(nameof(HasUnread));
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
            notification.IsRead = true;
            this.RaisePropertyChanged(nameof(UnreadCount));
            this.RaisePropertyChanged(nameof(HasUnread));
        });
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
        Notifications.Clear();
        CurrentToast = null;
        Log.Info("[NotificationService] Disposed");
    }

    #endregion
}