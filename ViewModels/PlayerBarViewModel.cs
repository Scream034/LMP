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
    private readonly IClipboardService _clipboard;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _speedUpdateTimer;

    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private float _volumeBeforeMute;
    private DateTime _lastSeekTime = DateTime.MinValue;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;

    // Минимальный интервал между seek (мс)
    private const int SeekCooldownMs = 250;

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

    // === SEEK ===
    [Reactive] public bool IsSeekBusy { get; private set; }

    // === ИНФОРМАЦИЯ О ПОТОКЕ ===
    [Reactive] public string StreamInfo { get; private set; } = "";
    [Reactive] public bool ShowStreamInfo { get; private set; }
    [Reactive] public string DownloadSpeedText { get; private set; } = "";

    public string SafeTitle => CurrentTrack?.Title ?? "Нет трека";
    public string SafeAuthor => CurrentTrack?.Author ?? "Неизвестный исполнитель";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }

    public PlayerBarViewModel(
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        IClipboardService clipboard)
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _clipboard = clipboard;

        MaxVolume = _library.Data.MaxVolumeLimit;
        Volume = _audio.GetVolume();
        _volumeBeforeMute = Volume > 5 ? Volume : 50;

        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        // Position timer
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePositionFromEngine();
        _positionTimer.Start();

        // Speed update timer
        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        // Engine events
        _audio.OnLoadingChanged += loading =>
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = loading;
                IsSeekBusy = loading;
            });

        _audio.OnTrackChanged += track =>
            Dispatcher.UIThread.Post(() => HandleTrackChanged(track));

        _audio.OnPlaybackStopped += () =>
            Dispatcher.UIThread.Post(UpdatePlayState);

        // Like sync
        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
            h => _library.OnTrackUpdated += h,
            h => _library.OnTrackUpdated -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(updatedTrack =>
            {
                if (CurrentTrack != null && CurrentTrack.Id == updatedTrack.Id)
                {
                    IsLiked = updatedTrack.IsLiked;
                    CurrentTrack.IsLiked = updatedTrack.IsLiked;
                }
            });

        // Download progress
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

        // Volume
        this.WhenAnyValue(x => x.Volume)
            .Throttle(TimeSpan.FromMilliseconds(30))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(v =>
            {
                _audio.SetVolume(v);
                IsMuted = v < 1;
            });

        // Data changed
        Observable.FromEvent(
            h => _library.OnDataChanged += h,
            h => _library.OnDataChanged -= h)
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

        PreviousCommand = ReactiveCommand.CreateFromTask(
            () => _audio.PlayPreviousAsync(), canExecute);

        NextCommand = ReactiveCommand.CreateFromTask(
            () => _audio.PlayNextAsync(), canExecute);

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
            }
        }, canExecute);

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            if (IsMuted || Volume < 1)
            {
                Volume = _volumeBeforeMute > 5 ? _volumeBeforeMute : 50;
            }
            else
            {
                _volumeBeforeMute = Volume;
                Volume = 0;
            }
        });

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

        IsSeekBusy = true;
        _lastDownloadedBytes = 0;

        if (track != null)
        {
            Duration = track.Duration;
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;
            IsLiked = track.IsLiked;
            Position = TimeSpan.Zero;
            PositionSeconds = 0;

            BufferedSeconds = track.IsDownloaded ? DurationSeconds : 0;
            UpdateStreamInfo();
        }
        else
        {
            DurationSeconds = 1;
            PositionSeconds = 0;
            BufferedSeconds = 0;
            StreamInfo = "";
            ShowStreamInfo = false;
        }

        UpdatePlayState();
    }

    private void UpdateStreamInfo()
    {
        if (CurrentTrack == null)
        {
            StreamInfo = "";
            ShowStreamInfo = false;
            return;
        }

        var info = _audio.GetCurrentStreamInfo();

        if (!string.IsNullOrEmpty(info.Format) || info.Bitrate > 0)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(info.Format))
                parts.Add(info.Format.ToUpperInvariant());
            if (info.Bitrate > 0)
                parts.Add($"{info.Bitrate}kbps");
            StreamInfo = string.Join(" • ", parts);
        }
        else
        {
            StreamInfo = CurrentTrack.IsDownloaded ? "Downloaded" : "Streaming";
        }
        ShowStreamInfo = true;
    }

    private void UpdateDownloadSpeed()
    {
        if (!HasTrack || CurrentTrack?.IsDownloaded == true)
        {
            DownloadSpeedText = "";
            return;
        }

        var currentBytes = _audio.GetDownloadedBytes();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedCheck).TotalSeconds;

        if (elapsed >= 0.5 && _lastSpeedCheck != DateTime.MinValue)
        {
            var bytesPerSecond = (currentBytes - _lastDownloadedBytes) / elapsed;
            var kbs = bytesPerSecond / 1024.0;

            if (kbs > 10)
            {
                DownloadSpeedText = kbs >= 1024
                    ? $"{kbs / 1024:F1} MB/s"
                    : $"{kbs:F0} KB/s";
            }
            else
            {
                DownloadSpeedText = "";
            }
        }

        _lastDownloadedBytes = currentBytes;
        _lastSpeedCheck = now;
    }

    private void UpdatePositionFromEngine()
    {
        if (!HasTrack || _isSeeking || _justFinishedSeeking) return;

        var currentPos = _audio.CurrentPosition;
        Position = currentPos;
        PositionSeconds = currentPos.TotalSeconds;

        // Снимаем busy когда начал играть
        if (IsSeekBusy && _audio.IsPlaying)
        {
            IsSeekBusy = false;
        }

        var engineDuration = _audio.TotalDuration;
        if (engineDuration.TotalSeconds > 0 &&
            Math.Abs(DurationSeconds - engineDuration.TotalSeconds) > 1)
        {
            Duration = engineDuration;
            DurationSeconds = Duration.TotalSeconds;
        }

        var bufferProgress = _audio.BufferProgress;
        if (bufferProgress > 0)
        {
            BufferedSeconds = DurationSeconds * (bufferProgress / 100.0);
        }

        UpdatePlayState();
    }

    private void UpdatePlayState()
    {
        IsPlaying = _audio.IsPlaying;
        IsPaused = _audio.IsPaused;
    }

    #region === SEEK ===

    public void StartSeek()
    {
        _isSeeking = true;
        _justFinishedSeeking = false;
    }

    public void UpdateSeekPosition(double seconds)
    {
        if (!_isSeeking) return;
        
        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = seconds;
        Position = TimeSpan.FromSeconds(seconds);
    }

    public async void EndSeek()
    {
        if (!HasTrack)
        {
            _isSeeking = false;
            return;
        }

        var targetSeconds = PositionSeconds;
        _isSeeking = false;
        _justFinishedSeeking = true;

        // Проверяем cooldown
        var timeSinceLastSeek = DateTime.UtcNow - _lastSeekTime;
        if (timeSinceLastSeek.TotalMilliseconds < SeekCooldownMs)
        {
            var delay = SeekCooldownMs - (int)timeSinceLastSeek.TotalMilliseconds;
            await Task.Delay(delay);
        }

        _lastSeekTime = DateTime.UtcNow;
        IsSeekBusy = true;

        _audio.Seek(TimeSpan.FromSeconds(targetSeconds));
        Position = TimeSpan.FromSeconds(targetSeconds);

        // Ждём подтверждения (макс 1.5 сек)
        await Task.Delay(300);
        
        var timeout = DateTime.UtcNow.AddMilliseconds(1200);
        while (DateTime.UtcNow < timeout)
        {
            var enginePos = _audio.CurrentPosition.TotalSeconds;
            if (Math.Abs(enginePos - targetSeconds) < 2.0)
            {
                break;
            }
            await Task.Delay(50);
        }

        IsSeekBusy = false;
        _justFinishedSeeking = false;
    }

    #endregion

    public void Dispose()
    {
        _positionTimer.Stop();
        _speedUpdateTimer.Stop();
    }
}