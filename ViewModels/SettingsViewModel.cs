// SettingsViewModel.cs
// ViewModel для страницы настроек приложения

using System.Reactive;
using System.Reactive.Linq;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

/// <summary>
/// ViewModel для страницы настроек.
/// Управляет:
/// - Путем загрузки файлов
/// - Настройками громкости и усиления
/// - Выбором языка
/// - Настройками качества аудио
/// - Авторизацией Google
/// - Discord RPC
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    // ЗАВИСИМОСТИ

    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    // НАСТРОЙКИ ПУТЕЙ И ЗАГРУЗКИ

    /// <summary>Путь для загрузки файлов</summary>
    [Reactive] public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Размер пакета загрузки</summary>
    [Reactive] public int LoadBatchSize { get; set; }

    /// <summary>Включить плавную загрузку</summary>
    [Reactive] public bool EnableSmoothLoading { get; set; }

    // НАСТРОЙКИ ЗВУКА

    /// <summary>Максимальный уровень громкости (100-500)</summary>
    [Reactive] public int MaxVolumeLimit { get; set; }

    /// <summary>Целевое усиление в децибелах (-20 до +20)</summary>
    [Reactive] public float TargetGainDb { get; set; }

    // НАСТРОЙКИ КАЧЕСТВА

    /// <summary>Предпочтение качества аудио</summary>
    public AudioQualityPreference QualityPreference
    {
        get => _library.Data.QualityPreference;
        set
        {
            if (_library.Data.QualityPreference == value) return;

            _library.Data.QualityPreference = value;
            _library.Save();

            // Очищаем кэш YouTube при смене качества
            _youtube.ClearCache();

            this.RaisePropertyChanged();

            Log.Info($"[Settings] Quality preference changed to: {value}");

            // Применяем к текущему треку если он играет
            ApplyQualityToCurrentTrack(value);
        }
    }

    /// <summary>Запоминать выбранный формат для каждого трека</summary>
    public bool RememberTrackFormat
    {
        get => _library.Data.RememberTrackFormat;
        set
        {
            if (_library.Data.RememberTrackFormat == value) return;

            _library.Data.RememberTrackFormat = value;
            _library.Save();
            this.RaisePropertyChanged();

            Log.Info($"[Settings] Remember track format: {value}");
        }
    }

    /// <summary>Список доступных настроек качества</summary>
    [Reactive] public List<AudioQualityPreference> QualityOptions { get; private set; }

    // ИНТЕГРАЦИИ

    /// <summary>Включен ли Discord RPC</summary>
    [Reactive] public bool DiscordRpcEnabled { get; set; }

    /// <summary>Автоматически воспроизводить при вставке URL</summary>
    [Reactive] public bool AutoPlayOnPaste { get; set; }

    // ЯЗЫКИ

    /// <summary>Список доступных языков</summary>
    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;

    /// <summary>Выбранный язык</summary>
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    // АВТОРИЗАЦИЯ

    /// <summary>Авторизован ли пользователь</summary>
    [Reactive] public bool IsAuthenticated { get; private set; }

    /// <summary>Email пользователя</summary>
    [Reactive] public string? UserEmail { get; private set; }

    /// <summary>Имя пользователя</summary>
    [Reactive] public string? UserName { get; private set; }

    /// <summary>Отображаемый email или заглушка</summary>
    public string DisplayEmail => string.IsNullOrWhiteSpace(UserEmail)
        ? L["Auth_NotSignedIn"]
        : UserEmail;

    /// <summary>Текст статуса авторизации</summary>
    public string AuthStatusText => IsAuthenticated
        ? L["Auth_LoggedIn"]
        : L["Auth_Guest"];

    // КОМАНДЫ

    /// <summary>Выбрать папку загрузки</summary>
    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }

    /// <summary>Очистить историю</summary>
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }

    /// <summary>Сбросить библиотеку</summary>
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }

    /// <summary>Войти в аккаунт</summary>
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }

    /// <summary>Выйти из аккаунта</summary>
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }

    // КОНСТРУКТОР

    /// <summary>
    /// Создает ViewModel настроек
    /// </summary>
    public SettingsViewModel(
        LibraryService library,
        GoogleAuthService auth,
        IDialogService dialog,
        AudioEngine audio,
        YoutubeProvider youtube)
    {
        _library = library;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _youtube = youtube;

        QualityOptions = [.. Enum.GetValues<AudioQualityPreference>()];

        LoadSettings();
        UpdateAuthState();

        // ПОДПИСКИ

        // Обновление локализованных текстов при смене языка
        LocalizationService.Instance.LanguageChanged += (s, e) =>
        {
            this.RaisePropertyChanged(nameof(DisplayEmail));
            this.RaisePropertyChanged(nameof(AuthStatusText));
            QualityOptions = [.. Enum.GetValues<AudioQualityPreference>()];
        };

        // Обновление текстов при изменении состояния авторизации
        this.WhenAnyValue(x => x.IsAuthenticated, x => x.UserEmail)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(DisplayEmail));
                this.RaisePropertyChanged(nameof(AuthStatusText));
            });

        // Автосохранение выбранного языка
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1)
            .WhereNotNull()
            .Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();

                Log.Info($"[Settings] Language changed to: {lang.Code}");
            });

        // Максимальная громкость - применяем мгновенно
        this.WhenAnyValue(x => x.MaxVolumeLimit)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.MaxVolumeLimit = v;
                _library.Save();
                _audio.UpdateAudioSettings();

                Log.Info($"[Settings] MaxVolumeLimit changed to: {v}");
            });

        // Усиление - применяем с небольшой задержкой (throttle)
        this.WhenAnyValue(x => x.TargetGainDb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(v =>
            {
                _library.Data.TargetGainDb = v;
                _library.Save();
                _audio.UpdateAudioSettings();

                Log.Info($"[Settings] TargetGainDb changed to: {v}dB");
            });

        // Плавная загрузка
        this.WhenAnyValue(x => x.EnableSmoothLoading)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.EnableSmoothLoading = v;
                _library.Save();
            });

        // Discord RPC
        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.DiscordRpcEnabled = v;
                _library.Save();

                Log.Info($"[Settings] Discord RPC: {v}");
            });

        // Автовоспроизведение при вставке URL
        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Skip(1)
            .Subscribe(v =>
            {
                _library.Data.AutoPlayOnUrlPaste = v;
                _library.Save();
            });


        // КОМАНДЫ


        // Выбор папки загрузки
        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);

        // Очистка истории
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

                Log.Info("[Settings] History cleared");
            }
        });

        // Сброс библиотеки
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var loc = LocalizationService.Instance;

            bool confirmed = await _dialog.ConfirmAsync(
                loc["Dialog_Warning"],
                loc["Dialog_ResetMessage"]);

            if (confirmed)
            {
                _library.Reset();
                LoadSettings();
                await _dialog.ShowInfoAsync(loc["Dialog_Done"], loc["Dialog_ResetComplete"]);

                Log.Info("[Settings] Library reset");
            }
        });

        // Вход в аккаунт
        LoginCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await _auth.StartLoginAsync();
            UpdateAuthState();
        });

        // Выход из аккаунта
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

                Log.Info("[Settings] Logged out");
            }
        });
    }

    // ЗАГРУЗКА НАСТРОЕК

    /// <summary>
    /// Загружает текущие настройки из библиотеки
    /// </summary>
    private void LoadSettings()
    {
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        LoadBatchSize = _library.Data.LoadBatchSize;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;
        MaxVolumeLimit = _library.Data.MaxVolumeLimit;
        TargetGainDb = _library.Data.TargetGainDb;

        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == _library.Data.LanguageCode)
            ?? Languages[0];

        Log.Info("[Settings] Settings loaded");
    }

    // ВЫБОР ПАПКИ ЗАГРУЗКИ

    /// <summary>
    /// Открывает диалог выбора папки загрузки
    /// </summary>
    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await _dialog.SelectFolderAsync(DownloadPath);

        if (!string.IsNullOrEmpty(newPath))
        {
            DownloadPath = newPath;
            _library.DownloadPath = newPath;
            _library.Save();

            Log.Info($"[Settings] Download path changed to: {newPath}");
        }
    }

    // ОБНОВЛЕНИЕ СОСТОЯНИЯ АВТОРИЗАЦИИ

    /// <summary>
    /// Обновляет состояние авторизации из сервиса
    /// </summary>
    private void UpdateAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        UserEmail = _auth.State.UserEmail;
        UserName = _auth.State.UserName;
    }

    /// <summary>
    /// Применяет изменение качества к текущему воспроизводимому треку
    /// </summary>
    private void ApplyQualityToCurrentTrack(AudioQualityPreference preference)
    {
        var currentTrack = _audio.CurrentTrack;
        if (currentTrack == null || currentTrack.IsDownloaded) return;

        // Определяем целевой контейнер
        string targetContainer = preference switch
        {
            AudioQualityPreference.BestAvailable => "webm",  // Opus
            AudioQualityPreference.Standard => "mp4",        // AAC
            _ => "webm"
        };

        //  Всегда применяем новую глобальную настройку, даже если пользователь ранее выбрал формат вручную.
        // Мы предполагаем, что изменение глобальной настройки является явным действием, которое должно переопределить текущее состояние.
        // Передаем 0 в качестве битрейта, чтобы провайдер выбрал лучший доступный битрейт для этого контейнера.
        currentTrack.PreferredContainer = targetContainer;
        currentTrack.PreferredBitrate = 0;

        Log.Info($"[Settings] Applying quality {preference} to current track (Force switch to {targetContainer})...");
        _ = _audio.SwitchQualityAsync(targetContainer, 0);
    }
}