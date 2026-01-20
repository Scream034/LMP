using System;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly DispatcherTimer _speedUpdateTimer;
    private readonly DispatcherTimer _fallbackPositionTimer;

    // === Состояние управления ===
    private bool _isSeeking;
    private bool _justFinishedSeeking;

    // Для Debounce Play/Pause (защита от зависания при спаме)
    private CancellationTokenSource? _playPauseCts;
    private volatile bool _isUserInteractingWithPlayButton;

    // Громкость
    private float _volumeBeforeMute;

    // Тайминги и счетчики
    private DateTime _lastSeekTime = DateTime.MinValue;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;
    private const int SeekCooldownMs = 250;

    // === Свойства Трека ===
    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }
    [Reactive] public bool IsLiked { get; private set; }

    public string SafeTitle => CurrentTrack?.Title ?? "Not Playing";
    public string SafeAuthor => CurrentTrack?.Author ?? "";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    // === Прогресс и Время ===
    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public double BufferedSeconds { get; private set; }
    [Reactive] public bool IsSeekBusy { get; private set; }

    // === Громкость ===
    [Reactive] public int Volume { get; set; }
    [Reactive] public int MaxVolume { get; private set; } = 100;
    [Reactive] public bool IsMuted { get; private set; }
    [Reactive] public bool ShuffleEnabled { get; set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }

    public double VolumeSliderWidth => 100 + ((MaxVolume - 100) * 0.5);

    // === Визуализация ===
    [Reactive] public double Bar1Opacity { get; set; } = 0.3;
    [Reactive] public double Bar2Opacity { get; set; } = 0.3;
    [Reactive] public double Bar3Opacity { get; set; } = 0.3;
    [Reactive] public double Bar4Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Thickness { get; set; } = 4;
    [Reactive] public string VolumeBarBrush { get; set; } = "#B3B3B3";

    [Reactive] public string StreamInfo { get; private set; } = "";
    [Reactive] public bool ShowStreamInfo { get; private set; }
    [Reactive] public string DownloadSpeedText { get; private set; } = "";

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
        if (MaxVolume < 100) MaxVolume = 100;

        Volume = (int)_audio.GetVolume();
        _volumeBeforeMute = Volume > 5 ? Volume : 50;

        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        UpdateVolumeBars();
        Log($"ViewModel initialized. MaxVol: {MaxVolume}, CurrentVol: {Volume}");

        // ---------------- ПОДПИСКИ ----------------

        _audio.OnPositionChanged += pos => Dispatcher.UIThread.Post(() =>
        {
            if (!_isSeeking && !_justFinishedSeeking)
            {
                Position = pos;
                PositionSeconds = pos.TotalSeconds;
            }

            if (IsSeekBusy && !IsLoading) IsSeekBusy = false;

            // Если пользователь не жмет кнопку, обновляем статус.
            if (!_isUserInteractingWithPlayButton)
            {
                UpdatePlayState();
            }
        });

        _audio.OnPlaybackStopped += () => Dispatcher.UIThread.Post(UpdatePlayState);

        this.WhenAnyValue(x => x.Volume)
            .Skip(1)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                IsMuted = v < 1;
                UpdateVolumeBars();
            });

        _audio.OnMaxVolumeChanged += newMax => Dispatcher.UIThread.Post(() =>
        {
            MaxVolume = newMax;
            this.RaisePropertyChanged(nameof(VolumeSliderWidth));
            if (Volume > MaxVolume) Volume = MaxVolume;
            UpdateVolumeBars();
        });

        _audio.OnTrackChanged += t => Dispatcher.UIThread.Post(() => HandleTrackChanged(t));
        _audio.OnLoadingChanged += l => Dispatcher.UIThread.Post(() => { IsLoading = l; IsSeekBusy = l; });

        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        Observable.FromEvent<Action<string, float>, (string, float)>(
            h => (id, p) => h((id, p)),
            h => _downloads.OnProgress += h,
            h => _downloads.OnProgress -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => { if (CurrentTrack?.Id == x.Item1) BufferedSeconds = DurationSeconds * x.Item2; });

        // ---------------- КОМАНДЫ ----------------

        var canEx = this.WhenAnyValue(x => x.HasTrack);

        // === LOGGED PLAY/PAUSE ===
        PlayPauseCommand = ReactiveCommand.Create(() =>
        {
            Log($"[UI] PlayPause Clicked. VM State: Play={IsPlaying}, Pause={IsPaused}");

            _playPauseCts?.Cancel();
            _playPauseCts = new CancellationTokenSource();
            var token = _playPauseCts.Token;

            _isUserInteractingWithPlayButton = true;

            // Optimistic Update
            bool wantsToPlay;
            if (IsPlaying)
            {
                IsPlaying = false;
                IsPaused = true;
                wantsToPlay = false;
            }
            else
            {
                IsPlaying = true;
                IsPaused = false;
                wantsToPlay = true;
            }
            Log($"[UI] Optimistic Update -> IsPlaying={IsPlaying}, sending {wantsToPlay} to engine");

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250, token);
                    if (token.IsCancellationRequested) return;

                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (token.IsCancellationRequested) return;

                        Log($"[UI] Executing Command -> SetPlaybackStateAsync({wantsToPlay})");
                        await _audio.SetPlaybackStateAsync(wantsToPlay);

                        // Даем чуть-чуть времени движку на смену статуса
                        await Task.Delay(200);

                        _isUserInteractingWithPlayButton = false;
                        UpdatePlayState(); // Синхронизация
                    });
                }
                catch { _isUserInteractingWithPlayButton = false; }
            });

        }, canEx);

        NextCommand = ReactiveCommand.CreateFromTask(() => _audio.PlayNextAsync(), canEx);
        PreviousCommand = ReactiveCommand.CreateFromTask(() => _audio.PlayPreviousAsync(), canEx);

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

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            if (Volume > 0)
            {
                _volumeBeforeMute = Volume;
                Volume = 0;
            }
            else
            {
                Volume = (int)(_volumeBeforeMute > 0 ? _volumeBeforeMute : 50);
            }
        });

        ToggleLikeCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentTrack != null) _library.ToggleLike(CurrentTrack);
        }, canEx);

        CopyLinkCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack?.Url != null)
                await _clipboard.SetTextAsync(CurrentTrack.Url);
        }, canEx);
    }

    private void UpdateVolumeBars()
    {
        double vol = Volume;
        Bar1Opacity = vol > 0 ? 1.0 : 0.3;
        Bar2Opacity = vol >= 20 ? 1.0 : 0.3;
        Bar3Opacity = vol >= 40 ? 1.0 : 0.3;
        Bar4Opacity = vol >= 60 ? 1.0 : 0.3;
        Bar5Opacity = vol >= 80 ? 1.0 : 0.3;

        if (vol > 100)
        {
            double boost = (vol - 100) / 100.0;
            Bar5Thickness = 4 + (boost * 6);
            if (Bar5Thickness > 12) Bar5Thickness = 12;
            VolumeBarBrush = "#1DB954";
        }
        else
        {
            Bar5Thickness = 4;
            VolumeBarBrush = "#B3B3B3";
        }
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
            ShowStreamInfo = false;
        }
        UpdatePlayState();
    }

    private void UpdateStreamInfo()
    {
        if (CurrentTrack == null) { ShowStreamInfo = false; return; }
        var info = _audio.GetCurrentStreamInfo();
        StreamInfo = !string.IsNullOrEmpty(info.Format)
            ? $"{info.Format} • {info.Bitrate}kbps"
            : "Stream";
        ShowStreamInfo = true;
    }

    private void UpdateDownloadSpeed()
    {
        if (!HasTrack || CurrentTrack?.IsDownloaded == true) { DownloadSpeedText = ""; return; }

        var currentBytes = _audio.GetDownloadedBytes();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedCheck).TotalSeconds;

        if (elapsed >= 0.5 && _lastSpeedCheck != DateTime.MinValue)
        {
            var kbs = ((currentBytes - _lastDownloadedBytes) / elapsed) / 1024.0;
            DownloadSpeedText = kbs > 10
                ? (kbs >= 1024 ? $"{kbs / 1024:F1} MB/s" : $"{kbs:F0} KB/s")
                : "";
        }
        _lastDownloadedBytes = currentBytes;
        _lastSpeedCheck = now;
    }

    private void FallbackPositionUpdate()
    {
        if (!HasTrack || _isSeeking || _justFinishedSeeking) return;

        var realDur = _audio.TotalDuration;
        if (Math.Abs(DurationSeconds - realDur.TotalSeconds) > 1 && realDur.TotalSeconds > 0)
        {
            Duration = realDur;
            DurationSeconds = Duration.TotalSeconds;
        }

        if (_audio.BufferProgress > 0)
            BufferedSeconds = DurationSeconds * (_audio.BufferProgress / 100.0);

        if (!_isUserInteractingWithPlayButton)
            UpdatePlayState();
    }

    private void UpdatePlayState()
    {
        if (_isUserInteractingWithPlayButton) return;

        bool enginePlaying = _audio.IsPlaying;
        bool enginePaused = _audio.IsPaused;

        // Если состояние изменилось - логируем
        if (IsPlaying != enginePlaying || IsPaused != enginePaused)
        {
            // Debug.WriteLine($"[UI] Sync State: Play={enginePlaying}, Pause={enginePaused}");
        }

        IsPlaying = enginePlaying;
        IsPaused = enginePaused;
    }

    // === Seek Logic ===

    public void StartSeek()
    {
        _isSeeking = true;
        _justFinishedSeeking = false;
    }

    public void UpdateSeekPosition(double s)
    {
        if (!_isSeeking) return;

        s = Math.Clamp(s, 0, DurationSeconds);
        PositionSeconds = s;
        Position = TimeSpan.FromSeconds(s);
    }

    public async void EndSeek()
    {
        if (!HasTrack) { _isSeeking = false; return; }

        double target = PositionSeconds;
        _isSeeking = false;
        _justFinishedSeeking = true;

        var delta = DateTime.UtcNow - _lastSeekTime;
        if (delta.TotalMilliseconds < SeekCooldownMs)
            await Task.Delay(SeekCooldownMs - (int)delta.TotalMilliseconds);

        _lastSeekTime = DateTime.UtcNow;
        IsSeekBusy = true;
        Log($"[UI] Seek End -> {target}s");

        await _audio.SeekAsync(TimeSpan.FromSeconds(target));

        await Task.Delay(300);
        IsSeekBusy = false;
        _justFinishedSeeking = false;
    }

    public void OnVolumeChangeComplete()
    {
        Log($"[UI] Volume Drag End. Saving.");
        _audio.SaveVolumeNow();
    }

    private void Log(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [PlayerBarVM] {message}");
    }

    public void Dispose()
    {
        _fallbackPositionTimer.Stop();
        _speedUpdateTimer.Stop();
        _playPauseCts?.Cancel();
        _audio.SaveVolumeNow();
    }
}