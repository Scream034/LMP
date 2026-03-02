using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using LMP.Core.Audio;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Player;

/// <summary>
/// ViewModel для нижней панели управления плеером (Player Bar).
/// Координирует UI с движком AudioEngine, управляет состоянием воспроизведения,
/// очередью, ползунками громкости и времени.
/// </summary>
public sealed class PlayerBarViewModel : ViewModelBase
{
    #region Constants

    private const int NavigationDebounceMs = 300;
    private const int HintDisplayDurationMs = 1500;
    private const int CopyHighlightDurationMs = 800;
    private const int PositionUpdateThrottleMs = 50;
    private const int TrackResetMinDurationMs = 300;
    private const int FallbackPositionIntervalMs = 500;
    private const int ShuffleAnimationDurationMs = 500;

    #endregion

    #region Fields

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly MusicLibraryManager _musicManager;

    private readonly DispatcherTimer _speedUpdateTimer;
    private readonly DispatcherTimer _fallbackPositionTimer;
    private readonly Subject<Unit> _nextSubject = new();
    private readonly Subject<Unit> _prevSubject = new();

    private bool _isSeeking;
    private bool _isInitialized;
    private volatile bool _isSuspended;

    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;
    private int _lastVolumeBeforeMute = 50;

    private WeakReference<PlayerBarView>? _viewRef;

    private int _trackResetSession;
    private DateTime _trackResetStartTime;
    private string? _pendingStreamInfoTrackId;
    private long _seekBusyStartTicks;

    #endregion

    #region Properties - Playback State

    [Reactive] public TrackInfo? CurrentTrack { get; private set; }
    [Reactive] public bool IsLoading { get; private set; }
    [Reactive] public bool IsPlaying { get; private set; }
    [Reactive] public bool IsPaused { get; private set; }
    [Reactive] public bool HasTrack { get; private set; }
    [Reactive] public bool IsLiked { get; private set; }
    [Reactive] public bool IsNavigating { get; private set; }
    [Reactive] public bool IsTrackResetting { get; private set; }

    public string SafeTitle => CurrentTrack?.Title ?? SL["Player_NotPlaying"];
    public string SafeAuthor => CurrentTrack?.Author ?? "";
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    #endregion

    #region Properties - Queue Info

    [Reactive] public int CurrentTrackIndex { get; private set; }
    [Reactive] public int TotalTracksInQueue { get; private set; }
    [Reactive] public bool HasQueueToShuffle { get; private set; }

    public string CurrentTrackIndexDisplay => (CurrentTrackIndex + 1).ToString();

    #endregion

    #region Properties - Seek & Duration

    [Reactive] public TimeSpan Position { get; set; }
    [Reactive] public TimeSpan Duration { get; private set; }
    [Reactive] public double PositionSeconds { get; set; }
    [Reactive] public double DurationSeconds { get; private set; }
    [Reactive] public bool IsSeekBusy { get; private set; }
    [Reactive] public bool IsSeekPreviewVisible { get; set; }

    #endregion

    #region Properties - Buffer Progress

    [Reactive] public double BufferProgressPercent { get; private set; }
    [Reactive] public IReadOnlyList<(double Start, double End)> BufferedRanges { get; private set; } = [];
    public bool UseSegmentedBuffer => BufferedRanges.Count > 1;
    [Reactive] public bool IsFullyBuffered { get; private set; }

    #endregion

    #region Properties - Volume

    [Reactive] public int Volume { get; set; }
    [Reactive] public int MaxVolume { get; private set; } = 100;
    [Reactive] public bool IsVolumePopupOpen { get; set; }
    [Reactive] public bool IsVolumePreviewVisible { get; set; }

    public float RealGain => _audio.GetVolume() > 0
        ? Math.Clamp(_audio.GetVolume() / 100f, 0f, 4f)
        : 0f;

    public bool IsReallyBoosted
    {
        get
        {
            var settings = _library.Settings.Audio;
            if (!settings.VolumeBoostEnabled) return false;
            return Volume > AudioEngine.VolumeNormalRange;
        }
    }

    public bool IsMuted => Volume < 1;
    public bool IsVolumeLow => Volume >= 1 && !IsReallyBoosted && GetEffectivePercent() <= 33;
    public bool IsVolumeMedium => !IsMuted && !IsReallyBoosted && GetEffectivePercent() > 33 && GetEffectivePercent() <= 66;
    public bool IsVolumeHigh => !IsMuted && !IsReallyBoosted && GetEffectivePercent() > 66;
    public bool IsVolumeBoosted => IsReallyBoosted;

    private int GetEffectivePercent()
    {
        var settings = _library.Settings.Audio;

        if (settings.VolumeBoostEnabled)
        {
            return Volume <= AudioEngine.VolumeNormalRange
                ? (int)(Volume / 2.0)
                : 100;
        }

        int maxVol = MaxVolume > 0 ? MaxVolume : 100;
        return (int)((double)Volume / maxVol * 100);
    }

    public IBrush VolumePercentBrush
    {
        get
        {
            var app = Application.Current;
            if (app == null) return Brushes.White;

            if (IsReallyBoosted)
            {
                if (app.Resources.TryGetResource("SystemWarnOrangeBrush", app.ActualThemeVariant, out var warnBrush)
                    && warnBrush is IBrush warn)
                    return warn;
                return new SolidColorBrush(Color.Parse("#FFB86C"));
            }

            if (app.Resources.TryGetResource("TextPrimaryBrush", app.ActualThemeVariant, out var textBrush)
                && textBrush is IBrush text)
                return text;
            return Brushes.White;
        }
    }

    #endregion

    #region Properties - Repeat & Shuffle

    /// <summary>
    /// Визуальная анимация shuffle (кратковременная подсветка при нажатии ЛКМ).
    /// НЕ персистентное состояние.
    /// </summary>
    [Reactive] public bool IsShuffleAnimating { get; private set; }

    /// <summary>
    /// Автоматическое перемешивание при старте очереди.
    /// Персистентное состояние из настроек. Переключается по ПКМ.
    /// </summary>
    [Reactive] public bool AutoShuffleEnabled { get; private set; }

    [Reactive] public RepeatMode RepeatMode { get; set; }
    [Reactive] public bool IsRepeatHintVisible { get; private set; }
    [Reactive] public string RepeatHintText { get; private set; } = "";

    /// <summary>
    /// Подсказка для shuffle (показывается при toggle auto-shuffle).
    /// </summary>
    [Reactive] public bool IsShuffleHintVisible { get; private set; }
    [Reactive] public string ShuffleHintText { get; private set; } = "";

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

    public string ShuffleTooltip => AutoShuffleEnabled
        ? SL["Player_Shuffle_AutoEnabled"] 
        : SL["Player_Shuffle_AutoDisabled"];

    public static string PreviousTooltip => SL["Player_Previous"];
    public static string NextTooltip => SL["Player_Next"];

    public string PlayPauseTooltip => IsPlaying
        ? SL["Player_Pause"]
        : SL["Player_Play"];

    public string RepeatTooltip => RepeatMode switch
    {
        RepeatMode.None => SL["Player_Repeat_Off"],
        RepeatMode.All => SL["Player_Repeat_All"],
        RepeatMode.One => SL["Player_Repeat_One"],
        _ => ""
    };

    public string LikeTooltip => IsLiked
        ? SL["Track_Unlike"]
        : SL["Track_Like"];

    public static string CopyTooltip => SL["Track_CopyLink"];

    public string MuteTooltip => IsMuted
        ? SL["Player_Unmute"]
        : SL["Player_Mute"];

    public string TrackNumberTooltip => string.Format(
        SL["Player_TrackNumber"],
        CurrentTrackIndex + 1,
        TotalTracksInQueue);

    public string DurationTooltip
    {
        get
        {
            if (IsLoading || DurationSeconds <= 0)
                return SL["Player_Loading_Duration"];

            return string.Format(
                SL["Player_Duration"],
                FormatTime(Position),
                FormatTime(Duration));
        }
    }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    
    /// <summary>
    /// ЛКМ: однократное перемешивание очереди.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; }
    
    /// <summary>
    /// ПКМ: toggle auto-shuffle (сохраняется в настройках).
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleAutoShuffleCommand { get; }
    
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
        YoutubeProvider youtube,
        MusicLibraryManager musicManager)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _musicManager = musicManager;

        Log.Debug("[PlayerBar] Created, initializing settings...");

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // ═══ ИСПРАВЛЕНИЕ БАГА ВОССТАНОВЛЕНИЯ НАСТРОЕК ═══
        // Вызываем инициализацию немедленно, так как LibraryService
        // уже загрузил настройки при старте приложения.
        // Раньше мы ждали события OnInitialized, которое мы пропускали (Race Condition).
        OnLibraryInitialized();

        // AUDIO ENGINE EVENTS
        Observable.FromEvent<Action<bool, bool>, (bool Playing, bool Paused)>(
                h => (p, u) => h((p, u)),
                h => _audio.OnPlaybackStateChanged += h,
                h => _audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                SyncPlaybackState(state.Playing, state.Paused);
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
               h => _audio.OnSeekCompleted += h,
               h => _audio.OnSeekCompleted -= h)
           .ObserveOn(RxApp.MainThreadScheduler)
           .Subscribe(pos =>
           {
               Volatile.Write(ref _seekBusyStartTicks, 0L);

               if (!_isSuspended)
               {
                   PositionSeconds = pos.TotalSeconds;
                   Position = pos;
                   ForceUpdateBufferProgress();
               }

               IsSeekBusy = false;
           })
           .DisposeWith(Disposables);

        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
                 h => _audio.OnPositionChanged += h,
                 h => _audio.OnPositionChanged -= h)
             .Where(_ => !_isSuspended && !_isSeeking && !IsTrackResetting && !IsSeekBusy)
             .Throttle(TimeSpan.FromMilliseconds(PositionUpdateThrottleMs))
             .DistinctUntilChanged(pos => (long)(pos.TotalMilliseconds / 100))
             .ObserveOn(RxApp.MainThreadScheduler)
             .Subscribe(pos =>
             {
                 if (_isSeeking || _isSuspended || IsTrackResetting || IsSeekBusy) return;

                 Position = pos;
                 PositionSeconds = pos.TotalSeconds;
                 this.RaisePropertyChanged(nameof(DurationTooltip));
             })
             .DisposeWith(Disposables);

        Observable.FromEvent<Action<int>, int>(
                h => _audio.OnMaxVolumeChanged += h,
                h => _audio.OnMaxVolumeChanged -= h)
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleMaxVolumeChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => _audio.OnTrackChanged += h,
                h => _audio.OnTrackChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleTrackChanged)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<AudioStreamInfo>, AudioStreamInfo>(
                h => _audio.OnStreamInfoChanged += h,
                h => _audio.OnStreamInfoChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateStreamInfo)
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<BufferState>, BufferState>(
                h => _audio.OnBufferStateChanged += h,
                h => _audio.OnBufferStateChanged -= h)
            .Where(_ => !_isSuspended)
            .Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleBufferStateChanged)
            .DisposeWith(Disposables);

        var cacheManager = AudioSourceFactory.GlobalCache ?? throw new NullReferenceException("AudioSourceFactory.GlobalCache is not initialized");
        Observable.FromEvent<Action<string, string, int, bool>, (string TrackId, string Container, int Bitrate, bool Downloaded)>(
                h => (t, c, b, d) => h((t, c, b, d)),
                h => cacheManager.OnFormatCached += h,
                h => cacheManager.OnFormatCached -= h)
            .Subscribe(x => OnFormatCached(x.TrackId, x.Container, x.Bitrate, x.Downloaded))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<TrackInfo>, TrackInfo>(
                h => _library.OnTrackUpdated += h,
                h => _library.OnTrackUpdated -= h)
            .Where(t => CurrentTrack != null && t.Id == CurrentTrack.Id)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                IsLiked = t.IsLiked;
                if (CurrentTrack != null)
                    CurrentTrack.IsLiked = t.IsLiked;
                this.RaisePropertyChanged(nameof(LikeTooltip));
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                RaiseVolumePropertiesChanged();

                if (_isInitialized && v > 0)
                    _library.UpdateSettings(s => s.LastVolume = v);
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.RepeatMode)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(RepeatTooltip)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.IsLiked)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(LikeTooltip)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.CurrentTrackIndex, x => x.TotalTracksInQueue)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(TrackNumberTooltip)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.AutoShuffleEnabled)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShuffleTooltip)))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool>, bool>(
                h => _audio.OnLoadingStateChanged += h,
                h => _audio.OnLoadingStateChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                IsLoading = loading;
                IsSeekBusy = loading;
                if (!_isSuspended)
                    this.RaisePropertyChanged(nameof(DurationTooltip));
            })
            .DisposeWith(Disposables);

        // TIMERS
        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FallbackPositionIntervalMs) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();

        // NAVIGATION SUBJECTS
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

        // COMMANDS
        var canNavigate = this.WhenAnyValue(
            x => x.HasTrack, x => x.IsNavigating, x => x.IsLoading,
            (hasTrack, isNav, loading) => hasTrack && !isNav && !loading);

        PlayPauseCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }, this.WhenAnyValue(x => x.HasTrack)));

        NextCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsNavigating = true;
            _nextSubject.OnNext(Unit.Default);
        }, canNavigate));

        PreviousCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            IsNavigating = true;
            _prevSubject.OnNext(Unit.Default);
        }, canNavigate));

        var canShuffle = this.WhenAnyValue(
            x => x.HasQueueToShuffle, x => x.IsLoading,
            (hasTracks, loading) => hasTracks && !loading);

        // ЛКМ: Однократное перемешивание с визуальной анимацией
        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _audio.ShuffleQueue();
            
            // Визуальная анимация (кратковременная подсветка)
            IsShuffleAnimating = true;
            Observable.Timer(TimeSpan.FromMilliseconds(ShuffleAnimationDurationMs))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => IsShuffleAnimating = false)
                .DisposeWith(Disposables);
        }, canShuffle));

        // ПКМ: Toggle auto-shuffle (сохраняется в настройках)
        ToggleAutoShuffleCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            AutoShuffleEnabled = !AutoShuffleEnabled;
            _library.UpdateSettings(s => s.ShuffleEnabled = AutoShuffleEnabled);
            _audio.ShuffleEnabled = AutoShuffleEnabled;
            
            ShowShuffleHint();
        }));

        ToggleRepeatCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.None => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.None
            };
            _audio.RepeatMode = RepeatMode;
            _library.UpdateSettings(s => s.RepeatMode = RepeatMode);
            ShowRepeatModeHint();
        }));

        ToggleMuteCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            if (IsMuted)
            {
                int restoreVolume = Math.Min(
                    _lastVolumeBeforeMute > 0 ? _lastVolumeBeforeMute : 50,
                    MaxVolume);
                Volume = restoreVolume;
            }
            else
            {
                _lastVolumeBeforeMute = Volume;
                _library.UpdateSettings(s => s.LastVolume = Volume);
                Volume = 0;
            }
            OnVolumeChangeComplete();
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
                await Clipboard.SetTextAsync(CurrentTrack.Url);
                ShowCopyHint();
            }
        }, this.WhenAnyValue(x => x.HasTrack)));

        LoadFormatsCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadFormatsAsync));

        SwitchFormatCommand = CreateCommand(ReactiveCommand.CreateFromTask<StreamOption>(async option =>
        {
            if (option == null) return;
            foreach (var f in AvailableFormats) f.IsActive = false;
            option.IsActive = true;
            BeginTrackReset();
            await _audio.SwitchQualityAsync(option.Container, (int)option.Bitrate);
        }));
    }

    internal void RegisterView(PlayerBarView view)
    {
        _viewRef = new WeakReference<PlayerBarView>(view);
    }

    #endregion

    #region Buffer Progress

    private void HandleBufferStateChanged(BufferState state)
    {
        if (IsTrackResetting)
            return;

        if (CurrentTrack?.IsDownloaded == true)
        {
            if (!IsFullyBuffered)
            {
                BufferProgressPercent = 100;
                BufferedRanges = [(0.0, 1.0)];
                IsFullyBuffered = true;
                this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
            }
            return;
        }

        BufferProgressPercent = state.Progress;
        IsFullyBuffered = state.IsFullyBuffered;
        BufferedRanges = state.Ranges;
        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    private void ForceUpdateBufferProgress()
    {
        if (!HasTrack || CurrentTrack == null) return;
        if (IsTrackResetting) return;

        if (CurrentTrack.IsDownloaded)
        {
            BufferProgressPercent = 100;
            BufferedRanges = [(0.0, 1.0)];
            IsFullyBuffered = true;
        }
        else
        {
            BufferProgressPercent = _audio.BufferProgress;
            BufferedRanges = _audio.GetBufferedRanges();
            IsFullyBuffered = _audio.IsFullyBuffered;
        }

        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    #endregion

    #region Track Reset Visual

    private void BeginTrackReset()
    {
        int session = Interlocked.Increment(ref _trackResetSession);
        _trackResetStartTime = DateTime.UtcNow;

        IsTrackResetting = true;

        BufferProgressPercent = 0;
        BufferedRanges = [];
        IsFullyBuffered = false;
        Position = TimeSpan.Zero;
        PositionSeconds = 0;
        IsSeekBusy = true;

        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
        this.RaisePropertyChanged(nameof(BufferedRanges));

        Log.Debug($"[PlayerBar] BeginTrackReset: session={session}");
    }

    private async void EndTrackReset(int session)
    {
        var elapsed = DateTime.UtcNow - _trackResetStartTime;
        int remaining = TrackResetMinDurationMs - (int)elapsed.TotalMilliseconds;
        if (remaining > 0)
            await Task.Delay(remaining);

        int currentSession = Volatile.Read(ref _trackResetSession);
        if (currentSession != session)
        {
            Log.Debug($"[PlayerBar] EndTrackReset skipped: session {session} != {currentSession}");
            return;
        }

        IsTrackResetting = false;
        Log.Debug($"[PlayerBar] EndTrackReset: session={session}");
    }

    #endregion

    #region Initialization

    private void OnLibraryInitialized()
    {
        var settings = _library.Settings;

        int newMax = Math.Max(settings.MaxVolumeLimit, 100);
        MaxVolume = newMax;

        int savedVolume = settings.LastVolume;
        if (savedVolume > 0 && savedVolume <= MaxVolume)
        {
            Volume = savedVolume;
            _lastVolumeBeforeMute = savedVolume;
        }
        else if (savedVolume > MaxVolume)
        {
            Volume = MaxVolume;
            _lastVolumeBeforeMute = MaxVolume;
        }
        else
        {
            Volume = 50;
            _lastVolumeBeforeMute = 50;
        }

        // Синхронизируем состояние визуальных кнопок с базой данных
        AutoShuffleEnabled = settings.ShuffleEnabled;
        RepeatMode = settings.RepeatMode;
        
        // Синхронизируем движок
        _audio.ShuffleEnabled = AutoShuffleEnabled;
        _audio.RepeatMode = RepeatMode;
        _audio.SetVolumeInstant(Volume);

        _isInitialized = true;
        RaiseVolumePropertiesChanged();
        UpdateQueueState();

        Log.Info($"[PlayerBar] Initialized: Vol={Volume}, MaxVol={MaxVolume}, AutoShuffle={AutoShuffleEnabled}, Repeat={RepeatMode}");
    }

    #endregion

    #region Private Handlers

    private void HandleMaxVolumeChanged(int newMax)
    {
        if (MaxVolume == newMax) return;

        int oldMax = MaxVolume;
        MaxVolume = newMax;

        if (Volume > MaxVolume)
            Volume = MaxVolume;

        if (_lastVolumeBeforeMute > MaxVolume)
            _lastVolumeBeforeMute = MaxVolume;

        RaiseVolumePropertiesChanged();
        Log.Info($"[PlayerBar] MaxVolume changed: {oldMax} -> {newMax}");
    }

    private void HandleTrackChanged(TrackInfo? track)
    {
        CurrentTrack = track;
        HasTrack = track != null;

        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(SafeAuthor));
        this.RaisePropertyChanged(nameof(SafeThumbnail));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));

        _lastDownloadedBytes = 0;
        AvailableFormats.Clear();

        if (track != null)
        {
            _pendingStreamInfoTrackId = track.Id;

            BeginTrackReset();

            Duration = track.Duration;
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;

            var storedTrack = _library.GetTrack(track.Id);
            IsLiked = storedTrack?.IsLiked ?? track.IsLiked;

            if (track.IsDownloaded)
            {
                BufferProgressPercent = 100;
                BufferedRanges = [(0.0, 1.0)];
                IsFullyBuffered = true;
            }

            ShowStreamInfo = true;
            StreamInfo = SL["Player_StreamInfo_Loading"];
        }
        else
        {
            _pendingStreamInfoTrackId = null;
            Duration = TimeSpan.Zero;
            DurationSeconds = 1;
            ShowStreamInfo = false;
            StreamInfo = "";
            IsLiked = false;
            IsTrackResetting = false;

            BufferProgressPercent = 0;
            BufferedRanges = [];
            IsFullyBuffered = false;
            Position = TimeSpan.Zero;
            PositionSeconds = 0;
        }

        this.RaisePropertyChanged(nameof(DurationTooltip));
        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
        UpdateQueueState();
    }

    private void UpdateQueueState()
    {
        var queue = _audio.Queue;
        TotalTracksInQueue = queue.Count;
        HasQueueToShuffle = queue.Count > 1;

        if (CurrentTrack != null)
        {
            CurrentTrackIndex = -1;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].Id == CurrentTrack.Id)
                {
                    CurrentTrackIndex = i;
                    break;
                }
            }
            if (CurrentTrackIndex < 0) CurrentTrackIndex = 0;
        }
        else
        {
            CurrentTrackIndex = 0;
        }

        this.RaisePropertyChanged(nameof(CurrentTrackIndexDisplay));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
    }

    private void SyncPlaybackState(bool isPlaying, bool isPaused)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
    }

    private void UpdateStreamInfo(AudioStreamInfo info)
    {
        if (CurrentTrack == null)
        {
            ShowStreamInfo = false;
            StreamInfo = "";
            return;
        }

        if (info.IsValid)
        {
            StreamInfo = info.FormatDisplay;

            if (!_isSuspended)
            {
                Duration = TimeSpan.FromMilliseconds(info.DurationMs);
                DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;
                this.RaisePropertyChanged(nameof(DurationTooltip));
            }

            foreach (var f in AvailableFormats)
            {
                f.IsActive = string.Equals(f.Codec, info.Codec, StringComparison.OrdinalIgnoreCase) &&
                             (int)f.Bitrate == info.Bitrate;
            }

            if (IsTrackResetting)
            {
                bool isForCurrentTrack = !string.IsNullOrEmpty(info.TrackId) &&
                                         CurrentTrack.Id == info.TrackId;

                if (!isForCurrentTrack && string.IsNullOrEmpty(info.TrackId))
                    isForCurrentTrack = _pendingStreamInfoTrackId == CurrentTrack.Id;

                if (isForCurrentTrack)
                {
                    _pendingStreamInfoTrackId = null;
                    int session = Volatile.Read(ref _trackResetSession);
                    EndTrackReset(session);
                }
            }
        }
        else
        {
            StreamInfo = SL["Player_StreamInfo_Loading"];
        }

        ShowStreamInfo = true;
    }

    private void UpdateDownloadSpeed()
    {
        if (_isSuspended) return;

        if (!HasTrack || CurrentTrack?.IsDownloaded == true || IsFullyBuffered)
        {
            if (DownloadSpeedText.Length > 0)
                DownloadSpeedText = "";
            return;
        }

        var currentBytes = _audio.GetDownloadedBytes();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedCheck).TotalSeconds;

        if (elapsed >= 0.5 && _lastSpeedCheck != DateTime.MinValue)
        {
            var kbs = (currentBytes - _lastDownloadedBytes) / elapsed / 1024.0;
            DownloadSpeedText = kbs > 10
                ? (kbs >= 1024
                    ? string.Format(SL["Stream_Speed_Mb"] ?? "{0:F1} MB/s", kbs / 1024)
                    : string.Format(SL["Stream_Speed_Kb"] ?? "{0:F0} KB/s", kbs))
                : "";
        }

        _lastDownloadedBytes = currentBytes;
        _lastSpeedCheck = now;
    }

    private void FallbackPositionUpdate()
    {
        if (!HasTrack || _isSuspended || IsTrackResetting)
            return;

        var realDur = _audio.TotalDuration;
        if (Math.Abs(DurationSeconds - realDur.TotalSeconds) > 1 && realDur.TotalSeconds > 0)
        {
            Duration = realDur;
            DurationSeconds = Duration.TotalSeconds;
            this.RaisePropertyChanged(nameof(DurationTooltip));
        }

        if (IsSeekBusy)
        {
            long busyTicks = Volatile.Read(ref _seekBusyStartTicks);
            if (busyTicks > 0 &&
                (DateTime.UtcNow.Ticks - busyTicks) > TimeSpan.FromSeconds(2).Ticks)
            {
                Log.Debug("[PlayerBar] Seek busy timeout — clearing guard");
                IsSeekBusy = false;
                Volatile.Write(ref _seekBusyStartTicks, 0L);

                var realPos = _audio.CurrentPosition;
                Position = realPos;
                PositionSeconds = realPos.TotalSeconds;
            }

            return;
        }

        if (_isSeeking)
            return;

        if (IsPlaying)
        {
            var pos = _audio.CurrentPosition;
            Position = pos;
            PositionSeconds = pos.TotalSeconds;
        }
    }

    #endregion

    #region Public Interaction

    public void StartSeek()
    {
        _isSeeking = true;
    }

    public void UpdateSeekPosition(double seconds)
    {
        if (!_isSeeking) return;

        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = seconds;
        Position = TimeSpan.FromSeconds(seconds);
        this.RaisePropertyChanged(nameof(DurationTooltip));
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

        PositionSeconds = target;
        Position = TimeSpan.FromSeconds(target);

        IsSeekBusy = true;
        Volatile.Write(ref _seekBusyStartTicks, DateTime.UtcNow.Ticks);

        try
        {
            await _audio.SeekAsync(TimeSpan.FromSeconds(target));
        }
        catch (OperationCanceledException)
        {
            IsSeekBusy = false;
            Volatile.Write(ref _seekBusyStartTicks, 0L);
            Log.Debug("[PlayerBar] Seek cancelled, guards cleared");
        }
        catch (Exception ex)
        {
            IsSeekBusy = false;
            Volatile.Write(ref _seekBusyStartTicks, 0L);

            var realPos = _audio.CurrentPosition;
            Position = realPos;
            PositionSeconds = realPos.TotalSeconds;
            Log.Warn($"[PlayerBar] Seek failed: {ex.Message}");
        }
    }

    public void CancelSeek()
    {
        _isSeeking = false;

        IsSeekBusy = false;
        Volatile.Write(ref _seekBusyStartTicks, 0L);

        var realPos = _audio.CurrentPosition;
        Position = realPos;
        PositionSeconds = realPos.TotalSeconds;
    }

    public void OnVolumeChangeComplete()
    {
        _audio.SaveVolumeNow();
    }

    public int GetVolumeScrollStep()
    {
        if (MaxVolume <= 100) return 1;
        return Math.Max(1, MaxVolume / 200);
    }

    #endregion

    #region Volume Helpers

    private void RaiseVolumePropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsVolumeLow));
        this.RaisePropertyChanged(nameof(IsVolumeMedium));
        this.RaisePropertyChanged(nameof(IsVolumeHigh));
        this.RaisePropertyChanged(nameof(IsVolumeBoosted));
        this.RaisePropertyChanged(nameof(IsReallyBoosted));
        this.RaisePropertyChanged(nameof(VolumePercentBrush));
        this.RaisePropertyChanged(nameof(MuteTooltip));
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

            var cache = AudioSourceFactory.GlobalCache;
            var cachedFormats = cache?.GetCachedFormats(CurrentTrack.Id) ?? [];

            AvailableFormats.Clear();

            foreach (var f in formats)
            {
                f.IsDownloaded = cachedFormats.Any(cached =>
                    string.Equals(f.Container, cached.Container, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs((int)f.Bitrate - cached.Bitrate) <= 10);

                f.IsActive = string.Equals(f.Codec, currentFormat, StringComparison.OrdinalIgnoreCase) &&
                             Math.Abs((int)f.Bitrate - currentBitrate) <= 10;

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
        if (CurrentTrack == null || CurrentTrack.Id != trackId) return;

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
            _ = LoadFormatsAsync();
    }

    #endregion

    #region Hint Methods

    private async void ShowRepeatModeHint()
    {
        RepeatHintText = RepeatMode switch
        {
            RepeatMode.None => SL["Player_Repeat_Off"],
            RepeatMode.All => SL["Player_Repeat_All"],
            RepeatMode.One => SL["Player_Repeat_One"],
            _ => ""
        };
        IsRepeatHintVisible = true;
        await Task.Delay(HintDisplayDurationMs);
        IsRepeatHintVisible = false;
    }

    private async void ShowShuffleHint()
    {
        ShuffleHintText = AutoShuffleEnabled
            ? SL["Player_Shuffle_AutoOn"]
            : SL["Player_Shuffle_AutoOff"];
        IsShuffleHintVisible = true;
        await Task.Delay(HintDisplayDurationMs);
        IsShuffleHintVisible = false;
    }

    private async void ShowLikeHint()
    {
        LikeHintText = IsLiked ? SL["Track_Added"] : SL["Track_Removed"];
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

    #region Language

    private void OnLanguageChanged(object? sender, string newLang)
    {
        this.RaisePropertyChanged(nameof(ShuffleTooltip));
        this.RaisePropertyChanged(nameof(PreviousTooltip));
        this.RaisePropertyChanged(nameof(NextTooltip));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));
        this.RaisePropertyChanged(nameof(RepeatTooltip));
        this.RaisePropertyChanged(nameof(LikeTooltip));
        this.RaisePropertyChanged(nameof(CopyTooltip));
        this.RaisePropertyChanged(nameof(MuteTooltip));
        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
        this.RaisePropertyChanged(nameof(DurationTooltip));
        
        // Обновляем XAML биндинги
        this.RaisePropertyChanged(nameof(L));
    }

    #endregion

    #region Helpers

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");

    #endregion

    #region LifeCycle

    protected override void OnSuspend()
    {
        _isSuspended = true;

        _fallbackPositionTimer.Stop();
        _speedUpdateTimer.Stop();

        DownloadSpeedText = "";

        if (_viewRef?.TryGetTarget(out var view) == true)
            view.OnSuspend();
    }

    protected override void OnResume()
    {
        _isSuspended = false;

        _fallbackPositionTimer.Start();
        _speedUpdateTimer.Start();

        var realDur = _audio.TotalDuration;
        if (realDur.TotalSeconds > 0)
        {
            Duration = realDur;
            DurationSeconds = Duration.TotalSeconds;

            this.RaisePropertyChanged(nameof(DurationTooltip));
        }

        FallbackPositionUpdate();
        ForceUpdateBufferProgress();

        if (_viewRef?.TryGetTarget(out var view) == true)
            view.OnResume();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            if (_isInitialized && Volume > 0)
                _library.UpdateSettings(s => s.LastVolume = Volume);

            _audio.SaveVolumeNow();
            _fallbackPositionTimer.Stop();
            _speedUpdateTimer.Stop();
        }
        base.Dispose(disposing);
    }

    #endregion
}