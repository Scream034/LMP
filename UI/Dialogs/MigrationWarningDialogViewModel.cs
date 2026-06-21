using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using LMP.Core.Services;
using LMP.UI.Features.Shell;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel для диалога предупреждения об обновлении базы данных с таймером удержания.
/// </summary>
public sealed class MigrationWarningDialogViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly DispatcherTimer _timer;
    private int _secondsRemaining;

    [Reactive] public string Title { get; set; } = string.Empty;
    [Reactive] public string Message { get; set; } = string.Empty;
    [Reactive] public string ButtonText { get; set; } = string.Empty;
    [Reactive] public bool CanClose { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public MigrationWarningDialogViewModel(
        string title,
        string message,
        int countdownSeconds,
        Action onClose)
    {
        Title = title;
        Message = message;
        _onClose = onClose;
        _secondsRemaining = countdownSeconds;

        UpdateButtonState();

        CloseCommand = ReactiveCommand.Create(() =>
        {
            if (CanClose) _onClose();
        }, this.WhenAnyValue(x => x.CanClose));

        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnTimerTick);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            _timer.Stop();
            CanClose = true;
            UpdateButtonState();
        }
        else
        {
            UpdateButtonState();
        }
    }

    private void UpdateButtonState()
    {
        var L = LocalizationService.Instance;
        if (_secondsRemaining > 0)
        {
            ButtonText = string.Format(L["Dialog_LegacyPlaylists_CountdownButton"] ?? "Read ({0}s)", _secondsRemaining);
            CanClose = false;
        }
        else
        {
            ButtonText = L["Common_OK"] ?? "OK";
            CanClose = true;
        }
    }
}