using System.Reactive;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Features.Notifications;

public sealed class NotificationButtonViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly NotificationPanelViewModel _panelViewModel;

    [Reactive] public bool IsPanelOpen { get; set; }

    public bool HasUnread => _notificationService.HasUnread;
    public int UnreadCount => _notificationService.UnreadCount;
    public string UnreadCountText => UnreadCount > 9 ? "9+" : UnreadCount.ToString();

    public ReactiveCommand<Unit, Unit> TogglePanelCommand { get; }

    public NotificationButtonViewModel(
        NotificationService notificationService,
        NotificationPanelViewModel panelViewModel)
    {
        _notificationService = notificationService;
        _panelViewModel = panelViewModel;

        _notificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NotificationService.UnreadCount)
                                or nameof(NotificationService.HasUnread))
            {
                this.RaisePropertyChanged(nameof(HasUnread));
                this.RaisePropertyChanged(nameof(UnreadCount));
                this.RaisePropertyChanged(nameof(UnreadCountText));
            }
        };

        TogglePanelCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsPanelOpen = !IsPanelOpen;

            if (IsPanelOpen)
            {
                Log.Debug("[NotificationButton] Panel opened");
                _ = _panelViewModel.OnPanelOpenedAsync();
                _notificationService.MarkAllAsRead();
            }
        }));
    }
}