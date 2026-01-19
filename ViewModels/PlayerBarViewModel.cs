using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.ViewModels;

public class PlayerBarViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly DispatcherTimer _positionTimer;

    // Состояние перемотки
    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private float _volumeBeforeMute;

    #region Observable Properties

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }

    // Тайминги
    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public double BufferedSeconds { get; private set; }

    // Громкость и настройки
    [Reactive] public float Volume { get; set; }
    [Reactive] public bool IsMuted { get; private set; }
    [Reactive] public bool ShuffleEnabled { get; set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }

    // Быстрый доступ к данным трека
    public string SafeTitle => CurrentTrack?.Title ?? "No Track";
    public string SafeAuthor => CurrentTrack?.Author ?? "Unknown Artist";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }

    #endregion

    public PlayerBarViewModel(AudioEngine audio, LibraryService library, DownloadService downloads)
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;

        // Начальное состояние
        Volume = _audio.GetVolume();
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        // 1. Таймер обновления позиции (опрос раз в 250мс для плавности)
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _positionTimer.Start();

        // 2. Подписки на события AudioEngine
        _audio.OnLoadingChanged += loading => IsLoading = loading;
        
        _audio.OnTrackChanged += track => {
            Dispatcher.UIThread.Post(() => OnTrackChanged(track));
        };

        _audio.OnPlaybackStopped += () => UpdatePlayState();

        // Событие изменения позиции напрямую из движка
        _audio.OnPositionChanged += pos => 
        {
            // Обновляем VM только если пользователь не перематывает сейчас
            if (!_isSeeking && !_justFinishedSeeking)
            {
                Dispatcher.UIThread.Post(() => {
                    Position = pos;
                    PositionSeconds = pos.TotalSeconds;
                });
            }
        };

        // 3. Подписка на прогресс загрузки (для визуализации буфера)
        Observable.FromEvent<Action<string, float>, (string, float)>(
            h => (id, p) => h((id, p)),
            h => _downloads.OnProgress += h,
            h => _downloads.OnProgress -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (CurrentTrack != null && x.Item1 == CurrentTrack.Id)
                {
                    BufferedSeconds = DurationSeconds * x.Item2;
                }
            });

        // 4. Логика громкости
        this.WhenAnyValue(x => x.Volume)
            .Throttle(TimeSpan.FromMilliseconds(50))
            .Subscribe(v =>
            {
                _audio.SetVolume(v);
                IsMuted = v <= 0.01f;
            });

        // 5. Инициализация команд
        var canExecute = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = ReactiveCommand.Create(() => { 
            _audio.TogglePlayPause(); 
            UpdatePlayState(); 
        }, canExecute);

        PreviousCommand = ReactiveCommand.Create(() => _audio.PlayPrevious(), canExecute);
        NextCommand = ReactiveCommand.Create(() => _audio.PlayNext(), canExecute);

        ToggleShuffleCommand = ReactiveCommand.Create(() => { 
            ShuffleEnabled = !ShuffleEnabled; 
            _audio.ShuffleEnabled = ShuffleEnabled; 
        });

        ToggleRepeatCommand = ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch { 
                RepeatMode.None => RepeatMode.RepeatAll, 
                RepeatMode.RepeatAll => RepeatMode.RepeatOne, 
                _ => RepeatMode.None 
            };
            _audio.RepeatMode = RepeatMode;
        });

        ToggleLikeCommand = ReactiveCommand.Create(() => { 
            if (CurrentTrack != null) { 
                _library.ToggleLike(CurrentTrack); 
                IsLiked = CurrentTrack.IsLiked; 
            } 
        }, canExecute);

        ToggleMuteCommand = ReactiveCommand.Create(() => { 
            if (IsMuted) {
                Volume = _volumeBeforeMute > 0.05f ? _volumeBeforeMute : 0.5f; 
            } else { 
                _volumeBeforeMute = Volume; 
                Volume = 0; 
            } 
        });
    }

    private void OnTrackChanged(TrackInfo? track)
    {
        CurrentTrack = track;
        HasTrack = track != null;

        // Уведомляем об изменении вычисляемых свойств
        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(SafeAuthor));
        this.RaisePropertyChanged(nameof(SafeThumbnail));

        if (track != null)
        {
            Duration = track.Duration;
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;
            IsLiked = track.IsLiked;
            Position = TimeSpan.Zero;
            PositionSeconds = 0;

            // Визуализация буфера
            if (track.IsDownloaded) {
                BufferedSeconds = DurationSeconds;
            } else if (_downloads.IsDownloading(track.Id)) {
                BufferedSeconds = DurationSeconds * _downloads.GetProgress(track.Id);
            } else {
                BufferedSeconds = 0;
            }
        }
        else
        {
            DurationSeconds = 1;
            PositionSeconds = 0;
            BufferedSeconds = 0;
        }

        UpdatePlayState();
    }

    private void UpdatePosition()
    {
        // Если мы в процессе перемотки, не берем данные из движка (чтобы избежать дерганья)
        if (!HasTrack || _isSeeking || _justFinishedSeeking) return;

        var currentPos = _audio.CurrentPosition;
        Position = currentPos;
        PositionSeconds = currentPos.TotalSeconds;

        // Если движок обновил общую длительность (актуально для потоков)
        if (_audio.TotalDuration.TotalSeconds > 0 && Math.Abs(DurationSeconds - _audio.TotalDuration.TotalSeconds) > 1)
        {
            Duration = _audio.TotalDuration;
            DurationSeconds = Duration.TotalSeconds;
        }

        UpdatePlayState();
    }

    private void UpdatePlayState()
    {
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
    }

    #region Seek Logic (защита от прыжков)

    /// <summary>
    /// Вызывается из View при начале перетаскивания (PointerPressed)
    /// </summary>
    public void StartSeek()
    {
        _isSeeking = true;
        _justFinishedSeeking = false;
    }

    /// <summary>
    /// Вызывается из View при завершении перетаскивания (PointerReleased)
    /// </summary>
    public async void EndSeek()
    {
        if (HasTrack)
        {
            // 1. Применяем позицию в аудио-движке
            _audio.Seek(TimeSpan.FromSeconds(PositionSeconds));
            
            // 2. Обновляем текстовое время немедленно
            Position = TimeSpan.FromSeconds(PositionSeconds);

            // 3. Активируем временную блокировку обновлений от движка
            _isSeeking = false;
            _justFinishedSeeking = true;

            // 4. Ждем 400мс, пока буфер и состояние NAudio стабилизируются
            await Task.Delay(400);
            
            _justFinishedSeeking = false;
        }
        else
        {
            _isSeeking = false;
        }
    }

    #endregion

    public void Dispose()
    {
        _positionTimer.Stop();
    }
}