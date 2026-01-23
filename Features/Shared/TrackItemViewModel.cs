// ============================================================================
// Файл: Features/Shared/TrackItemViewModel.cs
// Описание: ViewModel для отображения трека.
// Исправления:
//   - [FIX] Использование паттерна "Explicit Delegate Fields" для подписки на события.
//     Это предотвращает создание новых экземпляров делегатов при отписке
//     и гарантирует корректное удаление ссылок в сервисах.
//   - [FIX] Полная реализация IDisposable.
// ============================================================================

using System.Reactive;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Features.Shared;

/// <summary>
/// ViewModel, представляющая один музыкальный трек в списке.
/// Отвечает за отображение состояния (играет, пауза, лайк) и команд.
/// </summary>
public sealed class TrackItemViewModel : ViewModelBase, IDisposable
{
    #region Fields

    // Сервисы
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly MusicLibraryManager _manager;

    // Внешние действия
    private Action<TrackInfo>? _onPlay;
    
    private bool _isDisposed;

    // [FIX] Явное хранение делегатов для гарантированной отписки.
    // Если использовать лямбды напрямую (audio.Event += (s,e) => ...),
    // то при отписке (audio.Event -= (s,e) => ...) создается НОВЫЙ делегат,
    // и старый остается в памяти сервиса, удерживая весь ViewModel.
    private readonly Action<TrackInfo?> _onTrackChangedHandler;
    private readonly Action<bool, bool> _onPlaybackStateHandler;
    private readonly Action<TrackInfo> _onTrackUpdatedHandler;
    private readonly Action<string, float> _onDownloadProgressHandler;
    private readonly Action<string, bool, string?> _onDownloadCompletedHandler;

    #endregion

    #region Properties

    /// <summary>
    /// Данные трека.
    /// </summary>
    public TrackInfo Track { get; }

    /// <summary>
    /// ID трека.
    /// </summary>
    public string Id => Track.Id;

    /// <summary>
    /// Название трека.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Исполнитель.
    /// </summary>
    public string Author { get; }

    /// <summary>
    /// Длительность.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// URL обложки.
    /// </summary>
    public string ThumbnailUrl { get; }

    /// <summary>
    /// Является ли этот трек текущим в AudioEngine.
    /// </summary>
    [Reactive] public bool IsActive { get; private set; }

    /// <summary>
    /// Проигрывается ли этот трек прямо сейчас.
    /// </summary>
    [Reactive] public bool IsPlaying { get; private set; }

    /// <summary>
    /// Лайкнут ли трек.
    /// </summary>
    [Reactive] public bool IsLiked { get; set; }

    /// <summary>
    /// Скачан ли трек.
    /// </summary>
    [Reactive] public bool IsDownloaded { get; set; }

    /// <summary>
    /// Идет ли загрузка.
    /// </summary>
    [Reactive] public bool IsDownloading { get; set; }

    /// <summary>
    /// Прогресс загрузки (0..1).
    /// </summary>
    [Reactive] public float DownloadProgress { get; set; }

    /// <summary>
    /// Является ли контекстом очереди (скрывает кнопку "Add to queue").
    /// </summary>
    [Reactive] public bool IsQueueContext { get; set; }

    /// <summary>
    /// Видимость кнопки добавления в очередь.
    /// </summary>
    public bool ShowAddToQueue => !IsQueueContext;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddToQueueCommand { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="TrackItemViewModel"/>.
    /// </summary>
    public TrackItemViewModel(
        TrackInfo track,
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        MusicLibraryManager manager,
        Action<TrackInfo>? onPlay = null)
    {
        Track = track;
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _manager = manager;
        _onPlay = onPlay;

        // Копируем данные для отображения (immutable поля)
        Title = track.Title;
        Author = track.Author;
        Duration = track.Duration;
        ThumbnailUrl = track.ThumbnailUrl;

        // [FIX] Инициализация делегатов.
        // Ссылаемся на методы экземпляра. 
        // Это безопасно, так как мы явно отпишемся в Dispose.
        _onTrackChangedHandler = OnAudioTrackChanged;
        _onPlaybackStateHandler = OnAudioPlaybackStateChanged;
        _onTrackUpdatedHandler = OnLibraryTrackUpdated;
        _onDownloadProgressHandler = OnDownloadProgress;
        _onDownloadCompletedHandler = OnDownloadCompleted;

        // [FIX] Подписка.
        _audio.OnTrackChanged += _onTrackChangedHandler;
        _audio.OnPlaybackStateChanged += _onPlaybackStateHandler;
        _library.OnTrackUpdated += _onTrackUpdatedHandler;
        _downloads.OnProgress += _onDownloadProgressHandler;
        _downloads.OnCompleted += _onDownloadCompletedHandler;

        // Начальное состояние
        IsDownloading = _downloads.IsDownloading(track.Id);
        if (IsDownloading) 
            DownloadProgress = _downloads.GetProgress(track.Id);

        IsDownloaded = track.IsDownloaded;
        IsLiked = track.IsLiked;

        UpdateActiveState(_audio.CurrentTrack, _audio.IsPlaying);

        // Команды
        PlayCommand = ReactiveCommand.CreateFromTask(ExecutePlayAsync);
        ToggleLikeCommand = ReactiveCommand.CreateFromTask(ExecuteToggleLikeAsync);
        AddToQueueCommand = ReactiveCommand.Create(ExecuteAddToQueue);

        this.WhenAnyValue(x => x.IsQueueContext)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowAddToQueue)));
    }

    #endregion

    #region Event Handlers

    private void OnAudioTrackChanged(TrackInfo? track)
    {
        UpdateActiveState(track, _audio.IsPlaying);
    }

    private void OnAudioPlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        UpdateActiveState(_audio.CurrentTrack, isPlaying);
    }

    private void OnLibraryTrackUpdated(TrackInfo updatedTrack)
    {
        // Проверяем ID, так как событие глобальное
        if (updatedTrack.Id == Id)
        {
            IsLiked = updatedTrack.IsLiked;
            IsDownloaded = updatedTrack.IsDownloaded;
            this.RaisePropertyChanged(nameof(IsDownloaded));
        }
    }

    private void OnDownloadProgress(string trackId, float progress)
    {
        if (trackId == Id)
        {
            IsDownloading = true;
            DownloadProgress = progress;
        }
    }

    private void OnDownloadCompleted(string trackId, bool success, string? path)
    {
        if (trackId == Id)
        {
            IsDownloading = false;
            DownloadProgress = 0;
            if (success)
            {
                IsDownloaded = true;
                this.RaisePropertyChanged(nameof(IsDownloaded));
            }
        }
    }

    #endregion

    #region Methods

    private void UpdateActiveState(TrackInfo? currentTrack, bool isPlaying)
    {
        bool isMe = currentTrack?.Id == Id;
        IsActive = isMe;
        IsPlaying = isMe && isPlaying;
    }

    private async Task ExecutePlayAsync()
    {
        if (_audio.CurrentTrack?.Id == Id)
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        else
        {
            if (_onPlay != null)
                _onPlay(Track);
            else
                await _audio.PlayTrackAsync(Track);
        }
    }

    private async Task ExecuteToggleLikeAsync()
    {
        await _manager.ToggleLikeAsync(Track);
    }

    private void ExecuteAddToQueue()
    {
        _audio.Enqueue(Track);
    }

    /// <summary>
    /// Обновляет действие воспроизведения при переиспользовании VM.
    /// </summary>
    public void UpdatePlayAction(Action<TrackInfo>? onPlay)
    {
        _onPlay = onPlay;
    }

    /// <summary>
    /// Принудительно задает состояние активности (используется в очереди).
    /// </summary>
    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        IsPlaying = isActive && _audio.IsPlaying;
    }

    /// <summary>
    /// Метод для совместимости, вызывает Dispose.
    /// </summary>
    public void Cleanup()
    {
        Dispose();
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // [FIX] ГАРАНТИРОВАННАЯ ОТПИСКА.
        // Используем те же экземпляры делегатов, что и при подписке.
        _audio.OnTrackChanged -= _onTrackChangedHandler;
        _audio.OnPlaybackStateChanged -= _onPlaybackStateHandler;
        _library.OnTrackUpdated -= _onTrackUpdatedHandler;
        _downloads.OnProgress -= _onDownloadProgressHandler;
        _downloads.OnCompleted -= _onDownloadCompletedHandler;
        
        // Помогаем GC
        GC.SuppressFinalize(this);
    }

    #endregion
}