using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using LMP.Core.Models;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

public sealed class PlaylistEditorViewModel : ViewModelBase
{
    [Reactive] public string Name { get; set; }
    [Reactive] public string? ThumbnailUrl { get; set; }
    [Reactive] public string? CustomColor { get; set; }

    // ═══ Sync ═══

    /// <summary>
    /// Показывать секцию синхронизации.
    /// true если: пользователь аутентифицирован ИЛИ плейлист уже привязан к облаку.
    /// </summary>
    [Reactive] public bool ShowSyncSection { get; set; }

    /// <summary>
    /// Текущее состояние переключателя «Синхронизировать с YouTube».
    /// </summary>
    [Reactive] public bool IsSyncedToCloud { get; set; }

    /// <summary>
    /// Пользователь аутентифицирован — можно менять состояние toggle.
    /// </summary>
    public bool IsAuthenticated { get; }

    /// <summary>
    /// У плейлиста есть привязка к YouTube (YoutubeId != null).
    /// </summary>
    public bool HasYoutubeBinding { get; }

    /// <summary>
    /// Исходное значение синхронизации (для определения, изменилось ли).
    /// </summary>
    public bool OriginalSyncState { get; }

    // ═══ Validation ═══
    [Reactive] public string? ErrorMessage { get; private set; }
    [Reactive] public bool HasErrors { get; private set; }
    public IObservable<bool> CanSave { get; }

    // ═══ Preview ═══
    [Reactive] public string? ThumbnailPreviewUrl { get; private set; }
    [Reactive] public bool HasThumbnailPreview { get; private set; }
    [Reactive] public IBrush ColorPreviewBrush { get; private set; } = Brushes.Transparent;

    public PlaylistEditorViewModel(
        string name = "",
        string? thumbnailUrl = null,
        string? customColor = null,
        bool showSync = false,
        bool isSynced = false,
        bool isAuthenticated = false,
        bool hasYoutubeBinding = false)
    {
        Name = name;
        ThumbnailUrl = thumbnailUrl;
        CustomColor = customColor;
        ShowSyncSection = showSync;
        IsSyncedToCloud = isSynced;
        OriginalSyncState = isSynced;
        IsAuthenticated = isAuthenticated;
        HasYoutubeBinding = hasYoutubeBinding;

        // Валидация при изменении любого поля
        this.WhenAnyValue(x => x.Name, x => x.ThumbnailUrl, x => x.CustomColor)
            .Subscribe(_ => UpdateValidation())
            .DisposeWith(Disposables);

        // Кнопка Save активна только если нет ошибок
        CanSave = this.WhenAnyValue(x => x.HasErrors, errors => !errors);

        // Debounce превью обложки
        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(url =>
            {
                var isValid = IsValidUri(url);
                HasThumbnailPreview = isValid;
                ThumbnailPreviewUrl = isValid ? url : null;
            })
            .DisposeWith(Disposables);

        // Debounce превью цвета
        this.WhenAnyValue(x => x.CustomColor)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(colorStr => ColorPreviewBrush = TryParseColor(colorStr))
            .DisposeWith(Disposables);
    }

    #region Validation

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            SetError(SL["Error_EmptyName"] ?? "Name cannot be empty");
            return;
        }

        if (!string.IsNullOrWhiteSpace(ThumbnailUrl) && !IsValidUri(ThumbnailUrl))
        {
            SetError(SL["Error_InvalidUrl"] ?? "Invalid cover URL format");
            return;
        }

        if (!string.IsNullOrWhiteSpace(CustomColor) && !IsValidColor(CustomColor))
        {
            SetError(SL["Error_InvalidColor"] ?? "Invalid HEX color (example: #FF5555)");
            return;
        }

        ClearError();
    }

    private void SetError(string msg)
    {
        ErrorMessage = msg;
        HasErrors = true;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        HasErrors = false;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Проверяет валидность URI. Internal — переиспользуется в PlaylistViewModel.
    /// </summary>
    internal static bool IsValidUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps ||
                uri.Scheme == "avares");
    }

    private static bool IsValidColor(string? colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr)) return false;
        try { Color.Parse(colorStr); return true; }
        catch { return false; }
    }

    private static IBrush TryParseColor(string? colorStr)
    {
        if (IsValidColor(colorStr))
            return new SolidColorBrush(Color.Parse(colorStr!));
        return Brushes.Transparent;
    }

    #endregion

    #region Factory Methods

    /// <summary>
    /// Создаёт VM для диалога создания нового плейлиста.
    /// </summary>
    public static PlaylistEditorViewModel ForCreate() =>
        new(name: "", thumbnailUrl: null, customColor: null,
            showSync: false, isSynced: false,
            isAuthenticated: false, hasYoutubeBinding: false);

    /// <summary>
    /// Создаёт VM для диалога редактирования существующего плейлиста.
    /// </summary>
    public static PlaylistEditorViewModel ForEdit(Playlist playlist, bool isAuthenticated) =>
        new(name: playlist.Name,
            thumbnailUrl: playlist.ThumbnailUrl,
            customColor: playlist.CustomColor,
            showSync: isAuthenticated || playlist.IsFromAccount,
            isSynced: playlist.IsFromAccount,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: !string.IsNullOrEmpty(playlist.YoutubeId));

    #endregion

    #region Result

    /// <summary>
    /// Собирает результат редактирования (без sync — sync обрабатывается отдельно).
    /// </summary>
    public EditPlaylistResult ToResult() => new()
    {
        Name = Name.Trim(),
        ThumbnailUrl = string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : ThumbnailUrl.Trim(),
        CustomColor = string.IsNullOrWhiteSpace(CustomColor) ? null : CustomColor.Trim()
    };

    /// <summary>
    /// Проверяет, изменил ли пользователь состояние синхронизации.
    /// </summary>
    public bool SyncStateChanged => IsSyncedToCloud != OriginalSyncState;

    #endregion
}