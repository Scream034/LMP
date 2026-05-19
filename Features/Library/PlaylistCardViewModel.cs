using System.Reactive;
using System.Reactive.Linq;
using LMP.Core.Helpers;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Library;

/// <summary>
/// ViewModel карточки плейлиста в библиотеке.
///
/// <para><b>Ответственность:</b></para>
/// <list type="bullet">
///   <item>Отображение данных плейлиста (название, обложка, кол-во треков, статус синхронизации)</item>
///   <item>Команды контекстного меню (открыть, играть, в очередь, редактировать, удалить)</item>
///   <item>Анимация появления (IsVisible + CSS-transition)</item>
///   <item>Реактивное обновление при изменении данных через <see cref="UpdateFrom"/></item>
/// </list>
///
/// <para><b>Жизненный цикл:</b></para>
/// <para>Создаётся в <see cref="LibraryViewModel.CreatePlaylistCardVm"/>,
/// обновляется через <see cref="UpdateFrom"/>,
/// удаляется через Dispose при удалении из коллекции.</para>
/// </summary>
public sealed class PlaylistCardViewModel : ViewModelBase
{
    #region Приватные поля

    private readonly CookieAuthService _auth;

    private readonly Func<string, Task>? _onDelete;
    private readonly Func<Core.Models.Playlist, Task>? _onEdit;
    private readonly Func<Core.Models.Playlist, Task> _addToQueueAction;
    private readonly Func<Core.Models.Playlist, Task> _playAction;
    private readonly Action<string> _onOpen;

    /// <summary>
    /// Обработчик смены языка: обновляет <see cref="FormattedTrackCount"/>
    /// (склонение зависит от языка).
    /// </summary>
    private readonly EventHandler<string> _languageChangedHandler;
    private bool _isDisposed;

    #endregion

    #region Свойства

    /// <summary>Исходный объект плейлиста (мутабельный, обновляется через <see cref="UpdateFrom"/>).</summary>
    public Core.Models.Playlist Playlist { get; }

    /// <summary>Уникальный идентификатор плейлиста.</summary>
    public string Id => Playlist.Id;

    /// <summary>Название плейлиста (реактивное).</summary>
    [Reactive] public string Name { get; private set; }

    /// <summary>URL обложки (реактивное, с upscale для YouTube-превью).</summary>
    [Reactive] public string? ThumbnailUrl { get; private set; }

    /// <summary>YouTube URL плейлиста для CopyLinkButton. Null если не привязан.</summary>
    public string? YoutubeUrl
    {
        get
        {
            // Если это плейлист "Понравившиеся" и пользователь вошел в аккаунт, даём ссылку на системный плейлист LM
            if (IsLikedPlaylist && _auth.IsAuthenticated)
                return "https://www.youtube.com/playlist?list=LM";

            return string.IsNullOrEmpty(Playlist.YoutubeId)
                ? null
                : $"https://www.youtube.com/playlist?list={Playlist.YoutubeId}";
        }
    }

    /// <summary>Количество треков в плейлисте.</summary>
    [Reactive] public int TrackCount { get; set; }

    /// <summary>
    /// Плейлист синхронизирован с YouTube Music (реактивное).
    /// Используется для отображения иконки облачка в UI.
    /// </summary>
    [Reactive] public bool IsSynced { get; private set; }

    /// <summary>Плейлист хранится локально (включая TwoWaySync).</summary>
    public bool IsLocal => Playlist.IsLocal;

    /// <summary>Плейлист только для чтения (CloudPublic).</summary>
    public bool IsReadOnly => !Playlist.IsEditable;

    /// <summary>Можно ли удалить (всё кроме Liked).</summary>
    public bool CanDelete => Playlist.Id != LibraryService.LikedPlaylistId;

    public bool CanCopyLink => !string.IsNullOrEmpty(YoutubeUrl);

    /// <summary>
    /// Можно ли открыть диалог редактирования.
    /// Liked-плейлист редактируем — обложка, цвет и описание доступны для изменения.
    /// Только CloudPublic-плейлисты полностью заблокированы.
    /// </summary>
    public bool CanEdit => Playlist.IsEditable;

    /// <summary>Это плейлист «Понравившиеся» (специальный UI).</summary>
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;

    /// <summary>
    /// Форматированное количество треков с правильным склонением.
    /// Примеры: "Пусто", "1 трек", "5 треков".
    /// </summary>
    public string FormattedTrackCount => FormatTrackCount(TrackCount);

    /// <summary>Есть обложка для отображения (не пустая и не Liked).</summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>Показывать placeholder вместо обложки.</summary>
    public bool ShowPlaceholder => string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>
    /// Видимость карточки (для stagger-анимации появления).
    /// Карточка создаётся с IsVisible=false, затем Show() устанавливает true,
    /// а CSS-transition делает плавное появление.
    /// </summary>
    [Reactive] public bool IsVisible { get; set; }

    #endregion

    #region Команды

    /// <summary>Открыть плейлист (навигация на страницу плейлиста).</summary>
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// <summary>Удалить плейлист (с подтверждением).</summary>
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    /// <summary>Добавить все треки плейлиста в очередь.</summary>
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    /// <summary>Воспроизвести плейлист с первого трека.</summary>
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }

    /// <summary>Открыть диалог редактирования плейлиста.</summary>
    public ReactiveCommand<Unit, Unit> EditCommand { get; }

    /// <summary>Скопировать ссылку на плейлист. Используется в ContextMenu (ПКМ).</summary>
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }

    #endregion

    #region Конструктор

    /// <param name="playlist">Объект плейлиста из БД.</param>
    /// <param name="trackCount">Количество треков (из JOIN-запроса).</param>
    /// <param name="onOpen">Callback навигации при открытии.</param>
    /// <param name="addToQueueAction">Callback добавления в очередь.</param>
    /// <param name="playAction">Callback воспроизведения.</param>
    /// <param name="onDelete">Callback удаления (опционально).</param>
    /// <param name="onEdit">Callback редактирования (опционально).</param>
    public PlaylistCardViewModel(
      CookieAuthService auth,
      Core.Models.Playlist playlist,
      int trackCount,
      Action<string> onOpen,
      Func<Core.Models.Playlist, Task> addToQueueAction,
      Func<Core.Models.Playlist, Task> playAction,
      Func<string, Task>? onDelete = null,
      Func<Core.Models.Playlist, Task>? onEdit = null)
    {
        Playlist = playlist;
        _auth = auth;
        _onOpen = onOpen;
        _addToQueueAction = addToQueueAction;
        _playAction = playAction;
        _onDelete = onDelete;
        _onEdit = onEdit;

        Name = playlist.Name;
        TrackCount = trackCount;
        ThumbnailUrl = UpscaleThumbnailUrl(playlist.ThumbnailUrl);
        IsSynced = playlist.IsFromAccount || (IsLikedPlaylist && _auth.IsAuthenticated);

        OpenCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (!_isDisposed) _onOpen(Playlist.Id);
        }));

        var canDelete = this.WhenAnyValue(x => x.CanDelete)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        DeleteCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
            {
                if (!_isDisposed && _onDelete != null) await _onDelete(Playlist.Id);
            }, canDelete));

        AddToQueueCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed) await _addToQueueAction(Playlist);
        }));

        PlayCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed) await _playAction(Playlist);
        }));

        EditCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (!_isDisposed && _onEdit != null) await _onEdit(Playlist);
        }));

        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (_isDisposed || string.IsNullOrEmpty(YoutubeUrl)) return;

                await Clipboard.SetTextAsync(YoutubeUrl);

                CopyHintService.Instance.Show(
                    LocalizationService.Instance["Playlist_LinkCopied"] ?? "Copied!",
                    CopyHintKind.Success);
            },
            this.WhenAnyValue(x => x.YoutubeUrl, url => !string.IsNullOrEmpty(url))
                .ObserveOn(RxSchedulers.MainThreadScheduler)));

        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HasThumbnail));
                this.RaisePropertyChanged(nameof(ShowPlaceholder));
            })
            .DisposeWith(Disposables);

        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    #endregion

    #region Обновление данных

    /// <summary>
    /// Обновляет все отображаемые данные карточки из свежего объекта плейлиста.
    ///
    /// <para><b>Обновляемые поля:</b></para>
    /// <list type="bullet">
    ///   <item>Название (<c>Name</c> + <c>StoredName</c>)</item>
    ///   <item>Количество треков (<c>TrackCount</c>)</item>
    ///   <item>Обложка (<c>ThumbnailUrl</c> с upscale)</item>
    ///   <item>Статус синхронизации (<c>IsSynced</c> + <c>SyncMode</c> + <c>YoutubeId</c>)</item>
    /// </list>
    ///
    /// <para><b>Оптимизация:</b> каждое поле обновляется только при реальном изменении,
    /// чтобы не вызывать лишних уведомлений <c>PropertyChanged</c>.</para>
    /// </summary>
    /// <param name="playlist">Свежие данные плейлиста из БД.</param>
    /// <param name="trackCount">Актуальное количество треков.</param>
    public void UpdateFrom(Core.Models.Playlist playlist, int trackCount)
    {
        if (_isDisposed) return;

        if (Name != playlist.Name)
        {
            Playlist.StoredName = playlist.StoredName;
            Name = playlist.Name;
        }

        if (TrackCount != trackCount)
            TrackCount = trackCount;

        var newUrl = UpscaleThumbnailUrl(playlist.ThumbnailUrl);
        if (ThumbnailUrl != newUrl)
        {
            Playlist.ThumbnailUrl = playlist.ThumbnailUrl;
            ThumbnailUrl = newUrl;
        }

        bool newSynced = playlist.IsFromAccount || (IsLikedPlaylist && _auth.IsAuthenticated);
        if (IsSynced != newSynced)
        {
            Playlist.SyncMode = playlist.SyncMode;
            Playlist.YoutubeId = playlist.YoutubeId;
            IsSynced = newSynced;
        }
    }

    #endregion

    #region Вспомогательные методы

    /// <summary>
    /// Заменяет URL YouTube-превью на максимальное разрешение.
    /// <c>hqdefault.jpg</c> (480×360) → <c>maxresdefault.jpg</c> (1280×720).
    /// </summary>
    private static string? UpscaleThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.Contains("hqdefault.jpg"))
            return url.Replace("hqdefault.jpg", "maxresdefault.jpg");

        if (url.Contains("mqdefault.jpg"))
            return url.Replace("mqdefault.jpg", "maxresdefault.jpg");

        return url;
    }

    /// <summary>
    /// Форматирует количество треков с правильным склонением.
    /// Делегирует в <see cref="LocalizationService.GetPlural"/>.
    /// </summary>
    private static string FormatTrackCount(int count)
    {
        if (count == 0) return LocalizationService.Instance["Playlist_Empty"];
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
    }

    /// <summary>
    /// Показывает карточку (устанавливает <see cref="IsVisible"/> = true).
    /// CSS-transition в LibraryView.axaml обеспечивает плавное появление.
    /// </summary>
    public void Show()
    {
        if (!_isDisposed)
            IsVisible = true;
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Отписывается от события смены языка.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
        }
        base.Dispose(disposing);
    }

    #endregion
}