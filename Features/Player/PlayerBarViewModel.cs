using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using DynamicData.Binding;
using MyLiteMusicPlayer.Core.Models;
using MyLiteMusicPlayer.Core.Services;
using MyLiteMusicPlayer.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Features.Player;

public sealed class PlayerBarViewModel : ViewModelBase, IDisposable
{
    #region Constants

    private const int SeekCooldownMs = 250;
    private const int NavigationDebounceMs = 300;
    private const int HintDisplayDurationMs = 1500;
    private const int CopyHighlightDurationMs = 800;

    #endregion

    #region Fields

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly IClipboardService _clipboard;
    private readonly YoutubeProvider _youtube;

    private readonly DispatcherTimer _speedUpdateTimer;
    private readonly DispatcherTimer _fallbackPositionTimer;
    
    private readonly IDisposable? _librarySub;
    private readonly IDisposable? _downloadProgressSub;
    private readonly IDisposable _nextSub;
    private readonly IDisposable _prevSub;
    private readonly IDisposable? _loadingSub; // Добавлено для корректной очистки
    private readonly Subject<Unit> _nextSubject = new();
    private readonly Subject<Unit> _prevSubject = new();

    private readonly Action<bool, bool> _playbackStateHandler;
    private readonly Action _queueChangedHandler;
    private readonly Action<TimeSpan> _positionChangedHandler;
    private readonly Action<int> _maxVolumeChangedHandler;
    private readonly Action<TrackInfo?> _trackChangedHandler;
    private readonly Action _streamInfoReadyHandler;

    private bool _isSeeking;
    private bool _justFinishedSeeking;
    private bool _isDisposed;

    private DateTime _lastSeekTime = DateTime.MinValue;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;

    #endregion

    #region Properties - Playback State

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool IsNavigating { get; private set; }

    public string SafeTitle => CurrentTrack?.Title ?? "Not Playing";
    public string SafeAuthor => CurrentTrack?.Author ?? "";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    #endregion

    #region Properties - Queue Info

    [Reactive] public int CurrentTrackIndex { get; private set; }
    [Reactive] public int TotalTracksInQueue { get; private set; }
    [Reactive] public bool HasQueueToShuffle { get; private set; }
    
    public string QueuePositionText => TotalTracksInQueue > 0 
        ? $"{CurrentTrackIndex + 1} / {TotalTracksInQueue}" 
        : "";

    #endregion

    #region Properties - Seek & Duration

    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public double BufferedSeconds { get; private set; }
    [Reactive] public bool IsSeekBusy { get; private set; }

    #endregion

    #region Properties - Volume

    [Reactive] public int Volume { get; set; }
    [Reactive] public int MaxVolume { get; private set; } = 100;
    [Reactive] public bool IsMuted { get; private set; }

    public double VolumeSliderWidth => 100 + ((MaxVolume - 100) * 0.5);

    [Reactive] public double Bar1Opacity { get; set; } = 0.3;
    [Reactive] public double Bar2Opacity { get; set; } = 0.3;
    [Reactive] public double Bar3Opacity { get; set; } = 0.3;
    [Reactive] public double Bar4Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Thickness { get; set; } = 4;
    [Reactive] public bool IsVolumeBoosted { get; set; }

    #endregion

    #region Properties - Repeat & Shuffle

    [Reactive] public bool ShuffleEnabled { get; private set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }

    [Reactive] public bool IsRepeatHintVisible { get; private set; }
    [Reactive] public string RepeatHintText { get; private set; } = "";

    #endregion

    #region Properties - Like & Copy Hints

    [Reactive] public bool IsLikeHintVisible { get; private set; }
    [Reactive] public string LikeHintText { get; private set; } = "";

    [Reactive] public bool IsCopyHintVisible { get; private set; }
    [Reactive] public bool IsCopyHighlighted { get; private set; }

    #endregion

    #region Properties - Stream Info

    [Reactive] public string StreamInfo { get; private set; } = "";
    [Reactive] public bool ShowStreamInfo { get; private set; }
    [Reactive] public string DownloadSpeedText { get; private set; } = "";
    
    public ObservableCollection<StreamOption> AvailableFormats { get; } = [];

    #endregion

    #region Properties - Tooltips

    public string ShuffleTooltip => SL["Player_Shuffle"] ?? "Shuffle";
    public string PreviousTooltip => SL["Player_Previous"] ?? "Previous";
    public string NextTooltip => SL["Player_Next"] ?? "Next";
    
    public string PlayPauseTooltip => IsPlaying 
        ? (SL["Player_Pause"] ?? "Pause") 
        : (SL["Player_Play"] ?? "Play");

    public string RepeatTooltip => RepeatMode switch
    {
        RepeatMode.None => SL["Player_Repeat_Off"] ?? "Repeat Off",
        RepeatMode.RepeatAll => SL["Player_Repeat_All"] ?? "Repeat Queue",
        RepeatMode.RepeatOne => SL["Player_Repeat_One"] ?? "Repeat Track",
        _ => ""
    };

    public string LikeTooltip => IsLiked 
        ? (SL["Track_Unlike"] ?? "Remove from Liked") 
        : (SL["Track_Like"] ?? "Add to Liked");

    public string CopyTooltip => SL["Track_CopyLink"] ?? "Copy Link";

    public string VolumeTooltip => IsMuted 
        ? (SL["Player_Unmute"] ?? "Unmute") 
        : $"{SL["Player_Volume"] ?? "Volume"}: {Volume}%";

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadFormatsCommand { get; }
    public ReactiveCommand<StreamOption, Unit> SwitchFormatCommand { get; }

    #endregion

    #region Constructor

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

        // Initialize values
        MaxVolume = _library.Data.MaxVolumeLimit < 100 ? 100 : _library.Data.MaxVolumeLimit;
        Volume = (int)_audio.GetVolume();
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;
        UpdateVolumeBars();
        UpdateQueueState();

        Log.Info($"[PlayerBar] Initialized. MaxVol: {MaxVolume}, CurrentVol: {Volume}");

        // Event handlers (уже используют Dispatcher, это хорошо)
        _playbackStateHandler = (isPlaying, isPaused) => Dispatcher.UIThread.Post(() => 
        {
            SyncPlaybackState(isPlaying, isPaused);
            this.RaisePropertyChanged(nameof(PlayPauseTooltip));
        });
        _queueChangedHandler = () => Dispatcher.UIThread.Post(UpdateQueueState);
        _maxVolumeChangedHandler = newMax => Dispatcher.UIThread.Post(() => HandleMaxVolumeChanged(newMax));
        _trackChangedHandler = t => Dispatcher.UIThread.Post(() => HandleTrackChanged(t));
        _streamInfoReadyHandler = () => Dispatcher.UIThread.Post(UpdateStreamInfo);
        _positionChangedHandler = pos =>
        {
             if (!_isSeeking && !_justFinishedSeeking)
             {
                 Dispatcher.UIThread.Post(() => {
                     Position = pos;
                     PositionSeconds = pos.TotalSeconds;
                 });
             }
             if (IsSeekBusy && !IsLoading)
             {
                 Dispatcher.UIThread.Post(() => IsSeekBusy = false);
             }
        };

        // Subscribe to AudioEngine events
        _audio.OnPlaybackStateChanged += _playbackStateHandler;
        _audio.OnQueueChanged += _queueChangedHandler;
        _audio.OnPositionChanged += _positionChangedHandler;
        _audio.OnMaxVolumeChanged += _maxVolumeChangedHandler;
        _audio.OnTrackChanged += _trackChangedHandler;
        _audio.OnStreamInfoReady += _streamInfoReadyHandler;

        // Rx subscriptions
        _librarySub = Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
                h => _library.OnTrackUpdated += h,
                h => _library.OnTrackUpdated -= h)
            .Where(t => CurrentTrack != null && t.Id == CurrentTrack.Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                IsLiked = t.IsLiked;
                if (CurrentTrack != null) CurrentTrack.IsLiked = t.IsLiked;
                this.RaisePropertyChanged(nameof(LikeTooltip));
            });

        // Volume updates TO audio engine
        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                IsMuted = v < 1;
                UpdateVolumeBars();
                this.RaisePropertyChanged(nameof(VolumeTooltip));
            });

        this.WhenAnyValue(x => x.RepeatMode)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(RepeatTooltip)));

        this.WhenAnyValue(x => x.IsLiked)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(LikeTooltip)));

        // ИСПРАВЛЕНО: IsLoading подписка теперь переключается на UI поток
        // Это предотвращает Call from invalid thread при обновлении команд
        _loadingSub = _audio.WhenValueChanged(x => x.IsLoading)
            .ObserveOn(RxApp.MainThreadScheduler) // !!! ВАЖНО !!!
            .Subscribe(l =>
            {
                IsLoading = l;
                IsSeekBusy = l;
            });

        // Timers
        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        // Download progress subscription
        _downloadProgressSub = Observable.FromEvent<Action<string, float>, (string, float)>(
                h => (id, p) => h((id, p)),
                h => _downloads.OnProgress += h,
                h => _downloads.OnProgress -= h)
            .Sample(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (CurrentTrack?.Id == x.Item1)
                {
                    BufferedSeconds = DurationSeconds * x.Item2;
                }
            });

        // Navigation
        _nextSub = _nextSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _audio.PlayNextAsync(); }
                finally { IsNavigating = false; }
            });

        _prevSub = _prevSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _audio.PlayPreviousAsync(); }
                finally { IsNavigating = false; }
            });

        // Commands
        var canExecute = this.WhenAnyValue(x => x.HasTrack, x => x.IsLoading, 
            (hasTrack, loading) => hasTrack && !loading);
        var canNavigate = this.WhenAnyValue(x => x.HasTrack, x => x.IsNavigating, x => x.IsLoading,
            (hasTrack, isNav, loading) => hasTrack && !isNav && !loading);

        PlayPauseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            bool wantsToPlay = !_audio.IsPlaying;
            await _audio.SetPlaybackStateAsync(wantsToPlay);
        }, this.WhenAnyValue(x => x.HasTrack));

        NextCommand = ReactiveCommand.Create(() => { IsNavigating = true; _nextSubject.OnNext(Unit.Default); }, canNavigate);
        PreviousCommand = ReactiveCommand.Create(() => { IsNavigating = true; _prevSubject.OnNext(Unit.Default); }, canNavigate);

        var canShuffle = this.WhenAnyValue(x => x.HasQueueToShuffle, x => x.IsLoading,
            (hasTracks, loading) => hasTracks && !loading);
        ShuffleQueueCommand = ReactiveCommand.Create(() =>
        {
            _audio.ShuffleQueue();
            ShuffleEnabled = true;
            Observable.Timer(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ShuffleEnabled = false);
        }, canShuffle);

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
            
            ShowRepeatModeHint();
        });

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            _audio.ToggleMute();
            Volume = (int)_audio.GetVolume();
            this.RaisePropertyChanged(nameof(VolumeTooltip));
        });

        ToggleLikeCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentTrack != null)
            {
                _library.ToggleLike(CurrentTrack);
                ShowLikeHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack));

        CopyLinkCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Log.Info($"[PlayerBar] Copying link: {CurrentTrack?.Url}");
            if (CurrentTrack?.Url != null)
            {
                await _clipboard.SetTextAsync(CurrentTrack.Url);
                ShowCopyHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack));

        LoadFormatsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack == null) return;
            AvailableFormats.Clear();
            string videoId = CurrentTrack.Id.Replace("yt_", "");
            var formats = await _youtube.GetStreamOptionsAsync(videoId);
            foreach (var f in formats) AvailableFormats.Add(f);
        });

        SwitchFormatCommand = ReactiveCommand.CreateFromTask<StreamOption>(async (option) =>
        {
            if (option == null) return;
            await _audio.SwitchQualityAsync(option.Container, (int)option.Bitrate);
        });
    }

    #endregion

    #region Hint Methods

    private async void ShowRepeatModeHint()
    {
        RepeatHintText = RepeatMode switch
        {
            RepeatMode.None => SL["Player_Repeat_Off"] ?? "Repeat Off",
            RepeatMode.RepeatAll => SL["Player_Repeat_All"] ?? "Repeat Queue",
            RepeatMode.RepeatOne => SL["Player_Repeat_One"] ?? "Repeat Track",
            _ => ""
        };
        
        IsRepeatHintVisible = true;
        await Task.Delay(HintDisplayDurationMs);
        IsRepeatHintVisible = false;
    }

    private async void ShowLikeHint()
    {
        LikeHintText = IsLiked 
            ? (SL["Track_Added"] ?? "Added to Liked") 
            : (SL["Track_Removed"] ?? "Removed from Liked");
        
        IsLikeHintVisible = true;
        await Task.Delay(HintDisplayDurationMs);
        IsLikeHintVisible = false;
    }

    private async void ShowCopyHint()
    {
        IsCopyHighlighted = true;
        IsCopyHintVisible = true;
        
        await Task.Delay(CopyHighlightDurationMs);
        IsCopyHighlighted = false;
        
        await Task.Delay(HintDisplayDurationMs - CopyHighlightDurationMs);
        IsCopyHintVisible = false;
    }

    #endregion

    #region Private Handlers

    private void HandleMaxVolumeChanged(int newMax)
    {
        MaxVolume = newMax;
        this.RaisePropertyChanged(nameof(VolumeSliderWidth));
        if (Volume > MaxVolume) Volume = MaxVolume;
        UpdateVolumeBars();
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        CurrentTrack = track;
        HasTrack = track != null;
        IsNavigating = false;

        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(SafeAuthor));
        this.RaisePropertyChanged(nameof(SafeThumbnail));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));

        IsSeekBusy = true;
        _lastDownloadedBytes = 0;

        if (track != null)
        {
            Duration = track.Duration;
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;

            var storedTrack = _library.GetTrack(track.Id);
            IsLiked = storedTrack?.IsLiked ?? track.IsLiked;

            Position = TimeSpan.Zero;
            PositionSeconds = 0;
            BufferedSeconds = track.IsDownloaded ? DurationSeconds : 0;
            ShowStreamInfo = true;
            StreamInfo = SL["Stream_Loading"] ?? "Loading...";
        }
        else
        {
            DurationSeconds = 1;
            PositionSeconds = 0;
            BufferedSeconds = 0;
            ShowStreamInfo = false;
            StreamInfo = "";
            IsLiked = false;
        }

        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        var queue = _audio.Queue;
        TotalTracksInQueue = queue.Count;
        HasQueueToShuffle = queue.Count > 1;
        
        if (CurrentTrack != null)
        {
            CurrentTrackIndex = queue.ToList().FindIndex(t => t.Id == CurrentTrack.Id);
            if (CurrentTrackIndex < 0) CurrentTrackIndex = 0;
        }
        else
        {
            CurrentTrackIndex = 0;
        }
        
        this.RaisePropertyChanged(nameof(QueuePositionText));
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
            StreamInfo = SL["Stream_Loading"] ?? "Loading...";
            ShowStreamInfo = true;
            return;
        }

        if (CurrentTrack.IsDownloaded)
            StreamInfo = $"{format} • {SL["Stream_LocalFile"] ?? "Local File"}";
        else
            StreamInfo = bitrate > 0 ? $"{format} • {bitrate}kbps" : format;
        
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

    #endregion

    #region Public Interaction

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
            await Task.Delay(SeekCooldownMs - (int)delta.TotalMilliseconds);
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

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _audio.SaveVolumeNow();

        _fallbackPositionTimer.Stop();
        _speedUpdateTimer.Stop();

        _audio.OnPlaybackStateChanged -= _playbackStateHandler;
        _audio.OnQueueChanged -= _queueChangedHandler;
        _audio.OnPositionChanged -= _positionChangedHandler;
        _audio.OnMaxVolumeChanged -= _maxVolumeChangedHandler;
        _audio.OnTrackChanged -= _trackChangedHandler;
        _audio.OnStreamInfoReady -= _streamInfoReadyHandler;

        _librarySub?.Dispose();
        _loadingSub?.Dispose();
        _downloadProgressSub?.Dispose();
        _nextSub.Dispose();
        _prevSub.Dispose();
        _nextSubject.Dispose();
        _prevSubject.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}