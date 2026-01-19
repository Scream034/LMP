// ViewModels/SettingsViewModel.cs
using System.Reactive;
using System.Reactive.Linq;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;

    // === НАСТРОЙКИ ===
    [Reactive] public string DownloadPath { get; set; } = string.Empty;
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }
    [Reactive] public int LoadBatchSize { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }
    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }

    // === ЯЗЫКИ ===
    public List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    // === АВТОРИЗАЦИЯ ===
    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string? UserEmail { get; private set; }
    [Reactive] public string? UserName { get; private set; }

    // === КОМАНДЫ ===
    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }

    // Свойство для отображения Email или текста "Не вошел"
    public string DisplayEmail => string.IsNullOrWhiteSpace(UserEmail)
        ? L["Auth_NotSignedIn"]
        : UserEmail;

    // Свойство для статуса авторизации
    public string AuthStatusText => IsAuthenticated
        ? L["Auth_LoggedIn"]
        : L["Auth_Guest"];

    public SettingsViewModel(LibraryService library, GoogleAuthService auth, IDialogService dialog)
    {
        _library = library;
        _auth = auth;
        _dialog = dialog;

        // Загружаем настройки
        LoadSettings();
        UpdateAuthState();

        // === ПОДПИСКИ НА ИЗМЕНЕНИЯ ===

        // Магия: когда меняется язык или статус входа, уведомляем интерфейс об обновлении
        LocalizationService.Instance.LanguageChanged += (s, e) =>
        {
            this.RaisePropertyChanged(nameof(DisplayEmail));
            this.RaisePropertyChanged(nameof(AuthStatusText));
        };

        // Если IsAuthenticated меняется, тоже обновляем текст
        this.WhenAnyValue(x => x.IsAuthenticated, x => x.UserEmail)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(DisplayEmail));
                this.RaisePropertyChanged(nameof(AuthStatusText));
            });

        // Язык
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1)
            .WhereNotNull()
            .Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();
            });

        // Настройки звука
        this.WhenAnyValue(x => x.MaxVolumeLimit)
            .Skip(1)
            .Subscribe(v => { _library.Data.MaxVolumeLimit = v; _library.Save(); });

        this.WhenAnyValue(x => x.TargetGainDb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(v => { _library.Data.TargetGainDb = v; _library.Save(); });

        // Общие настройки
        this.WhenAnyValue(x => x.EnableSmoothLoading)
            .Skip(1)
            .Subscribe(v => { _library.Data.EnableSmoothLoading = v; _library.Save(); });

        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Skip(1)
            .Subscribe(v => { _library.Data.DiscordRpcEnabled = v; _library.Save(); });

        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Skip(1)
            .Subscribe(v => { _library.Data.AutoPlayOnUrlPaste = v; _library.Save(); });

        // === КОМАНДЫ ===

        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);

        ClearHistoryCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var loc = LocalizationService.Instance;
            bool confirmed = await _dialog.ConfirmAsync(
                loc["Dialog_Confirm"],
                loc["Dialog_ClearHistoryMessage"]);

            if (confirmed)
            {
                _library.ClearHistory();
                await _dialog.ShowInfoAsync(loc["Dialog_Done"], loc["Dialog_HistoryCleared"]);
            }
        });

        ResetLibraryCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var loc = LocalizationService.Instance;
            bool confirmed = await _dialog.ConfirmAsync(
                loc["Dialog_Warning"],
                loc["Dialog_ResetMessage"]);

            if (confirmed)
            {
                _library.Reset();
                LoadSettings(); // Перезагружаем все настройки в UI
                await _dialog.ShowInfoAsync(loc["Dialog_Done"], loc["Dialog_ResetComplete"]);
            }
        });

        LoginCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _auth.StartLoginAsync();
            UpdateAuthState();
        });

        LogoutCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var loc = LocalizationService.Instance;
            bool confirmed = await _dialog.ConfirmAsync(
                loc["Auth_Logout"],
                loc["Dialog_LogoutMessage"]);

            if (confirmed)
            {
                _auth.Logout();
                UpdateAuthState();
            }
        });
    }

    private void LoadSettings()
    {
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        LoadBatchSize = _library.Data.LoadBatchSize;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;
        MaxVolumeLimit = _library.Data.MaxVolumeLimit;
        TargetGainDb = _library.Data.TargetGainDb;

        // Установка языка
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == _library.Data.LanguageCode)
                           ?? Languages[0];
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await _dialog.SelectFolderAsync(DownloadPath);
        if (!string.IsNullOrEmpty(newPath))
        {
            DownloadPath = newPath;
            _library.DownloadPath = newPath;
            _library.Save();
        }
    }

    private void UpdateAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        UserEmail = _auth.State.UserEmail;
        UserName = _auth.State.UserName;
    }
}