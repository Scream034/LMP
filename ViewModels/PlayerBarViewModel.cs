using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using DynamicData.Binding;
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
    private readonly YoutubeProvider _youtube;

    private readonly DispatcherTimer _speedUpdateTimer;
    // Timer для fallback позиции тоже можно оставить, он редкий (500мс)
    private readonly DispatcherTimer _fallbackPositionTimer;

    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private float _volumeBeforeMute;

    private DateTime _lastSeekTime = DateTime.MinValue;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;
    private const int SeekCooldownMs = 250;

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }
    [Reactive] public bool IsLiked { get; private set; }

    public string SafeTitle => CurrentTrack?.Title ?? "Not Playing";
    public string SafeAuthor => CurrentTrack?.Author ?? "";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    public ObservableCollection<StreamOption> AvailableFormats { get; } = [];

    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public double BufferedSeconds { get; private set; }
    [Reactive] public bool IsSeekBusy { get; private set; }

    [Reactive] public int Volume { get; set; }
    [Reactive] public int MaxVolume { get; private set; } = 100;
    [Reactive] public bool IsMuted { get; private set; }
    [Reactive] public bool ShuffleEnabled { get; set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }

    public double VolumeSliderWidth => 100 + ((MaxVolume - 100) * 0.5);

    // Свойства для визуализации громкости (оставил как есть, но оптимизация в AudioEngine handle)
    [Reactive] public double Bar1Opacity { get; set; } = 0.3;
    [Reactive] public double Bar2Opacity { get; set; } = 0.3;
    [Reactive] public double Bar3Opacity { get; set; } = 0.3;
    [Reactive] public double Bar4Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Thickness { get; set; } = 4;
    [Reactive] public bool IsVolumeBoosted { get; set; }

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
    public ReactiveCommand<Unit, Unit> LoadFormatsCommand { get; }
    public ReactiveCommand<StreamOption, Unit> SwitchFormatCommand { get; }

    public PlayerBarViewModel(
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        IClipboardService clipboard,
        YoutubeProvider youtube)
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _clipboard = clipboard;
        _youtube = youtube;

        MaxVolume = _library.Data.MaxVolumeLimit;
        if (MaxVolume < 100) MaxVolume = 100;

        Volume = (int)_audio.GetVolume();
        _volumeBeforeMute = Volume > 5 ? Volume : 50;

        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        UpdateVolumeBars();

        Log.Info($"[PlayerBar] Initialized. MaxVol: {MaxVolume}, CurrentVol: {Volume}");

        _audio.OnPlaybackStateChanged += (isPlaying, isPaused) =>
            Dispatcher.UIThread.Post(() => SyncPlaybackState(isPlaying, isPaused));

        // Оптимизация: Throttle/Sample для обновлений позиции.
        // VLC шлет события очень часто (каждые ~10мс). Мы ограничиваем обновление UI до 5 раз в секунду.
        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
                h => _audio.OnPositionChanged += h,
                h => _audio.OnPositionChanged -= h)
            .Sample(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos =>
            {
                if (!_isSeeking && !_justFinishedSeeking)
                {
                    Position = pos;
                    PositionSeconds = pos.TotalSeconds;
                }
                if (IsSeekBusy && !IsLoading)
                {
                    IsSeekBusy = false;
                }
            });

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
            if (Volume > MaxVolume)
            {
                Volume = MaxVolume;
            }
            UpdateVolumeBars();
        });

        _audio.OnTrackChanged += t => Dispatcher.UIThread.Post(() => HandleTrackChanged(t));

        _audio.WhenValueChanged(x => x.IsLoading)
            .Subscribe(l =>
            {
                IsLoading = l;
                IsSeekBusy = l;
            });

        _audio.OnStreamInfoReady += () => Dispatcher.UIThread.Post(UpdateStreamInfo);

        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        // Оптимизация: Throttle для обновлений прогресса загрузки
        Observable.FromEvent<Action<string, float>, (string, float)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .Sample(TimeSpan.FromMilliseconds(200)) // Не обновлять прогресс-бар чаще 5 раз/сек
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (CurrentTrack?.Id == x.Item1)
                {
                    BufferedSeconds = DurationSeconds * x.Item2;
                }
            });

        var canExecute = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            bool wantsToPlay = !_audio.IsPlaying;
            await _audio.SetPlaybackStateAsync(wantsToPlay);
        }, canExecute);

        NextCommand = ReactiveCommand.CreateFromTask(() => _audio.PlayNextAsync(), canExecute);
        PreviousCommand = ReactiveCommand.CreateFromTask(() => _audio.PlayPreviousAsync(), canExecute);

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
            Log.Info($"[PlayerBar] ToggleLikeCommand: {CurrentTrack?.Id}");
            if (CurrentTrack != null)
            {
                _library.ToggleLike(CurrentTrack);
                IsLiked = CurrentTrack.IsLiked;
            }
        }, canExecute);

        CopyLinkCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Log.Info($"[PlayerBar] Copying link to clipboard: {CurrentTrack?.Url} ({CurrentTrack?.Title})");
            if (CurrentTrack?.Url != null)
            {
                await _clipboard.SetTextAsync(CurrentTrack.Url);
            }
        }, canExecute);

        LoadFormatsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack == null) return;
            AvailableFormats.Clear();
            string videoId = CurrentTrack.Id.Replace("yt_", "");
            var formats = await _youtube.GetStreamOptionsAsync(videoId);
            foreach (var f in formats)
            {
                AvailableFormats.Add(f);
            }
        });

        SwitchFormatCommand = ReactiveCommand.CreateFromTask<StreamOption>(async (option) =>
        {
            if (option == null) return;
            await _audio.SwitchQualityAsync(option.Container, (int)option.Bitrate);
        });
    }

    private void SyncPlaybackState(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
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
            IsVolumeBoosted = true;
        }
        else
        {
            Bar5Thickness = 4;
            IsVolumeBoosted = false;
        }
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        if (track != null)
        {
            Log.Info($"[PlayerBar] Track changed to: {track.Title} (ID: {track.Id}, URL: {track.Url})");
        }
        else
        {
            Log.Info("[PlayerBar] Track cleared");
        }

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
            ShowStreamInfo = true;
            StreamInfo = L["Stream_Loading"] ?? "Loading...";
        }
        else
        {
            DurationSeconds = 1;
            PositionSeconds = 0;
            BufferedSeconds = 0;
            ShowStreamInfo = false;
            StreamInfo = "";
        }
    }

    private void UpdateStreamInfo()
    {
        if (CurrentTrack == null)
        {
            ShowStreamInfo = false;
            StreamInfo = "";
            return;
        }
        var (format, bitrate, isReady) = _audio.GetCurrentStreamInfo();

        if (!isReady || string.IsNullOrEmpty(format))
        {
            StreamInfo = L["Stream_Loading"] ?? "Loading...";
            ShowStreamInfo = true;
            return;
        }

        if (CurrentTrack.IsDownloaded)
        {
            StreamInfo = $"{format} • {L["Stream_LocalFile"] ?? "Local File"}";
        }
        else
        {
            StreamInfo = bitrate > 0 ? $"{format} • {bitrate}kbps" : format;
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
        {
            BufferedSeconds = DurationSeconds * (_audio.BufferProgress / 100.0);
        }
    }

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

        double target = PositionSeconds;
        _isSeeking = false;
        _justFinishedSeeking = true;

        var delta = DateTime.UtcNow - _lastSeekTime;
        if (delta.TotalMilliseconds < SeekCooldownMs)
        {
            await Task.Delay(SeekCooldownMs - (int)delta.TotalMilliseconds);
        }
        _lastSeekTime = DateTime.UtcNow;

        IsSeekBusy = true;
        await _audio.SeekAsync(TimeSpan.FromSeconds(target));
        await Task.Delay(300);

        IsSeekBusy = false;
        _justFinishedSeeking = false;
    }

    public void OnVolumeChangeComplete()
    {
        _audio.SaveVolumeNow();
    }

    public void Dispose()
    {
        _fallbackPositionTimer.Stop();
        _speedUpdateTimer.Stop();
        _audio.SaveVolumeNow();
    }
}