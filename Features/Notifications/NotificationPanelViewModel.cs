// Features/Notifications/NotificationPanelViewModel.cs

using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using Notification = LMP.Core.Models.Notification;

namespace LMP.Features.Notifications;

public sealed class NotificationPanelViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;

    public ObservableCollection<Notification> Notifications => _notificationService.Notifications;
    public bool HasNotifications => Notifications.Count > 0;

    public ReactiveCommand<Unit, Unit> ClearAllCommand { get; }
    public ReactiveCommand<string?, Unit> CopyErrorCommand { get; }
    public ReactiveCommand<string?, Unit> CopyTrackUrlCommand { get; }

    public NotificationPanelViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;

        Notifications.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasNotifications));
        };

        ClearAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _notificationService.ClearAll();
        }));

        CopyErrorCommand = CreateCommand(ReactiveCommand.Create<string?>(details =>
        {
            if (string.IsNullOrEmpty(details)) return;
            CopyToClipboard(details, "Error details");
        }));

        CopyTrackUrlCommand = CreateCommand(ReactiveCommand.Create<string?>(trackId =>
        {
            if (string.IsNullOrEmpty(trackId)) return;

            var url = trackId.StartsWith("yt_")
                ? $"https://youtube.com/watch?v={trackId[3..]}"
                : trackId.StartsWith("http")
                    ? trackId
                    : $"https://youtube.com/watch?v={trackId}";

            CopyToClipboard(url, "Track URL");
        }));
    }

    private static void CopyToClipboard(string text, string description)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow?.Clipboard?.SetTextAsync(text);
                Log.Info($"[Notification] {description} copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Notification] Failed to copy: {ex.Message}");
        }
    }
}