// ViewModels/SettingsViewModel.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;
    private readonly IDialogService _dialog;

    // === ЛОКАЛИЗАЦИЯ (прямые свойства для надёжного биндинга) ===
    [Reactive] public string L_Title { get; private set; } = "";
    [Reactive] public string L_AccountLanguage { get; private set; } = "";
    [Reactive] public string L_Audio { get; private set; } = "";
    [Reactive] public string L_Storage { get; private set; } = "";
    [Reactive] public string L_General { get; private set; } = "";
    [Reactive] public string L_Login { get; private set; } = "";
    [Reactive] public string L_Logout { get; private set; } = "";
    [Reactive] public string L_Language { get; private set; } = "";
    [Reactive] public string L_MaxVolume { get; private set; } = "";
    [Reactive] public string L_MaxVolumeDesc { get; private set; } = "";
    [Reactive] public string L_Gain { get; private set; } = "";
    [Reactive] public string L_GainDesc { get; private set; } = "";
    [Reactive] public string L_DownloadPath { get; private set; } = "";
    [Reactive] public string L_ChangeFolder { get; private set; } = "";
    [Reactive] public string L_ClearHistory { get; private set; } = "";
    [Reactive] public string L_ResetApp { get; private set; } = "";
    [Reactive] public string L_Discord { get; private set; } = "";
    [Reactive] public string L_AutoPaste { get; private set; } = "";
    [Reactive] public string L_SmoothLoading { get; private set; } = "";

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

    public SettingsViewModel(LibraryService library, GoogleAuthService auth, IDialogService dialog)
    {
        _library = library;
        _auth = auth;
        _dialog = dialog;

        // Загружаем настройки
        LoadSettings();
        UpdateAuthState();
        UpdateLocalizedStrings();

        // Подписка на смену языка
        LocalizationService.Instance.LanguageChanged += (_, _) => UpdateLocalizedStrings();

        // === ПОДПИСКИ НА ИЗМЕНЕНИЯ ===

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

    private void UpdateLocalizedStrings()
    {
        var loc = LocalizationService.Instance;

        L_Title = loc["Settings_Title"];
        L_AccountLanguage = loc["Settings_Account_Language"].ToUpperInvariant();
        L_Audio = loc["Settings_Audio"].ToUpperInvariant();
        L_Storage = loc["Settings_Storage_Data"].ToUpperInvariant();
        L_General = loc["Settings_General"].ToUpperInvariant();
        L_Login = loc["Auth_Login"];
        L_Logout = loc["Auth_Logout"];
        L_Language = loc["Language"];
        L_MaxVolume = loc["Audio_MaxVolume"];
        L_MaxVolumeDesc = loc["Audio_MaxVolumeDesc"];
        L_Gain = loc["Audio_Gain"];
        L_GainDesc = loc["Audio_GainDesc"];
        L_DownloadPath = loc["Storage_Path"];
        L_ChangeFolder = loc["Storage_Change"];
        L_ClearHistory = loc["Storage_ClearHistory"];
        L_ResetApp = loc["Storage_ResetApp"];
        L_Discord = loc["General_Discord"];
        L_AutoPaste = loc["General_AutoPaste"];
        L_SmoothLoading = loc["Perf_SmoothLoading"];
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