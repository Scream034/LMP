using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using DynamicData.Binding;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Player;

public sealed class PlayerBarViewModel : ViewModelBase
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
    private readonly MusicLibraryManager _musicManager;
    private readonly StreamCacheManager _cacheManager;

    private readonly DispatcherTimer _speedUpdateTimer;
    private readonly DispatcherTimer _fallbackPositionTimer;
    private readonly Subject<Unit> _nextSubject = new();
    private readonly Subject<Unit> _prevSubject = new();

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

    public string SafeTitle => CurrentTrack?.Title ?? SL["Player_NotPlaying"];
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

    public static string ShuffleTooltip => SL.Get("Player_Shuffle", "Shuffle");
    public static string PreviousTooltip => SL.Get("Player_Previous", "Previous");
    public static string NextTooltip => SL.Get("Player_Next", "Next");

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

    public static string CopyTooltip => SL["Track_CopyLink"] ?? "Copy Link";

    public string VolumeTooltip => IsMuted
        ? SL.Get("Player_Unmute", "Unmute")
        : string.Format(SL.Get("Player_VolumeTooltip", "Volume: {0}%"), Volume);

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
        YoutubeProvider youtube,
        MusicLibraryManager musicManager,
        StreamCacheManager cacheManager)
    {
        _audio = audio;
        _library = library;
        _downloads = downloads;
        _clipboard = clipboard;
        _youtube = youtube;
        _musicManager = musicManager;
        _cacheManager = cacheManager;

        // Initialize values
        MaxVolume = _library.Settings.MaxVolumeLimit < 100 ? 100 : _library.Settings.MaxVolumeLimit;
        Volume = (int)_audio.GetVolume();
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;
        UpdateVolumeBars();
        UpdateQueueState();

        Log.Info($"Initialized. MaxVol: {MaxVolume}, CurrentVol: {Volume}");

        // --- Event Subscriptions (via DisposeWith to prevent leaks) ---

        // AudioEngine Events
        Observable.FromEvent<Action<bool, bool>, (bool, bool)>(
            h => (p, u) => h((p, u)),
            h => _audio.OnPlaybackStateChanged += h,
            h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                SyncPlaybackState(state.Item1, state.Item2);
                this.RaisePropertyChanged(nameof(PlayPauseTooltip));
            })
            .DisposeWith(Disposables);

        Observable.FromEvent(
            h => _audio.OnQueueChanged += h,
            h => _audio.OnQueueChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateQueueState())
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
            h => _audio.OnPositionChanged += h,
            h => _audio.OnPositionChanged -= h)
            // No ObserveOn needed for high frequency if logic is simple, 
            // but for UI update usually needed. Throttling is handled in VM state if needed.
            .Subscribe(pos =>
            {
                if (!_isSeeking && !_justFinishedSeeking)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Position = pos;
                        PositionSeconds = pos.TotalSeconds;
                    });
                }
                if (IsSeekBusy && !IsLoading)
                {
                    Dispatcher.UIThread.Post(() => IsSeekBusy = false);
                }
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<int>, int>(
            h => _audio.OnMaxVolumeChanged += h,
            h => _audio.OnMaxVolumeChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleMaxVolumeChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
            h => _audio.OnTrackChanged += h,
            h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleTrackChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent(
            h => _audio.OnStreamInfoReady += h,
            h => _audio.OnStreamInfoReady -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateStreamInfo()) // ИСПРАВЛЕНО: используем лямбду для игнорирования аргумента Unit
            .DisposeWith(Disposables);

        // Cache Events
        Observable.FromEvent<Action<string, string, int, bool>, (string, string, int, bool)>(
            h => (t, c, b, d) => h((t, c, b, d)),
            h => _cacheManager.OnFormatCached += h,
            h => _cacheManager.OnFormatCached -= h)
            .Subscribe(x => OnFormatCached(x.Item1, x.Item2, x.Item3, x.Item4))
            .DisposeWith(Disposables);

        // Library Updates
        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
            h => _library.OnTrackUpdated += h,
            h => _library.OnTrackUpdated -= h)
            .Where(t => CurrentTrack != null && t.Id == CurrentTrack.Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                IsLiked = t.IsLiked;
                CurrentTrack?.IsLiked = t.IsLiked;
                this.RaisePropertyChanged(nameof(LikeTooltip));
            })
            .DisposeWith(Disposables);

        // Download Progress
        Observable.FromEvent<Action<string, float>, (string, float)>(
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
            })
            .DisposeWith(Disposables);


        // Volume updates TO audio engine
        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                IsMuted = v < 1;
                UpdateVolumeBars();
                this.RaisePropertyChanged(nameof(VolumeTooltip));
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.RepeatMode)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(RepeatTooltip)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsLiked)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(LikeTooltip)))
            .DisposeWith(Disposables);

        _audio.WhenValueChanged(x => x.IsLoading)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(l =>
            {
                IsLoading = l;
                IsSeekBusy = l;
            })
            .DisposeWith(Disposables);

        // Timers
        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        // Navigation Subjects
        _nextSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _audio.PlayNextAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);

        _prevSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _audio.PlayPreviousAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);

        // Commands
        var canExecute = this.WhenAnyValue(x => x.HasTrack, x => x.IsLoading,
            (hasTrack, loading) => hasTrack && !loading);
        var canNavigate = this.WhenAnyValue(x => x.HasTrack, x => x.IsNavigating, x => x.IsLoading,
            (hasTrack, isNav, loading) => hasTrack && !isNav && !loading);

        PlayPauseCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            bool wantsToPlay = !_audio.IsPlaying;
            await _audio.SetPlaybackStateAsync(wantsToPlay);
        }, this.WhenAnyValue(x => x.HasTrack)));

        NextCommand = CreateCommand(ReactiveCommand.Create(() => { IsNavigating = true; _nextSubject.OnNext(Unit.Default); }, canNavigate));
        PreviousCommand = CreateCommand(ReactiveCommand.Create(() => { IsNavigating = true; _prevSubject.OnNext(Unit.Default); }, canNavigate));

        var canShuffle = this.WhenAnyValue(x => x.HasQueueToShuffle, x => x.IsLoading,
            (hasTracks, loading) => hasTracks && !loading);
        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _audio.ShuffleQueue();
            ShuffleEnabled = true;
            Observable.Timer(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => ShuffleEnabled = false)
                .DisposeWith(Disposables);
        }, canShuffle));

        ToggleRepeatCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch { RepeatMode.None => RepeatMode.RepeatAll, RepeatMode.RepeatAll => RepeatMode.RepeatOne, _ => RepeatMode.None };
            _audio.RepeatMode = RepeatMode;
            _library.UpdateSettings(s => s.RepeatMode = RepeatMode);
            ShowRepeatModeHint();
        }));

        ToggleMuteCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _audio.ToggleMute();
            Volume = (int)_audio.GetVolume();
            this.RaisePropertyChanged(nameof(VolumeTooltip));
        }));

        ToggleLikeCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack != null)
            {
                await _musicManager.ToggleLikeAsync(CurrentTrack);
                ShowLikeHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack)));

        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack?.Url != null)
            {
                await _clipboard.SetTextAsync(CurrentTrack.Url);
                ShowCopyHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack)));

        LoadFormatsCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadFormatsAsync));

        SwitchFormatCommand = CreateCommand(ReactiveCommand.CreateFromTask<StreamOption>(async (option) =>
        {
            if (option == null) return;
            foreach (var f in AvailableFormats) f.IsActive = false;
            option.IsActive = true;
            await _audio.SwitchQualityAsync(option.Container, (int)option.Bitrate);
        }));
    }

    #endregion

    #region Format Loading

    private async Task LoadFormatsAsync()
    {
        if (CurrentTrack == null) return;

        try
        {
            string videoId = CurrentTrack.Id.Replace("yt_", "");
            var formats = await _youtube.GetStreamOptionsAsync(videoId);

            var (currentFormat, currentBitrate, _) = _audio.GetCurrentStreamInfo();
            var cachedFormats = _cacheManager.GetCachedFormats(CurrentTrack.Id);

            AvailableFormats.Clear();

            foreach (var f in formats)
            {
                f.IsDownloaded = cachedFormats.Any(cached =>
                    string.Equals(f.Container, cached.Container, StringComparison.OrdinalIgnoreCase) &&
                    (int)f.Bitrate == cached.Bitrate);

                if (!f.IsDownloaded)
                {
                    f.IsDownloaded = _cacheManager.IsFormatCached(CurrentTrack.Id, f.Container, (int)f.Bitrate);
                }

                f.IsActive = string.Equals(f.Codec, currentFormat, StringComparison.OrdinalIgnoreCase) &&
                             (int)f.Bitrate == currentBitrate;

                AvailableFormats.Add(f);
            }
            Log.Debug($"Loaded {AvailableFormats.Count} formats, {cachedFormats.Count} cached");
        }
        catch (Exception ex)
        {
            Log.Error($"LoadFormatsAsync error: {ex.Message}");
        }
    }

    private void OnFormatCached(string trackId, string container, int bitrate, bool isDownloaded)
    {
        if (CurrentTrack == null || CurrentTrack.Id != trackId)
            return;

        bool found = false;
        foreach (var format in AvailableFormats)
        {
            if (string.Equals(format.Container, container, StringComparison.OrdinalIgnoreCase) &&
                (int)format.Bitrate == bitrate)
            {
                format.IsDownloaded = isDownloaded;
                found = true;
                break;
            }
        }

        if (!found && AvailableFormats.Count > 0)
        {
            _ = LoadFormatsAsync();
        }

        if (isDownloaded)
        {
            UpdateStreamInfo();
        }
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

        AvailableFormats.Clear();

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
            StreamInfo = SL.Get("Player_StreamInfo_Loading", "Loading...");
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
            StreamInfo = SL.Get("Player_StreamInfo_Loading", "Loading...");
            ShowStreamInfo = true;
            return;
        }

        foreach (var f in AvailableFormats)
        {
            f.IsActive = string.Equals(f.Codec, format, StringComparison.OrdinalIgnoreCase) &&
                         (int)f.Bitrate == bitrate;
        }

        if (bitrate > 0)
        {
            StreamInfo = $"{format} • {bitrate}kbps";
        }
        else
        {
            StreamInfo = format;
        }

        if (CurrentTrack.IsDownloaded && !string.IsNullOrEmpty(CurrentTrack.LocalPath))
        {
            StreamInfo += " ✓";
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
                ? (kbs >= 1024
                    ? string.Format(SL.Get("Stream_Speed_Mb", "{0:F1} MB/s"), kbs / 1024)
                    : string.Format(SL.Get("Stream_Speed_Kb", "{0:F0} KB/s"), kbs))
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

        if (IsPlaying)
        {
            Position = _audio.CurrentPosition;
            PositionSeconds = Position.TotalSeconds;
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

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _isDisposed = true;
            _audio.SaveVolumeNow();
            _fallbackPositionTimer.Stop();
            _speedUpdateTimer.Stop();
            
            // Note: Rx subscriptions (via DisposeWith) and Timer handles are cleared in base.Dispose()
        }
        base.Dispose(disposing);
    }

    #endregion
}