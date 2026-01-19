using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;

    [Reactive] public string DownloadPath { get; set; }
    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }

    // Новые свойства
    [Reactive] public int LoadBatchSize { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }

    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string? UserEmail { get; private set; }

    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }

    public SettingsViewModel(LibraryService library, GoogleAuthService auth)
    {
        _library = library;
        _auth = auth;

        // Загрузка настроек
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        LoadBatchSize = _library.Data.LoadBatchSize;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;

        // Валидация при загрузке (на всякий случай)
        if (LoadBatchSize < 5) LoadBatchSize = 20;

        UpdateAuthState();

        // Сохранение при изменении
        this.WhenAnyValue(x => x.DownloadPath)
            .Subscribe(path => { if (!string.IsNullOrEmpty(path)) _library.DownloadPath = path; });

        this.WhenAnyValue(x => x.DiscordRpcEnabled)
            .Subscribe(v => { _library.Data.DiscordRpcEnabled = v; _library.Save(); });

        this.WhenAnyValue(x => x.AutoPlayOnPaste)
            .Subscribe(v => { _library.Data.AutoPlayOnUrlPaste = v; _library.Save(); });

        // Новые подписки
        this.WhenAnyValue(x => x.LoadBatchSize)
            .Subscribe(v => { _library.Data.LoadBatchSize = v; _library.Save(); });

        this.WhenAnyValue(x => x.EnableSmoothLoading)
            .Subscribe(v => { _library.Data.EnableSmoothLoading = v; _library.Save(); });

        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            // Логика диалога выбора папки (оставим как заглушку или реализуем позже с TopLevel)
            await Task.CompletedTask;
        });

        ClearHistoryCommand = ReactiveCommand.Create(() => _library.ClearHistory());
        ResetLibraryCommand = ReactiveCommand.Create(() => _library.Reset());

        LoginCommand = ReactiveCommand.CreateFromTask(async () => { await _auth.StartLoginAsync(); UpdateAuthState(); });
        LogoutCommand = ReactiveCommand.Create(() => { _auth.Logout(); UpdateAuthState(); });
    }

    private void UpdateAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        UserEmail = _auth.State.UserEmail;
    }
}