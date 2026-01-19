using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;

namespace MyLiteMusicPlayer.ViewModels;

public class PlayerBarViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads; // Для статуса буферизации
    private readonly DispatcherTimer _positionTimer;

    // Флаг, который показывает, что пользователь сейчас тащит ползунок
    private bool _isSeeking;

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }

    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }

    // Новое свойство для отображения прогресса буферизации/скачивания (от 0 до 1)
    [Reactive] public double BufferedProgress { get; private set; }
    // Длина буфера в секундах для привязки к ширине слайдера
    [Reactive] public double BufferedSeconds { get; private set; }

    [Reactive] public float Volume { get; set; }
    [Reactive] public bool IsMuted { get; private set; }
    [Reactive] public bool ShuffleEnabled { get; set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }

    public string SafeTitle => CurrentTrack?.Title ?? "";
    public string SafeAuthor => CurrentTrack?.Author ?? "";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }

    private float _volumeBeforeMute;

    public PlayerBarViewModel(AudioEngine audio, LibraryService library, DownloadService downloads)
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;

        Volume = _audio.GetVolume();
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
        _positionTimer.Start();

        // Подписки на AudioEngine
        Observable.FromEvent<bool>(h => _audio.OnLoadingChanged += h, h => _audio.OnLoadingChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler).Subscribe(loading => IsLoading = loading);

        Observable.FromEvent<TrackInfo>(h => _audio.OnTrackChanged += h, h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler).Subscribe(OnTrackChanged);

        Observable.FromEvent(h => _audio.OnPlaybackStopped += h, h => _audio.OnPlaybackStopped -= h)
            .ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ => UpdatePlayState());

        Observable.FromEvent<TimeSpan>(h => _audio.OnPositionChanged += h, h => _audio.OnPositionChanged -= h)
             .ObserveOn(RxApp.MainThreadScheduler)
             .Subscribe(pos =>
             {
                 // Обновляем VM, только если не перетаскиваем ползунок
                 if (!_isSeeking)
                 {
                     Position = pos;
                     PositionSeconds = pos.TotalSeconds;
                 }
             });

        // Подписка на скачивание (для визуализации буфера)
        Observable.FromEvent<Action<string, float>, (string, float)>(
            h => (id, p) => h((id, p)),
            h => _downloads.OnProgress += h,
            h => _downloads.OnProgress -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (CurrentTrack != null && x.Item1 == CurrentTrack.Id)
                {
                    BufferedProgress = x.Item2;
                    BufferedSeconds = DurationSeconds * x.Item2;
                }
            });

        // Volume logic
        this.WhenAnyValue(x => x.Volume).Throttle(TimeSpan.FromMilliseconds(50)).Subscribe(v =>
        {
            _audio.SetVolume(v);
            IsMuted = v == 0;
        });

        var hasTrack = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = ReactiveCommand.Create(() => { _audio.TogglePlayPause(); UpdatePlayState(); }, hasTrack);
        PreviousCommand = ReactiveCommand.Create(() => _audio.PlayPrevious(), hasTrack);
        NextCommand = ReactiveCommand.Create(() => _audio.PlayNext(), hasTrack);
        ToggleShuffleCommand = ReactiveCommand.Create(() => { ShuffleEnabled = !ShuffleEnabled; _audio.ShuffleEnabled = ShuffleEnabled; });
        ToggleRepeatCommand = ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch { RepeatMode.None => RepeatMode.RepeatAll, RepeatMode.RepeatAll => RepeatMode.RepeatOne, _ => RepeatMode.None };
            _audio.RepeatMode = RepeatMode;
        });
        ToggleLikeCommand = ReactiveCommand.Create(() => { if (CurrentTrack != null) { _library.ToggleLike(CurrentTrack); IsLiked = CurrentTrack.IsLiked; } }, hasTrack);
        ToggleMuteCommand = ReactiveCommand.Create(() => { if (IsMuted) Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.5f; else { _volumeBeforeMute = Volume; Volume = 0; } });
    }

    private void OnTrackChanged(TrackInfo? track)
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

            // Если трек уже скачан, буфер полный
            if (track.IsDownloaded)
            {
                BufferedProgress = 1.0;
                BufferedSeconds = DurationSeconds;
            }
            else if (_downloads.IsDownloading(track.Id))
            {
                BufferedProgress = _downloads.GetProgress(track.Id);
                BufferedSeconds = DurationSeconds * BufferedProgress;
            }
            else
            {
                BufferedProgress = 0;
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
        if (HasTrack && !_isSeeking)
        {
            Position = _audio.CurrentPosition;
            PositionSeconds = Position.TotalSeconds;

            // Уточнение длительности, если стрим подгрузил метаданные
            if (Math.Abs(DurationSeconds - _audio.TotalDuration.TotalSeconds) > 1 && _audio.TotalDuration.TotalSeconds > 0)
            {
                Duration = _audio.TotalDuration;
                DurationSeconds = Duration.TotalSeconds;
                // Обновляем буфер бар при изменении длины
                if (BufferedProgress > 0) BufferedSeconds = DurationSeconds * BufferedProgress;
            }
        }
        UpdatePlayState();
    }

    private void UpdatePlayState()
    {
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
    }

    // Вызывается при нажатии (Start Drag)
    public void StartSeek()
    {
        _isSeeking = true;
    }

    // Вызывается при отпускании (End Drag / Click)
    public void EndSeek()
    {
        if (HasTrack)
        {
            _audio.Seek(TimeSpan.FromSeconds(PositionSeconds));
        }
        _isSeeking = false;
    }

    public void Dispose() => _positionTimer.Stop();
}