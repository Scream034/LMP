using System.IO;
using System.IO.Compression;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using LMP.Core.Audio.Http;
using LMP.Core.Services;
using LMP.UI.Helpers;
using LMP.UI.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

public sealed class AuthDialogViewModel : ViewModelBase
{
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _userData;
    private readonly LocalAuthServer _localServer;

    [Reactive] public string CookiesText { get; set; } = string.Empty;
    [Reactive] public bool IsAuthenticating { get; private set; }
    [Reactive] public string StatusText { get; private set; } = string.Empty;
    [Reactive] public bool IsError { get; private set; }
    [Reactive] public int AttemptCount { get; private set; }

    [Reactive] public bool IsExtensionDownloading { get; private set; }
    [Reactive] public bool IsExtensionReady { get; private set; }
    [Reactive] public bool IsGuideExpanded { get; set; } = true;
    [Reactive] public string ExtensionFolderPath { get; private set; } = string.Empty;

    /// <summary>
    /// Свойство-индикатор активности спиннера (загрузка расширения или проверка авторизации).
    /// </summary>
    public bool IsSpinnerActive => IsAuthenticating || IsExtensionDownloading;

    public Action<bool>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> AuthenticateCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadExtensionCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPathCommand { get; }
    public ReactiveCommand<string, Unit> CopyLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    private readonly CancellationTokenSource _cts = new();

    public AuthDialogViewModel(
        CookieAuthService auth,
        YoutubeUserDataService userData,
        LocalAuthServer localServer)
    {
        _auth = auth;
        _userData = userData;
        _localServer = localServer;

        AuthenticateCommand = CreateCommand(ReactiveCommand.CreateFromTask(AuthenticateAsync));
        DownloadExtensionCommand = CreateCommand(ReactiveCommand.CreateFromTask(DownloadExtensionAsync));
        CopyPathCommand = CreateCommand(ReactiveCommand.CreateFromTask(CopyPathToClipboardAsync));
        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask<string>(CopyLinkAsync));
        CloseCommand = CreateCommand(ReactiveCommand.Create(() => OnResult?.Invoke(false)));

        // Инициализируем статус по умолчанию текстом ожидания
        StatusText = SL["Dialog_Login_WaitingStatus"] ?? "Ожидаем запрос от расширения или введите куки вручную...";

        // Отслеживаем изменения состояний для управления системным спиннером
        this.WhenAnyValue(x => x.IsAuthenticating, x => x.IsExtensionDownloading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsSpinnerActive)));

        _ = StartListeningAsync(_cts.Token);
    }

    private async Task StartListeningAsync(CancellationToken ct)
    {
        try
        {
            var cookies = await _localServer.WaitForCookiesAsync(ct);
            if (!string.IsNullOrEmpty(cookies) && !ct.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CookiesText = cookies;
                    AuthenticateCommand.Execute(Unit.Default);
                });
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
    }

    private async Task AuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(CookiesText))
        {
            SetStatus(SL["Dialog_Login_WaitingStatus"], isError: true);
            return;
        }

        IsAuthenticating = true;
        AttemptCount++;
        SetStatus($"{SL["Splash_ConnectingYouTube"]} ({AttemptCount})", isError: false);

        try
        {
            _auth.SaveCookies(CookiesText);

            if (!_auth.IsAuthenticated)
            {
                SetStatus($"{SL["Dialog_Error_Title"]}: Не найден SAPISID!", isError: true);
                return;
            }

            SetStatus(SL["Nav_PleaseWait"], isError: false);

            // Строгая проверка сессии перед получением профиля
            var (isValid, error) = await _auth.ValidateSessionAsync();
            if (!isValid)
            {
                SetStatus(SL["Auth_SessionExpired_Message"] ?? "Куки недействительны или устарели.", isError: true);
                _auth.Logout();
                return;
            }

            var (name, email, avatar) = await _userData.GetAccountInfoAsync();

            // Защита от сбоя парсинга (профиль "User" без аватара расценивается как ошибка авторизации)
            if (string.IsNullOrEmpty(name) || (name.Equals("User", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(avatar)))
            {
                SetStatus(SL["Auth_ProfileLoadError_Message"], isError: true);
                _auth.Logout();
                return;
            }

            _auth.UpdateUserProfile(name, email, avatar);

            SetStatus($"{SL["Dialog_Success"]}! {name}", isError: false);
            await Task.Delay(1000);

            OnResult?.Invoke(true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private async Task DownloadExtensionAsync()
    {
        try
        {
            IsExtensionDownloading = true;
            SetStatus(SL["Splash_Initializing"], isError: false);

            SafeDeleteDirectory(G.Folder.Extension);
            Directory.CreateDirectory(G.Folder.Extension);

            using var response = await SharedHttpClient.Instance.GetAsync(
                G.AuthExtensionDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                _cts.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;

            await using (var downloadStream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
            await using (var fs = new FileStream(G.FilePath.TempAuthExtensionZipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                long totalBytesRead = 0;
                int bytesRead;

                while ((bytesRead = await downloadStream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token).ConfigureAwait(false);
                    totalBytesRead += bytesRead;

                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        var progressPercent = Math.Clamp((double)totalBytesRead / contentLength.Value * 100, 0, 100);
                        var totalMb = (double)totalBytesRead / (1024 * 1024);
                        var contentMb = (double)contentLength.Value / (1024 * 1024);
                        var progressText = string.Format(SL["Extension_Install_Downloading_Progress"], progressPercent, totalMb, contentMb);
                        Dispatcher.UIThread.Post(() => SetStatus(progressText, isError: false));
                    }
                    else
                    {
                        var totalMb = (double)totalBytesRead / (1024 * 1024);
                        var progressText = string.Format(SL["Extension_Install_Downloading_Indeterminate"], totalMb);
                        Dispatcher.UIThread.Post(() => SetStatus(progressText, isError: false));
                    }
                }
            }

            Dispatcher.UIThread.Post(() => SetStatus(SL["Splash_PreparingImages"], isError: false));

            ZipFile.ExtractToDirectory(G.FilePath.TempAuthExtensionZipFile, G.Folder.Extension);

            var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ExtensionFolderPath = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
                IsExtensionReady = true;
                IsGuideExpanded = true;
                SetStatus(SL["Dialog_Login_WaitingStatus"], isError: false);
            });

            await CopyPathToClipboardAsync();
        }
        catch (Exception)
        {
            SetStatus(SL["Extension_Install_DownloadError"], isError: true);
        }
        finally
        {
            IsExtensionDownloading = false;
            try { if (File.Exists(G.FilePath.TempAuthExtensionZipFile)) File.Delete(G.FilePath.TempAuthExtensionZipFile); } catch { /* ignore */ }
        }
    }

    private async Task CopyPathToClipboardAsync()
    {
        if (string.IsNullOrEmpty(ExtensionFolderPath)) return;
        await Clipboard.SetTextAsync(ExtensionFolderPath);
        CopyHintService.Instance.Show(SL["Extension_Path_Copied_Toast"] ?? "Путь скопирован! Добавьте распакованное расширение в браузер.", CopyHintKind.Success);
    }

    private async Task CopyLinkAsync(string url)
    {
        await Clipboard.SetTextAsync(url);
        CopyHintService.Instance.Show(SL["Extension_Link_Copied_Toast"] ?? "Ссылка скопирована! Перейдите в ваш браузер и вставьте её в адресную строку.", CopyHintKind.Success);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsError = isError;
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException)
        {
            try
            {
                var tempPath = path + "_deleted_" + Path.GetRandomFileName();
                Directory.Move(path, tempPath);
                _ = Task.Run(() => { try { Directory.Delete(tempPath, true); } catch { } });
            }
            catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}