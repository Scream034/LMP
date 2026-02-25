using System.Reactive;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Notifications;

public sealed class NotificationButtonViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;

    [Reactive] public bool IsPanelOpen { get; set; }

    public bool HasUnread => _notificationService.HasUnread;
    public int UnreadCount => _notificationService.UnreadCount;
    public string UnreadCountText => UnreadCount > 9 ? "9+" : UnreadCount.ToString();

    public ReactiveCommand<Unit, Unit> TogglePanelCommand { get; }

    public NotificationButtonViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;

        // Подписываемся на изменения в сервисе
        _notificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NotificationService.UnreadCount) or nameof(NotificationService.HasUnread))
            {
                this.RaisePropertyChanged(nameof(HasUnread));
                this.RaisePropertyChanged(nameof(UnreadCount));
                this.RaisePropertyChanged(nameof(UnreadCountText));
            }
        };

        TogglePanelCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsPanelOpen = !IsPanelOpen;

            // При открытии панели отмечаем все как прочитанные
            if (IsPanelOpen)
            {
                _notificationService.MarkAllAsRead();
            }
        }));
    }
}