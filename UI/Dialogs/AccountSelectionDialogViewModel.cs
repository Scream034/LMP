using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;

namespace LMP.UI.Dialogs;

public sealed class AccountSelectionDialogViewModel : ViewModelBase
{
    public ObservableCollection<YoutubeAccountItem> Accounts { get; }

    private YoutubeAccountItem? _selectedAccount;
    public YoutubeAccountItem? SelectedAccount
    {
        get => _selectedAccount;
        set => this.RaiseAndSetIfChanged(ref _selectedAccount, value);
    }

    public Action<YoutubeAccountItem?>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>
    /// Инициализирует модель представления выбора аккаунта.
    /// </summary>
    public AccountSelectionDialogViewModel(
        IEnumerable<YoutubeAccountItem> accounts,
        string? activeAuthUser = "")
    {
        Accounts = new ObservableCollection<YoutubeAccountItem>(accounts);

        // Логика выбора:
        // 1. Если у нас явно сохранен AuthUser (!= "") -> ищем точное совпадение.
        // 2. Если это свежий логин (AuthUser == "") -> доверяем флагу IsSelected от YouTube.
        // 3. Фоллбэк на первый аккаунт.
        SelectedAccount = Accounts.FirstOrDefault(a => a.AuthUser == activeAuthUser && !string.IsNullOrEmpty(activeAuthUser))
                       ?? Accounts.FirstOrDefault(a => a.IsSelected)
                       ?? Accounts.FirstOrDefault();

        var canConfirm = this.WhenAnyValue(
            x => x.SelectedAccount!,
            (object acc) => acc != null
        );

        ConfirmCommand = CreateCommand(ReactiveCommand.Create(() => OnResult?.Invoke(SelectedAccount), canConfirm));
        CancelCommand = CreateCommand(ReactiveCommand.Create(() => OnResult?.Invoke(null)));
    }
}