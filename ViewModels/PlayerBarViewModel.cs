using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

/// <summary>
/// ViewModel для панели управления плеером.
/// Обеспечивает связь между UI и AudioEngine, обрабатывает громкость (до 400%) и перемотку.
/// </summary>
public class PlayerBarViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly DispatcherTimer _positionTimer;

    // Состояние для управления перемоткой (защита от "прыгающего" слайдера)
    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private float _volumeBeforeMute;

    #region Observable Properties (Состояние UI)

    /// <summary> Текущий воспроизводимый трек </summary>
    [Reactive] public TrackInfo? CurrentTrack { get; private set; }

    /// <summary> Флаг активной загрузки/буферизации </summary>
    [Reactive] public bool IsLoading { get; private set; }

    /// <summary> Флаг воспроизведения </summary>
    [Reactive] public bool IsPlaying { get; private set; }

    /// <summary> Флаг паузы </summary>
    [Reactive] public bool IsPaused { get; private set; }

    // --- Тайминги ---

    /// <summary> Текущая позиция в формате TimeSpan </summary>
    [Reactive] public TimeSpan Position { get; set; }

    /// <summary> Общая длительность трека </summary>
    [Reactive] public TimeSpan Duration { get; private set; }

    /// <summary> Текущая позиция в секундах (для Slider) </summary>
    [Reactive] public double PositionSeconds { get; set; }

    /// <summary> Длительность в секундах (для Slider) </summary>
    [Reactive] public double DurationSeconds { get; private set; }

    /// <summary> Прогресс буферизации в секундах </summary>
    [Reactive] public double BufferedSeconds { get; private set; }

    // --- Громкость и Настройки ---

    /// <summary> 
    /// Текущее значение громкости в UI (от 0 до MaxVolume).
    /// </summary>
    [Reactive] public float Volume { get; set; }

    /// <summary> 
    /// Максимально допустимый предел громкости (100, 200, 300 или 400).
    /// </summary>
    [Reactive] public int MaxVolume { get; private set; } = 100;

    /// <summary> Флаг выключенного звука </summary>
    [Reactive] public bool IsMuted { get; private set; }

    /// <summary> Режим перемешивания </summary>
    [Reactive] public bool ShuffleEnabled { get; set; }

    /// <summary> Режим повтора </summary>
    [Reactive] public RepeatMode RepeatMode { get; set; }

    /// <summary> Флаг наличия трека в "Любимом" </summary>
    [Reactive] public bool IsLiked { get; private set; }

    /// <summary> Флаг доступности управления (есть ли загруженный трек) </summary>
    [Reactive] public bool HasTrack { get; private set; }

    // --- Безопасные свойства для биндинга ---
    public string SafeTitle => CurrentTrack?.Title ?? "Нет трека";
    public string SafeAuthor => CurrentTrack?.Author ?? "Неизвестный исполнитель";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    #endregion

    #region Commands (Команды управления)

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

        // 1. Инициализация состояния
        MaxVolume = _library.Data.MaxVolumeLimit;
        Volume = _audio.GetVolume() * 100f;
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        // 2. Таймер обновления позиции
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePositionFromEngine();
        _positionTimer.Start();

        // 3. Подписки на события AudioEngine
        _audio.OnLoadingChanged += loading => IsLoading = loading;

        _audio.OnTrackChanged += track =>
        {
            Dispatcher.UIThread.Post(() => HandleTrackChanged(track));
        };

        _audio.OnPlaybackStopped += () => UpdatePlayState();

        // 4. Подписка на прогресс загрузки (Буфер)
        Observable.FromEvent<Action<string, float>, (string, float)>(
            h => (id, p) => h((id, p)),
            h => _downloads.OnProgress += h,
            h => _downloads.OnProgress -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (CurrentTrack != null && x.Item1 == CurrentTrack.Id)
                    BufferedSeconds = DurationSeconds * x.Item2;
            });

        // 5. Реактивное управление громкостью
        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                _audio.SetVolume(v / 100f);
                IsMuted = v <= 0.01f;
            });

        this.WhenAnyValue(x => x.Volume)
            .Throttle(TimeSpan.FromSeconds(1))
            .Subscribe(v =>
            {
                _library.Data.Volume = v / 100f;
                _library.Save();
            });

        // Слежение за изменением лимитов в настройках
        Observable.FromEvent(h => _library.OnDataChanged += h, h => _library.OnDataChanged -= h)
            .Subscribe(_ =>
            {
                MaxVolume = _library.Data.MaxVolumeLimit;
                if (Volume > MaxVolume) Volume = MaxVolume;
            });

        // 6. Инициализация команд
        var canExecute = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = ReactiveCommand.Create(() =>
        {
            _audio.TogglePlayPause();
            UpdatePlayState();
        }, canExecute);

        PreviousCommand = ReactiveCommand.CreateFromTask(
            () => _audio.PlayPreviousAsync(),
            canExecute);

        NextCommand = ReactiveCommand.CreateFromTask(
            () => _audio.PlayNextAsync(),
            canExecute);

        ToggleShuffleCommand = ReactiveCommand.Create(() =>
        {
            ShuffleEnabled = !ShuffleEnabled;
            _audio.ShuffleEnabled = ShuffleEnabled;
            _library.Data.ShuffleEnabled = ShuffleEnabled;
            _library.Save();
        });

        ToggleRepeatCommand = ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.None => RepeatMode.RepeatAll,
                RepeatMode.RepeatAll => RepeatMode.RepeatOne,
                _ => RepeatMode.None
            };
            _audio.RepeatMode = RepeatMode;
            _library.Data.RepeatMode = RepeatMode;
            _library.Save();
        });

        ToggleLikeCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentTrack != null)
            {
                _library.ToggleLike(CurrentTrack);
                IsLiked = CurrentTrack.IsLiked;
            }
        }, canExecute);

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            if (IsMuted)
            {
                Volume = _volumeBeforeMute > 5f ? _volumeBeforeMute : 50f;
            }
            else
            {
                _volumeBeforeMute = Volume;
                Volume = 0;
            }
        });
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        CurrentTrack = track;
        HasTrack = track != null;

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

            if (track.IsDownloaded)
                BufferedSeconds = DurationSeconds;
            else if (_downloads.IsDownloading(track.Id))
                BufferedSeconds = DurationSeconds * _downloads.GetProgress(track.Id);
            else
                BufferedSeconds = 0;
        }
        else
        {
            DurationSeconds = 1;
            PositionSeconds = 0;
            BufferedSeconds = 0;
        }

        UpdatePlayState();
    }

    private void UpdatePositionFromEngine()
    {
        if (!HasTrack || _isSeeking || _justFinishedSeeking) return;

        var currentPos = _audio.CurrentPosition;
        Position = currentPos;
        PositionSeconds = currentPos.TotalSeconds;

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

    #region Seek Logic

    public void StartSeek()
    {
        _isSeeking = true;
        _justFinishedSeeking = false;
    }

    public async void EndSeek()
    {
        if (HasTrack)
        {
            _audio.Seek(TimeSpan.FromSeconds(PositionSeconds));
            Position = TimeSpan.FromSeconds(PositionSeconds);

            _isSeeking = false;
            _justFinishedSeeking = true;

            // Задержка для стабилизации потока после перемотки
            await Task.Delay(500);

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