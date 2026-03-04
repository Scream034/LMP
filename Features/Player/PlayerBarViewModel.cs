using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Media;
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
///
/// <para><b>Suspend/Resume архитектура (event-based):</b></para>
/// <list type="bullet">
///   <item>При Suspend: тяжёлые подписки (позиция, буфер, скорость) ОТПИСЫВАЮТСЯ через dispose</item>
///   <item>При Resume: подписки ПЕРЕСОЗДАЮТСЯ, ForceSync восстанавливает состояние</item>
///   <item>Критичные свойства (IsPlaying, CurrentTrack, RepeatMode) обновляются ВСЕГДА</item>
/// </list>
///
/// <para><b>Volume Popup при suspend:</b></para>
/// <para>View вызывает RequestResume() через PlayerControlService,
/// что триггерит полный Resume (BroadcastResume) из MainWindow.</para>
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
    private const int SpeedUpdateIntervalMs = 1000;
    private const int SeekBusyTimeoutMs = 2000;

    #endregion

    #region Fields

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly MusicLibraryManager _musicManager;
    private readonly PlayerControlService _playerControl;

    private readonly Subject<Unit> _nextSubject = new();
    private readonly Subject<Unit> _prevSubject = new();

    /// <summary>
    /// Подписки на тяжёлые события AudioEngine. Создаются при Resume, dispose при Suspend.
    /// Это ключевой механизм: вместо флагов и .Where() фильтров мы просто отписываемся.
    /// </summary>
    private CompositeDisposable? _heavySubscriptions;

    private bool _isSeeking;
    private bool _isInitialized;

    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;
    private int _lastVolumeBeforeMute = 50;

    private int _trackResetSession;
    private DateTime _trackResetStartTime;
    private string? _pendingStreamInfoTrackId;
    private long _seekBusyStartTicks;

    /// <summary>
    /// Id последнего обработанного трека. Предотвращает повторный BeginTrackReset
    /// при ForceSync когда трек не менялся.
    /// </summary>
    private string? _lastHandledTrackId;

    /// <summary>
    /// Последний валидный StreamInfo текст. Кэшируется для восстановления при ForceSync.
    /// </summary>
    private string _lastValidStreamInfo = "";

    /// <summary>
    /// Кэшированный результат GetEffectivePercent для избежания повторных вычислений.
    /// Обновляется при изменении Volume или MaxVolume.
    /// </summary>
    private int _cachedEffectivePercent;

    /// <summary>
    /// CTS для текущего активного hint. При показе нового — предыдущий отменяется,
    /// предотвращая гонку состояний при быстрых кликах.
    /// </summary>
    private CancellationTokenSource? _activeHintCts;

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
    public bool IsVolumeLow => Volume >= 1 && !IsReallyBoosted && _cachedEffectivePercent <= 33;
    public bool IsVolumeMedium => !IsMuted && !IsReallyBoosted && _cachedEffectivePercent > 33 && _cachedEffectivePercent <= 66;
    public bool IsVolumeHigh => !IsMuted && !IsReallyBoosted && _cachedEffectivePercent > 66;
    public bool IsVolumeBoosted => IsReallyBoosted;

    /// <summary>
    /// Вычисляет эффективный процент громкости с учётом VolumeBoost.
    /// Результат кэшируется в <see cref="_cachedEffectivePercent"/>.
    /// </summary>
    private void RecalcEffectivePercent()
    {
        var settings = _library.Settings.Audio;

        if (settings.VolumeBoostEnabled)
        {
            _cachedEffectivePercent = Volume <= AudioEngine.VolumeNormalRange
                ? (int)(Volume / 2.0)
                : 100;
        }
        else
        {
            int maxVol = MaxVolume > 0 ? MaxVolume : 100;
            _cachedEffectivePercent = (int)((double)Volume / maxVol * 100);
        }
    }

    public IBrush VolumePercentBrush
    {
        get
        {
            var app = Application.Current;
            if (app == null) return Brushes.White;

            string resourceKey = IsReallyBoosted ? "SystemWarnOrangeBrush" : "TextPrimaryBrush";
            if (app.Resources.TryGetResource(resourceKey, app.ActualThemeVariant, out var brush) && brush is IBrush b)
                return b;

            return IsReallyBoosted
                ? new SolidColorBrush(Color.Parse("#FFB86C"))
                : Brushes.White;
        }
    }

    #endregion

    #region Properties - Repeat & Shuffle

    [Reactive] public bool IsShuffleAnimating { get; private set; }
    [Reactive] public bool AutoShuffleEnabled { get; private set; }
    [Reactive] public RepeatMode RepeatMode { get; set; }

    #endregion

    #region Properties - Hints (unified)

    [Reactive] public bool IsRepeatHintVisible { get; private set; }
    [Reactive] public string RepeatHintText { get; private set; } = "";
    [Reactive] public bool IsShuffleHintVisible { get; private set; }
    [Reactive] public string ShuffleHintText { get; private set; } = "";
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

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NextCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ShuffleQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAutoShuffleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> LoadFormatsCommand { get; private set; } = null!;
    public ReactiveCommand<StreamOption, Unit> SwitchFormatCommand { get; private set; } = null!;

    #endregion

    #region Events for View

    /// <summary>
    /// Событие запроса suspend для View. View подписывается на него
    /// вместо прямого вызова через WeakReference.
    /// </summary>
    public event Action? SuspendRequested;

    /// <summary>
    /// Событие запроса resume для View.
    /// </summary>
    public event Action? ResumeRequested;

    #endregion

    #region Constructor

    public PlayerBarViewModel(
        AudioEngine audio,
        LibraryService library,
        YoutubeProvider youtube,
        MusicLibraryManager musicManager,
        PlayerControlService playerControl)
    {
        _audio = audio;
        _library = library;
        _youtube = youtube;
        _musicManager = musicManager;
        _playerControl = playerControl;

        Log.Debug("[PlayerBar] Created, initializing...");

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        InitializeFromSettings();
        SetupCommands();
        SubscribeLightweight();
        SubscribeHeavy();

        Log.Info("[PlayerBar] Initialization complete");
    }

    #endregion

    #region Initialization

    private void InitializeFromSettings()
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

        AutoShuffleEnabled = _playerControl.ShuffleEnabled;
        RepeatMode = _playerControl.RepeatMode;

        _audio.SetVolumeInstant(Volume);
        RecalcEffectivePercent();

        _isInitialized = true;
        RaiseVolumePropertiesChanged();
        UpdateQueueState();

        Log.Info($"[PlayerBar] Initialized: Vol={Volume}, MaxVol={MaxVolume}, AutoShuffle={AutoShuffleEnabled}, Repeat={RepeatMode}");
    }

    private void SetupCommands()
    {
        var canNavigate = this.WhenAnyValue(
            x => x.HasTrack, x => x.IsNavigating, x => x.IsLoading,
            (hasTrack, isNav, loading) => hasTrack && !isNav && !loading);

        var canShuffle = this.WhenAnyValue(
            x => x.HasQueueToShuffle, x => x.IsLoading,
            (hasTracks, loading) => hasTracks && !loading);

        var hasTrackObs = this.WhenAnyValue(x => x.HasTrack);

        PlayPauseCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            _playerControl.PlayPauseAsync, hasTrackObs));

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

        ShuffleQueueCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _playerControl.ShuffleQueue();
            IsShuffleAnimating = true;
            Observable.Timer(TimeSpan.FromMilliseconds(ShuffleAnimationDurationMs))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => IsShuffleAnimating = false)
                .DisposeWith(Disposables);
        }, canShuffle));

        ToggleAutoShuffleCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _playerControl.ToggleAutoShuffle();
            ShowHint(
                v => IsShuffleHintVisible = v,
                () => ShuffleHintText = AutoShuffleEnabled
                    ? SL["Player_Shuffle_AutoOn"]
                    : SL["Player_Shuffle_AutoOff"]);
        }));

        ToggleRepeatCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _playerControl.ToggleRepeat();
            ShowHint(
                v => IsRepeatHintVisible = v,
                () => RepeatHintText = RepeatMode switch
                {
                    RepeatMode.None => SL["Player_Repeat_Off"],
                    RepeatMode.All => SL["Player_Repeat_All"],
                    RepeatMode.One => SL["Player_Repeat_One"],
                    _ => ""
                });
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
                ShowHint(
                    v => IsLikeHintVisible = v,
                    () => LikeHintText = IsLiked ? SL["Track_Added"] : SL["Track_Removed"]);
            }
        }, hasTrackObs));

        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack?.Url != null)
            {
                await Clipboard.SetTextAsync(CurrentTrack.Url);
                ShowCopyHint();
            }
        }, hasTrackObs));

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

    /// <summary>
    /// Подписки на легковесные события, которые работают ВСЕГДА (даже в suspend).
    /// Это состояние воспроизведения, текущий трек, repeat/shuffle, очередь.
    /// </summary>
    private void SubscribeLightweight()
    {
        _playerControl.PlaybackStateObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                IsPlaying = state.IsPlaying;
                IsPaused = state.IsPaused;
                this.RaisePropertyChanged(nameof(PlayPauseTooltip));
            })
            .DisposeWith(Disposables);

        _playerControl.CurrentTrackObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleTrackChanged)
            .DisposeWith(Disposables);

        _playerControl.RepeatModeObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(mode =>
            {
                RepeatMode = mode;
                this.RaisePropertyChanged(nameof(RepeatTooltip));
            })
            .DisposeWith(Disposables);

        _playerControl.ShuffleEnabledObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(enabled =>
            {
                AutoShuffleEnabled = enabled;
                this.RaisePropertyChanged(nameof(ShuffleTooltip));
            })
            .DisposeWith(Disposables);

        _playerControl.IsLoadingObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(loading =>
            {
                IsLoading = loading;
                IsSeekBusy = loading;
            })
            .DisposeWith(Disposables);

        _playerControl.QueueCountObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateQueueState())
            .DisposeWith(Disposables);

        _playerControl.ForceSyncObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => HandleForceSync())
            .DisposeWith(Disposables);

        // Volume → engine + settings + UI
        this.WhenAnyValue(x => x.Volume)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                RecalcEffectivePercent();
                RaiseVolumePropertiesChanged();

                if (_isInitialized && v > 0)
                    _library.UpdateSettings(s => s.LastVolume = v);
            })
            .DisposeWith(Disposables);

        // MaxVolume change from AudioEngine
        Observable.FromEvent<Action<int>, int>(
                h => _audio.OnMaxVolumeChanged += h,
                h => _audio.OnMaxVolumeChanged -= h)
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleMaxVolumeChanged)
            .DisposeWith(Disposables);

        // Track updated in library (like status)
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

        // Format cached notification
        var cacheManager = AudioSourceFactory.GlobalCache
            ?? throw new NullReferenceException("AudioSourceFactory.GlobalCache is not initialized");
        Observable.FromEvent<Action<string, string, int, bool>, (string TrackId, string Container, int Bitrate, bool Downloaded)>(
                h => (t, c, b, d) => h((t, c, b, d)),
                h => cacheManager.OnFormatCached += h,
                h => cacheManager.OnFormatCached -= h)
            .Subscribe(x => OnFormatCached(x.TrackId, x.Container, x.Bitrate, x.Downloaded))
            .DisposeWith(Disposables);

        // Queue index tooltip
        this.WhenAnyValue(x => x.CurrentTrackIndex, x => x.TotalTracksInQueue)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(TrackNumberTooltip)))
            .DisposeWith(Disposables);

        // Navigation subjects
        _nextSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _playerControl.NextAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);

        _prevSubject
            .Throttle(TimeSpan.FromMilliseconds(NavigationDebounceMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ =>
            {
                try { await _playerControl.PreviousAsync(); }
                finally { IsNavigating = false; }
            })
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// Подписки на тяжёлые события AudioEngine: позиция, буфер, StreamInfo, скорость.
    /// <para>
    /// При Suspend — dispose через <see cref="_heavySubscriptions"/>.
    /// При Resume — пересоздаются заново.
    /// Это исключает необходимость в флагах <c>_isSuspended</c>/<c>_isWindowActive</c>
    /// и фильтрации <c>.Where(_ => !_isSuspended)</c> — события просто не доходят.
    /// </para>
    /// </summary>
    private void SubscribeHeavy()
    {
        _heavySubscriptions?.Dispose();
        _heavySubscriptions = new CompositeDisposable();

        // Position updates
        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
                h => _audio.OnPositionChanged += h,
                h => _audio.OnPositionChanged -= h)
            .Where(_ => !_isSeeking && !IsTrackResetting && !IsSeekBusy)
            .Throttle(TimeSpan.FromMilliseconds(PositionUpdateThrottleMs))
            .DistinctUntilChanged(pos => (long)(pos.TotalMilliseconds / 100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos =>
            {
                if (_isSeeking || IsTrackResetting || IsSeekBusy) return;
                Position = pos;
                PositionSeconds = pos.TotalSeconds;
                this.RaisePropertyChanged(nameof(DurationTooltip));
            })
            .DisposeWith(_heavySubscriptions);

        // Seek completed
        Observable.FromEvent<Action<TimeSpan>, TimeSpan>(
                h => _audio.OnSeekCompleted += h,
                h => _audio.OnSeekCompleted -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos =>
            {
                Volatile.Write(ref _seekBusyStartTicks, 0L);
                PositionSeconds = pos.TotalSeconds;
                Position = pos;
                SyncBufferState();
                IsSeekBusy = false;
            })
            .DisposeWith(_heavySubscriptions);

        // Buffer state
        Observable.FromEvent<Action<BufferState>, BufferState>(
                h => _audio.OnBufferStateChanged += h,
                h => _audio.OnBufferStateChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(HandleBufferStateChanged)
            .DisposeWith(_heavySubscriptions);

        // Stream info
        Observable.FromEvent<Action<AudioStreamInfo>, AudioStreamInfo>(
                h => _audio.OnStreamInfoChanged += h,
                h => _audio.OnStreamInfoChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateStreamInfo)
            .DisposeWith(_heavySubscriptions);

        // Fallback position timer (Rx interval instead of DispatcherTimer)
        Observable.Interval(TimeSpan.FromMilliseconds(FallbackPositionIntervalMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FallbackPositionUpdate())
            .DisposeWith(_heavySubscriptions);

        // Speed update timer (Rx interval instead of DispatcherTimer)
        Observable.Interval(TimeSpan.FromMilliseconds(SpeedUpdateIntervalMs))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateDownloadSpeed())
            .DisposeWith(_heavySubscriptions);
    }

    #endregion

    #region Unified Hint System

    /// <summary>
    /// Показывает временный hint с автоматическим скрытием.
    /// Отменяет предыдущий hint если он ещё отображается (предотвращает гонку).
    /// </summary>
    /// <param name="setVisible">Setter для IsVisible свойства hint</param>
    /// <param name="setText">Action для установки текста (вызывается перед показом)</param>
    /// <param name="durationMs">Длительность отображения</param>
    private async void ShowHint(Action<bool> setVisible, Action setText, int durationMs = HintDisplayDurationMs)
    {
        _activeHintCts?.Cancel();
        var cts = new CancellationTokenSource();
        _activeHintCts = cts;

        try
        {
            setText();
            setVisible(true);
            await Task.Delay(durationMs, cts.Token);
            setVisible(false);
        }
        catch (OperationCanceledException)
        {
            // Новый hint отменил этот — нормальное поведение
        }
    }

    /// <summary>
    /// Специальный hint для Copy: сначала highlight, потом обычный fade.
    /// </summary>
    private async void ShowCopyHint()
    {
        _activeHintCts?.Cancel();
        var cts = new CancellationTokenSource();
        _activeHintCts = cts;

        try
        {
            IsCopyHighlighted = true;
            IsCopyHintVisible = true;
            await Task.Delay(CopyHighlightDurationMs, cts.Token);
            IsCopyHighlighted = false;
            await Task.Delay(HintDisplayDurationMs - CopyHighlightDurationMs, cts.Token);
            IsCopyHintVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Отменён — ок
        }
    }

    #endregion

    #region Buffer Progress (unified)

    /// <summary>
    /// Единый метод синхронизации состояния буфера.
    /// Используется вместо дублирования логики в HandleBufferStateChanged,
    /// ForceUpdateBufferProgress, HandleTrackChanged.
    /// </summary>
    /// <param name="externalState">BufferState от события AudioEngine (если есть)</param>
    private void SyncBufferState(BufferState? externalState = null)
    {
        if (!HasTrack || CurrentTrack == null || IsTrackResetting)
            return;

        if (CurrentTrack.IsDownloaded)
        {
            SetFullyBuffered();
            return;
        }

        if (externalState.HasValue)
        {
            var state = externalState.Value;
            BufferProgressPercent = state.Progress;
            IsFullyBuffered = state.IsFullyBuffered;
            BufferedRanges = state.Ranges;
        }
        else
        {
            BufferProgressPercent = _audio.BufferProgress;
            BufferedRanges = _audio.GetBufferedRanges();
            IsFullyBuffered = _audio.IsFullyBuffered;
        }

        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    /// <summary>
    /// Устанавливает 100% буферизацию. Используется для downloaded треков.
    /// </summary>
    private void SetFullyBuffered()
    {
        if (IsFullyBuffered && BufferProgressPercent >= 100) return;

        BufferProgressPercent = 100;
        BufferedRanges = [(0.0, 1.0)];
        IsFullyBuffered = true;
        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    /// <summary>
    /// Сбрасывает буфер в нулевое состояние (для reset/null track).
    /// </summary>
    private void ResetBufferState()
    {
        BufferProgressPercent = 0;
        BufferedRanges = [];
        IsFullyBuffered = false;
        this.RaisePropertyChanged(nameof(UseSegmentedBuffer));
    }

    private void HandleBufferStateChanged(BufferState state)
    {
        if (IsTrackResetting) return;
        SyncBufferState(state);
    }

    #endregion

    #region Track Reset Visual

    private void BeginTrackReset()
    {
        int session = Interlocked.Increment(ref _trackResetSession);
        _trackResetStartTime = DateTime.UtcNow;

        IsTrackResetting = true;
        Position = TimeSpan.Zero;
        PositionSeconds = 0;
        IsSeekBusy = true;
        ResetBufferState();

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

        RecalcEffectivePercent();
        RaiseVolumePropertiesChanged();
        Log.Info($"[PlayerBar] MaxVolume changed: {oldMax} -> {newMax}");
    }

    /// <summary>
    /// Обрабатывает смену трека.
    /// Сравнивает Id нового трека с _lastHandledTrackId.
    /// Если трек тот же — пропускает BeginTrackReset.
    /// </summary>
    private void HandleTrackChanged(TrackInfo? track)
    {
        string? newTrackId = track?.Id;
        bool isNewTrack = newTrackId != _lastHandledTrackId;
        _lastHandledTrackId = newTrackId;

        CurrentTrack = track;
        HasTrack = track != null;

        RaiseTrackInfoChanged();

        if (track != null)
        {
            if (isNewTrack)
            {
                _lastDownloadedBytes = 0;
                _lastValidStreamInfo = "";
                AvailableFormats.Clear();
                _pendingStreamInfoTrackId = track.Id;

                BeginTrackReset();

                Duration = track.Duration;
                DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;

                ShowStreamInfo = true;
                StreamInfo = SL["Player_StreamInfo_Loading"];

                Log.Debug($"[PlayerBar] New track: {track.Id}");
            }
            else
            {
                Log.Debug($"[PlayerBar] Same track event skipped: {track.Id}");
            }

            var storedTrack = _library.GetTrack(track.Id);
            IsLiked = storedTrack?.IsLiked ?? track.IsLiked;

            if (track.IsDownloaded)
                SetFullyBuffered();
        }
        else
        {
            ResetToNoTrack();
        }

        this.RaisePropertyChanged(nameof(DurationTooltip));
        UpdateQueueState();
    }

    /// <summary>
    /// Полный сброс состояния когда трек убран (null).
    /// </summary>
    private void ResetToNoTrack()
    {
        _pendingStreamInfoTrackId = null;
        _lastDownloadedBytes = 0;
        _lastValidStreamInfo = "";
        AvailableFormats.Clear();
        Duration = TimeSpan.Zero;
        DurationSeconds = 1;
        ShowStreamInfo = false;
        StreamInfo = "";
        IsLiked = false;
        IsTrackResetting = false;
        Position = TimeSpan.Zero;
        PositionSeconds = 0;
        ResetBufferState();
    }

    /// <summary>
    /// Мягкая синхронизация состояния при восстановлении из трея.
    /// НЕ вызывает BeginTrackReset. Восстанавливает позицию, буфер,
    /// StreamInfo из кэша, like-статус.
    /// </summary>
    private void HandleForceSync()
    {
        if (!HasTrack || CurrentTrack == null)
            return;

        // Снимаем зависшие guard-флаги
        if (IsTrackResetting)
        {
            IsTrackResetting = false;
            Log.Debug("[PlayerBar] ForceSync: cleared stale IsTrackResetting");
        }

        if (IsSeekBusy)
        {
            ClearSeekBusy();
            Log.Debug("[PlayerBar] ForceSync: cleared stale IsSeekBusy");
        }

        // Позиция
        SyncPositionFromEngine();

        // Длительность
        var dur = _audio.TotalDuration;
        if (dur.TotalSeconds > 0)
        {
            Duration = dur;
            DurationSeconds = dur.TotalSeconds;
        }

        // Буфер
        SyncBufferState();

        // StreamInfo из кэша
        if (!string.IsNullOrEmpty(_lastValidStreamInfo))
        {
            StreamInfo = _lastValidStreamInfo;
            ShowStreamInfo = true;
        }

        // Like-статус из БД
        var storedTrack = _library.GetTrack(CurrentTrack.Id);
        if (storedTrack != null)
        {
            IsLiked = storedTrack.IsLiked;
            CurrentTrack.IsLiked = storedTrack.IsLiked;
        }

        // Сброс stale speed data
        _lastDownloadedBytes = _audio.GetDownloadedBytes();
        _lastSpeedCheck = DateTime.UtcNow;
        DownloadSpeedText = "";

        // Синхронизация Volume (мог измениться из tray scroll)
        int currentEngineVolume = (int)Math.Round(_audio.GetVolume());
        if (Volume != currentEngineVolume)
            Volume = currentEngineVolume;

        // Поднимаем все PropertyChanged
        RaiseTrackInfoChanged();
        this.RaisePropertyChanged(nameof(DurationTooltip));
        this.RaisePropertyChanged(nameof(LikeTooltip));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));

        UpdateQueueState();

        Log.Debug($"[PlayerBar] ForceSync: pos={FormatTime(Position)}, dur={FormatTime(Duration)}, buf={BufferProgressPercent:F0}%, stream={StreamInfo}");
    }

    private void UpdateQueueState()
    {
        var queue = _audio.Queue;
        TotalTracksInQueue = queue.Count;
        HasQueueToShuffle = queue.Count > 1;

        if (CurrentTrack != null)
        {
            CurrentTrackIndex = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].Id == CurrentTrack.Id)
                {
                    CurrentTrackIndex = i;
                    break;
                }
            }
        }
        else
        {
            CurrentTrackIndex = 0;
        }

        this.RaisePropertyChanged(nameof(CurrentTrackIndexDisplay));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
    }

    /// <summary>
    /// Обновляет StreamInfo из AudioEngine event.
    /// Duration обновляется только когда heavy-подписки активны (не в suspend —
    /// т.к. этот метод подписан через _heavySubscriptions, при suspend событие не придёт).
    /// StreamInfo text кэшируется для восстановления при ForceSync.
    /// </summary>
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
            _lastValidStreamInfo = info.FormatDisplay;
            StreamInfo = info.FormatDisplay;

            Duration = TimeSpan.FromMilliseconds(info.DurationMs);
            DurationSeconds = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 1;
            this.RaisePropertyChanged(nameof(DurationTooltip));

            int normalizedInfoBitrate = AudioConstants.NormalizeBitrate(info.Bitrate);

            foreach (var f in AvailableFormats)
            {
                int normalizedFormatBitrate = AudioConstants.NormalizeBitrate((int)f.Bitrate);
                f.IsActive = string.Equals(f.Codec, info.Codec, StringComparison.OrdinalIgnoreCase) &&
                             normalizedInfoBitrate == normalizedFormatBitrate;
            }

            if (IsTrackResetting)
            {
                bool isForCurrentTrack = (!string.IsNullOrEmpty(info.TrackId) && CurrentTrack.Id == info.TrackId)
                                         || (string.IsNullOrEmpty(info.TrackId) && _pendingStreamInfoTrackId == CurrentTrack.Id);

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

    /// <summary>
    /// Обновляет текст скорости загрузки. Вызывается по Rx interval (1 раз/сек).
    /// При suspend подписка dispose — метод не вызывается.
    /// </summary>
    private void UpdateDownloadSpeed()
    {
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

    /// <summary>
    /// Fallback обновление позиции из AudioEngine (каждые 500ms).
    /// Используется когда OnPositionChanged event не приходит (буферизация).
    /// При suspend подписка dispose — метод не вызывается.
    /// </summary>
    private void FallbackPositionUpdate()
    {
        if (!HasTrack || IsTrackResetting)
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
                (DateTime.UtcNow.Ticks - busyTicks) > TimeSpan.FromMilliseconds(SeekBusyTimeoutMs).Ticks)
            {
                Log.Debug("[PlayerBar] Seek busy timeout — clearing guard");
                ClearSeekBusy();
                SyncPositionFromEngine();
            }
            return;
        }

        if (_isSeeking) return;

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
            ClearSeekBusy();
            Log.Debug("[PlayerBar] Seek cancelled, guards cleared");
        }
        catch (Exception ex)
        {
            ClearSeekBusy();
            SyncPositionFromEngine();
            Log.Warn($"[PlayerBar] Seek failed: {ex.Message}");
        }
    }

    public void CancelSeek()
    {
        _isSeeking = false;
        ClearSeekBusy();
        SyncPositionFromEngine();
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

    /// <summary>
    /// Запрашивает Resume через PlayerControlService.
    /// Проверяет глобальный SuspendLevel вместо локального состояния подписок.
    /// </summary>
    public void RequestResumeIfSuspended()
    {
        if (IsSuspended)
        {
            Log.Info($"[PlayerBar] Requesting resume (level={CurrentSuspendLevel})");
            _playerControl.RequestResume();
        }
    }

    #endregion

    #region Private Helpers

    private void ClearSeekBusy()
    {
        IsSeekBusy = false;
        Volatile.Write(ref _seekBusyStartTicks, 0L);
    }

    private void SyncPositionFromEngine()
    {
        var realPos = _audio.CurrentPosition;
        Position = realPos;
        PositionSeconds = realPos.TotalSeconds;
    }

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

    /// <summary>
    /// Поднимает PropertyChanged для всех track-info свойств.
    /// Вызывается при смене трека и при ForceSync.
    /// </summary>
    private void RaiseTrackInfoChanged()
    {
        this.RaisePropertyChanged(nameof(SafeTitle));
        this.RaisePropertyChanged(nameof(SafeAuthor));
        this.RaisePropertyChanged(nameof(SafeThumbnail));
        this.RaisePropertyChanged(nameof(PlayPauseTooltip));
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");

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

            int normalizedCurrentBitrate = AudioConstants.NormalizeBitrate(currentBitrate);

            foreach (var f in formats)
            {
                int normalizedFormatBitrate = AudioConstants.NormalizeBitrate((int)f.Bitrate);

                f.IsDownloaded = cachedFormats.Any(cached =>
                {
                    int normalizedCachedBitrate = AudioConstants.NormalizeBitrate(cached.Bitrate);
                    return string.Equals(f.Container, cached.Container, StringComparison.OrdinalIgnoreCase) &&
                           normalizedFormatBitrate == normalizedCachedBitrate;
                });

                f.IsActive = string.Equals(f.Codec, currentFormat, StringComparison.OrdinalIgnoreCase) &&
                             normalizedFormatBitrate == normalizedCurrentBitrate;

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

    #region Language

    private void OnLanguageChanged(object? sender, string newLang)
    {
        RaiseTrackInfoChanged();
        RaiseVolumePropertiesChanged();

        this.RaisePropertyChanged(nameof(ShuffleTooltip));
        this.RaisePropertyChanged(nameof(PreviousTooltip));
        this.RaisePropertyChanged(nameof(NextTooltip));
        this.RaisePropertyChanged(nameof(RepeatTooltip));
        this.RaisePropertyChanged(nameof(LikeTooltip));
        this.RaisePropertyChanged(nameof(CopyTooltip));
        this.RaisePropertyChanged(nameof(TrackNumberTooltip));
        this.RaisePropertyChanged(nameof(DurationTooltip));
        this.RaisePropertyChanged(nameof(L));
    }

    #endregion

    #region LifeCycle

    protected override void OnSuspend(SuspendLevel level)
    {
        // Hard (tray) или Soft (потеря фокуса с оптимизацией) — dispose heavy subs
        // Soft без оптимизации никогда не дойдёт сюда (BroadcastSuspendLevel преобразует в None)

        _heavySubscriptions?.Dispose();
        _heavySubscriptions = null;

        DownloadSpeedText = "";

        SuspendRequested?.Invoke();

        Log.Debug($"[PlayerBar] Suspended (level={level}): heavy subscriptions disposed");
    }

    protected override void OnResume(SuspendLevel previousLevel)
    {
        Log.Debug($"[PlayerBar] OnResume called (previousLevel={previousLevel})");

        // Сброс stale speed data
        _lastDownloadedBytes = _audio.GetDownloadedBytes();
        _lastSpeedCheck = DateTime.UtcNow;

        // Пересоздаём тяжёлые подписки
        SubscribeHeavy();

        ResumeRequested?.Invoke();

        Log.Debug("[PlayerBar] Resumed: heavy subscriptions recreated");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

            _activeHintCts?.Cancel();
            _activeHintCts?.Dispose();

            _heavySubscriptions?.Dispose();

            if (_isInitialized && Volume > 0)
                _library.UpdateSettings(s => s.LastVolume = Volume);

            _audio.SaveVolumeNow();
        }
        base.Dispose(disposing);
    }

    #endregion
}