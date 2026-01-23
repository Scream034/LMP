using System.Reactive;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;

namespace MyLiteMusicPlayer.Features.Library;

/// <summary>
/// ViewModel для карточки плейлиста в сетке библиотеки.
/// Управляет отображением информации о плейлисте и командами.
/// </summary>
public sealed class PlaylistCardViewModel : ViewModelBase, IDisposable
{
    #region Fields

    private readonly Action<Core.Models.Playlist> _addToQueueAction;
    private readonly Func<string, Task>? _onDelete;
    
    // [FIX] Явный делегат для отписки
    private readonly EventHandler<string> _languageChangedHandler;
    
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>Данные плейлиста.</summary>
    public Core.Models.Playlist Playlist { get; }
    
    /// <summary>ID плейлиста.</summary>
    public string Id => Playlist.Id;
    
    /// <summary>Название плейлиста.</summary>
    public string Name => Playlist.Name;
    
    /// <summary>URL обложки.</summary>
    public string? ThumbnailUrl => Playlist.ThumbnailUrl;
    
    /// <summary>Количество треков.</summary>
    public int TrackCount => Playlist.TrackCount;

    /// <summary>Плейлист сохранен локально.</summary>
    public bool IsLocal => Playlist.IsLocal;
    
    /// <summary>Плейлист синхронизирован.</summary>
    public bool IsSynced => Playlist.IsFromAccount;
    
    /// <summary>Плейлист только для чтения.</summary>
    public bool IsReadOnly => !Playlist.IsEditable;

    /// <summary>Можно ли удалить плейлист (нельзя удалить "Любимое").</summary>
    public bool CanDelete => Playlist.Id != "liked";

    /// <summary>Форматированное количество треков (локализованное).</summary>
    public string FormattedTrackCount => LocalizationService.Instance.GetPlural("Playlist_TracksCount", TrackCount);

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PlaylistCardViewModel"/>.
    /// </summary>
    public PlaylistCardViewModel(
        Core.Models.Playlist playlist,
        Action<string> onOpen,
        Action<Core.Models.Playlist> addToQueueAction,
        Func<string, Task>? onDelete = null)
    {
        Playlist = playlist;
        _onDelete = onDelete;
        _addToQueueAction = addToQueueAction;

        OpenCommand = ReactiveCommand.Create(() => onOpen(playlist.Id));

        var canDelete = this.WhenAnyValue(x => x.CanDelete);
        DeleteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_onDelete != null)
            {
                await _onDelete(Playlist.Id);
            }
        }, canDelete);

        AddToQueueCommand = ReactiveCommand.Create(() => _addToQueueAction(Playlist));

        this.WhenAnyValue(x => x.TrackCount)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(FormattedTrackCount)));

        // [FIX] Сохраняем обработчик и подписываемся
        _languageChangedHandler = (_, _) => this.RaisePropertyChanged(nameof(FormattedTrackCount));
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Освобождает ресурсы и отписывается от событий.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // [FIX] Отписка от статического события
        LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;

        GC.SuppressFinalize(this);
    }

    #endregion
}