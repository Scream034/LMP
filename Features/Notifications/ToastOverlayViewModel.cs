using System.Reactive;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using Notification = LMP.Core.Models.Notification;

namespace LMP.Features.Notifications;

public sealed class ToastOverlayViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;

    public Notification? CurrentToast => _notificationService.CurrentToast;
    public bool IsVisible => _notificationService.IsToastVisible;

    #region Null-safe wrapper properties for XAML binding

    public string ToastTitle => CurrentToast?.Title ?? string.Empty;
    public string ToastMessage => CurrentToast?.Message ?? string.Empty;
    public string ToastIcon => CurrentToast?.Icon ?? string.Empty;
    
    /// <summary>
    /// Severity для конвертера в XAML.
    /// </summary>
    public NotificationSeverity ToastSeverity => CurrentToast?.Severity ?? NotificationSeverity.Info;

    #endregion

    public ReactiveCommand<Unit, Unit> DismissCommand { get; }

    public ToastOverlayViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;

        _notificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotificationService.CurrentToast))
            {
                this.RaisePropertyChanged(nameof(CurrentToast));
                this.RaisePropertyChanged(nameof(IsVisible));
                RaiseToastWrapperProperties();
            }
            if (e.PropertyName == nameof(NotificationService.IsToastVisible))
            {
                this.RaisePropertyChanged(nameof(IsVisible));
            }
        };

        DismissCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _notificationService.DismissToast();
        }));
    }

    private void RaiseToastWrapperProperties()
    {
        this.RaisePropertyChanged(nameof(ToastTitle));
        this.RaisePropertyChanged(nameof(ToastMessage));
        this.RaisePropertyChanged(nameof(ToastIcon));
        this.RaisePropertyChanged(nameof(ToastSeverity));
    }
}