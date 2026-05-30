using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using LMP.Core.Services;
using Avalonia.Media;

namespace LMP.UI.Features.Library;

/// <summary>
/// ViewModel карточки плейлиста в библиотеке.
/// </summary>
public sealed class PlaylistCardViewModel : ViewModelBase
{
    #region Приватные поля

    private readonly CookieAuthService _auth;
    private readonly PlayerControlService _playerControl;
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;

    private readonly Func<string, Task>? _onDelete;
    private readonly Func<Core.Models.Playlist, Task>? _onEdit;
    private readonly Func<Core.Models.Playlist, Task> _addToQueueAction;
    private readonly Func<Core.Models.Playlist, Task> _playAction;
    private readonly Action<string> _onOpen;

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
            if (IsLikedPlaylist && _auth.IsAuthenticated)
                return "https://www.youtube.com/playlist?list=LM";

            return string.IsNullOrEmpty(Playlist.YoutubeId)
                ? null
                : $"https://www.youtube.com/playlist?list={Playlist.YoutubeId}";
        }
    }

    /// <summary>Количество треков в плейлисте.</summary>
    [Reactive] public int TrackCount { get; set; }

    /// <summary>Плейлист синхронизирован с YouTube Music (реактивное).</summary>
    [Reactive] public bool IsSynced { get; private set; }

    /// <summary>Плейлист хранится локально (включая TwoWaySync).</summary>
    public bool IsLocal => Playlist.IsLocal;

    /// <summary>Плейлист только для чтения (CloudPublic).</summary>
    public bool IsReadOnly => !Playlist.IsEditable;

    /// <summary>Можно ли удалить (всё кроме Liked).</summary>
    public bool CanDelete => Playlist.Id != LibraryService.LikedPlaylistId;

    public bool CanCopyLink => !string.IsNullOrEmpty(YoutubeUrl);

    /// <summary>Можно ли открыть диалог редактирования.</summary>
    public bool CanEdit => Playlist.IsEditable;

    /// <summary>Это плейлист «Понравившиеся» (специальный UI).</summary>
    public bool IsLikedPlaylist => Playlist.Id == LibraryService.LikedPlaylistId;

    /// <summary>Форматированное количество треков с правильным склонением.</summary>
    public string FormattedTrackCount => FormatTrackCount(TrackCount);

    /// <summary>Есть обложка для отображения (не пустая и не Liked).</summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>Показывать placeholder вместо обложки.</summary>
    public bool ShowPlaceholder => string.IsNullOrEmpty(ThumbnailUrl) && !IsLikedPlaylist;

    /// <summary>Активен ли данный плейлист в плеере прямо сейчас.</summary>
    [Reactive] public bool IsActive { get; private set; }

    /// <summary>Очередь чистая — полностью совпадает с треками этого плейлиста.</summary>
    [Reactive] public bool IsQueuePure { get; private set; }

    /// <summary>Плейлист сейчас активно играет (эквивалент IsPlayingPure в PlaylistViewModel).</summary>
    [Reactive] public bool IsPlayingPure { get; private set; }

    /// <summary>Текст для пункта контекстного меню "Воспроизвести / Пауза" с локализацией.</summary>
    public string PlayMenuHeader => IsPlayingPure
        ? (LocalizationService.Instance["Player_Pause"] ?? "Pause")
        : (LocalizationService.Instance["Player_Play"] ?? "Play");

    /// <summary>Геометрия иконки воспроизведения (Play/Pause) для контекстного меню.</summary>
    public Geometry? PlayMenuIcon
    {
        get
        {
            var key = IsPlayingPure ? "Icon.Pause" : "Icon.Play";
            if (Avalonia.Application.Current != null &&
                Avalonia.Application.Current.TryGetResource(key, null, out var res) &&
                res is Geometry geo)
            {
                return geo;
            }
            return null;
        }
    }

    /// <summary>Видимость карточки (для stagger-анимации появления).</summary>
    [Reactive] public bool IsVisible { get; set; }

    #endregion

    #region Команды

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }

    #endregion

    #region Конструктор

    public PlaylistCardViewModel(
      CookieAuthService auth,
      PlayerControlService playerControl,
      LibraryService library,
      AudioEngine audio,
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
        _playerControl = playerControl;
        _library = library;
        _audio = audio;
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

        // Реактивный трекинг состояния активности и чистоты очереди
        Observable.CombineLatest(
            _playerControl.ActivePlaylistIdObservable,
            _playerControl.PlaybackStateObservable,
            _playerControl.QueueCountObservable,
            (activeId, state, qCount) => new { activeId, state.IsPlaying, qCount })
        .Throttle(TimeSpan.FromMilliseconds(50)) // Дебаунс против спама при быстрой смене треков
        .ObserveOn(RxSchedulers.MainThreadScheduler)
        .Subscribe(async data =>
        {
            if (_isDisposed) return;

            bool isActive = data.activeId == Id;

            // Если играет другой плейлист — сбрасываем все состояния активности
            if (!isActive)
            {
                IsActive = false;
                IsQueuePure = false;
                IsPlayingPure = false;
                return;
            }

            // Быстрый отсев: если размеры не совпадают, очередь заведомо "грязная"
            if (data.qCount != TrackCount)
            {
                IsActive = false;
                IsQueuePure = false;
                IsPlayingPure = false;
                return;
            }

            // Асинхронная проверка чистоты очереди в бэкграунде
            try
            {
                var trackIds = await _library.GetPlaylistTrackIdsAsync(Id);
                var trackIdSet = new HashSet<string>(trackIds, StringComparer.Ordinal);

                var queue = _audio.Queue;
                bool isPure = queue.Count == trackIds.Count;
                if (isPure)
                {
                    for (int i = 0; i < queue.Count; i++)
                    {
                        if (!trackIdSet.Contains(queue[i].Id))
                        {
                            isPure = false;
                            break;
                        }
                    }
                }

                IsActive = isPure; // Рамка светится и пульсирует только если очередь чистая
                IsQueuePure = isPure;
                IsPlayingPure = isPure && data.IsPlaying;
            }
            catch (Exception ex)
            {
                Log.Error($"[PlaylistCard] Error checking queue purity: {ex.Message}");
                IsActive = false;
                IsQueuePure = false;
                IsPlayingPure = false;
            }
        })
        .DisposeWith(Disposables);

        // Инвалидация свойств при изменении активности
        this.WhenAnyValue(x => x.IsPlayingPure)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(PlayMenuHeader));
                this.RaisePropertyChanged(nameof(PlayMenuIcon));
            })
            .DisposeWith(Disposables);

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

        _languageChangedHandler = (_, _) =>
        {
            this.RaisePropertyChanged(nameof(FormattedTrackCount));
            this.RaisePropertyChanged(nameof(PlayMenuHeader));
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    #endregion

    #region Обновление данных

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

    private static string? UpscaleThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.Contains("hqdefault.jpg"))
            return url.Replace("hqdefault.jpg", "maxresdefault.jpg");

        if (url.Contains("mqdefault.jpg"))
            return url.Replace("mqdefault.jpg", "maxresdefault.jpg");

        return url;
    }

    private static string FormatTrackCount(int count)
    {
        if (count == 0) return LocalizationService.Instance["Playlist_Empty"];
        return LocalizationService.Instance.GetPlural("Playlist_TracksCount", count);
    }

    public void Show()
    {
        if (!_isDisposed)
            IsVisible = true;
    }

    #endregion

    #region Dispose

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