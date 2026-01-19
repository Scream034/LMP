using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

public class PlayerBarViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly IClipboardService _clipboard; // Новый сервис
    private readonly DispatcherTimer _positionTimer;

    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private float _volumeBeforeMute;

    // ... (Все свойства Reactive Properties остаются без изменений) ...
    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public double BufferedSeconds { get; private set; }
    [Reactive] public float Volume { get; set; }
    [Reactive] public int MaxVolume { get; private set; } = 100;
    [Reactive] public bool IsMuted { get; private set; }
    [Reactive] public bool ShuffleEnabled { get; set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }

    public string SafeTitle => CurrentTrack?.Title ?? "Нет трека";
    public string SafeAuthor => CurrentTrack?.Author ?? "Неизвестный исполнитель";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    // ... (Команды) ...
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    // НОВАЯ КОМАНДА
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }

    public PlayerBarViewModel(
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        IClipboardService clipboard) // Внедряем clipboard
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _clipboard = clipboard;

        MaxVolume = _library.Data.MaxVolumeLimit;
        Volume = _audio.GetVolume() * 100f;
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePositionFromEngine();
        _positionTimer.Start();

        // Подписки на AudioEngine
        _audio.OnLoadingChanged += loading => IsLoading = loading;
        _audio.OnTrackChanged += track => Dispatcher.UIThread.Post(() => HandleTrackChanged(track));
        _audio.OnPlaybackStopped += () => UpdatePlayState();

        // Подписка на изменение данных трека в библиотеке (СИНХРОНИЗАЦИЯ ЛАЙКА)
        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
            h => _library.OnTrackUpdated += h,
            h => _library.OnTrackUpdated -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(updatedTrack =>
            {
                // Если обновленный трек играет прямо сейчас, обновляем состояние лайка в UI
                if (CurrentTrack != null && CurrentTrack.Id == updatedTrack.Id)
                {
                    IsLiked = updatedTrack.IsLiked;
                    // Обновляем ссылку на сам объект трека, чтобы данные были актуальны
                    CurrentTrack.IsLiked = updatedTrack.IsLiked;
                }
            });

        // Подписка на прогресс загрузки
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

        // Громкость
        this.WhenAnyValue(x => x.Volume).Subscribe(v =>
        {
            _audio.SetVolume(v / 100f);
            IsMuted = v <= 0.01f;
        });
        this.WhenAnyValue(x => x.Volume).Throttle(TimeSpan.FromSeconds(1)).Subscribe(v =>
        {
            _library.Data.Volume = v / 100f;
            _library.Save();
        });

        Observable.FromEvent(h => _library.OnDataChanged += h, h => _library.OnDataChanged -= h)
            .Subscribe(_ =>
            {
                MaxVolume = _library.Data.MaxVolumeLimit;
                if (Volume > MaxVolume) Volume = MaxVolume;
            });

        var canExecute = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = ReactiveCommand.Create(() =>
        {
            _audio.TogglePlayPause();
            UpdatePlayState();
        }, canExecute);

        PreviousCommand = ReactiveCommand.CreateFromTask(() => _audio.PlayPreviousAsync(), canExecute);
        NextCommand = ReactiveCommand.CreateFromTask(() => _audio.PlayNextAsync(), canExecute);

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
                // LibraryService теперь сам вызовет OnTrackUpdated, 
                // на который мы подписаны выше, поэтому IsLiked обновится автоматически
                _library.ToggleLike(CurrentTrack);
            }
        }, canExecute);

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            if (IsMuted) Volume = _volumeBeforeMute > 5f ? _volumeBeforeMute : 50f;
            else { _volumeBeforeMute = Volume; Volume = 0; }
        });

        // РЕАЛИЗАЦИЯ КОПИРОВАНИЯ ССЫЛКИ
        CopyLinkCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack != null && !string.IsNullOrEmpty(CurrentTrack.Url))
            {
                await _clipboard.SetTextAsync(CurrentTrack.Url);
            }
        }, canExecute);
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

            if (track.IsDownloaded) BufferedSeconds = DurationSeconds;
            else if (_downloads.IsDownloading(track.Id)) BufferedSeconds = DurationSeconds * _downloads.GetProgress(track.Id);
            else BufferedSeconds = 0;
        }
        else
        {
            DurationSeconds = 1;
            PositionSeconds = 0;
            BufferedSeconds = 0;
        }
        UpdatePlayState();
    }

    // ... (Методы UpdatePositionFromEngine, UpdatePlayState, Seek Logic без изменений) ...
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

    public void StartSeek() { _isSeeking = true; _justFinishedSeeking = false; }

    public async void EndSeek()
    {
        if (HasTrack)
        {
            _audio.Seek(TimeSpan.FromSeconds(PositionSeconds));
            Position = TimeSpan.FromSeconds(PositionSeconds);
            _isSeeking = false;
            _justFinishedSeeking = true;
            await Task.Delay(500);
            _justFinishedSeeking = false;
        }
        else { _isSeeking = false; }
    }

    public void Dispose() => _positionTimer.Stop();
}