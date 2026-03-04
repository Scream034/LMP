using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LMP.Core.Audio;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Единый координатор управления воспроизведением.
/// Предоставляет реактивные свойства и команды, синхронизированные между всеми UI компонентами.
/// 
/// <para><b>Архитектура:</b></para>
/// <list type="bullet">
///   <item>Является единственным подписчиком на события AudioEngine для state tracking</item>
///   <item>Предоставляет BehaviorSubject-based IObservable для PlayerBar, TrayIcon, MediaKeys</item>
///   <item>Работает независимо от suspend/resume состояния окна</item>
/// </list>
/// 
/// <para><b>ForceSync:</b></para>
/// <para>Не публикует повторные значения в CurrentTrack если объект тот же (по Id).
/// Это предотвращает ложный TrackReset при восстановлении из трея.</para>
/// 
/// <para><b>Shuffle:</b></para>
/// <para>Все изменения ShuffleEnabled ДОЛЖНЫ идти через этот сервис (SetShuffleEnabled / ToggleAutoShuffle),
/// чтобы BehaviorSubject всегда был синхронизирован с AudioEngine.</para>
/// </summary>
public sealed class PlayerControlService : IDisposable
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;

    private readonly BehaviorSubject<bool> _isPlayingSubject;
    private readonly BehaviorSubject<bool> _isPausedSubject;
    private readonly BehaviorSubject<bool> _isLoadingSubject;
    private readonly BehaviorSubject<TrackInfo?> _currentTrackSubject;
    private readonly BehaviorSubject<RepeatMode> _repeatModeSubject;
    private readonly BehaviorSubject<bool> _shuffleEnabledSubject;
    private readonly BehaviorSubject<int> _queueCountSubject;

    /// <summary>
    /// Реактивный поток текущей громкости (0–MaxVolume).
    /// Публикуется при любом изменении: скролл трея, слайдер, программное.
    /// </summary>
    private readonly BehaviorSubject<int> _volumeSubject;

    /// <summary>
    /// Сигнал принудительной синхронизации. Подписчики должны обновить
    /// все свои состояния без полного TrackReset.
    /// Значение — Unit при вызове ForceSync.
    /// </summary>
    private readonly Subject<Unit> _forceSyncSubject = new();

    /// <summary>
    /// Сигнал запроса Resume из любого компонента (например, Volume popup при suspend).
    /// MainWindow подписывается и вызывает RestoreFromTray / BroadcastResume.
    /// </summary>
    private readonly Subject<Unit> _resumeRequestSubject = new();

    private bool _disposed;

    public PlayerControlService(AudioEngine audio, LibraryService library)
    {
        _audio = audio;
        _library = library;

        _isPlayingSubject = new BehaviorSubject<bool>(_audio.IsPlaying);
        _isPausedSubject = new BehaviorSubject<bool>(_audio.IsPaused);
        _isLoadingSubject = new BehaviorSubject<bool>(_audio.IsLoading);
        _currentTrackSubject = new BehaviorSubject<TrackInfo?>(_audio.CurrentTrack);
        _repeatModeSubject = new BehaviorSubject<RepeatMode>(_audio.RepeatMode);
        _shuffleEnabledSubject = new BehaviorSubject<bool>(_audio.ShuffleEnabled);
        _queueCountSubject = new BehaviorSubject<int>(_audio.Queue.Count);
        _volumeSubject = new BehaviorSubject<int>((int)Math.Round(_audio.GetVolume()));

        _audio.OnPlaybackStateChanged += OnPlaybackStateChanged;
        _audio.OnTrackChanged += OnTrackChanged;
        _audio.OnQueueChanged += OnQueueChanged;
        _audio.OnLoadingStateChanged += OnLoadingStateChanged;

        Log.Debug("[PlayerControl] Service initialized");
    }

    #region Properties

    public bool IsPlaying => _isPlayingSubject.Value;
    public bool IsPaused => _isPausedSubject.Value;
    public bool IsLoading => _isLoadingSubject.Value;
    public TrackInfo? CurrentTrack => _currentTrackSubject.Value;
    public RepeatMode RepeatMode => _repeatModeSubject.Value;
    public bool ShuffleEnabled => _shuffleEnabledSubject.Value;
    public bool HasTrack => _currentTrackSubject.Value != null;
    public int QueueCount => _queueCountSubject.Value;

    /// <summary>
    /// Текущая громкость (0–MaxVolume). 
    /// Обновляется реактивно через <see cref="VolumeObservable"/>.
    /// </summary>
    public int CurrentVolume => _volumeSubject.Value;

    #endregion

    #region Observables

    public IObservable<bool> IsPlayingObservable => _isPlayingSubject.AsObservable();
    public IObservable<bool> IsPausedObservable => _isPausedSubject.AsObservable();
    public IObservable<bool> IsLoadingObservable => _isLoadingSubject.AsObservable();

    /// <summary>
    /// Поток изменений текущего трека.
    /// Публикуется ТОЛЬКО при реальной смене трека (другой Id).
    /// При ForceSync не переиздаётся — используйте ForceSyncObservable.
    /// </summary>
    public IObservable<TrackInfo?> CurrentTrackObservable => _currentTrackSubject.AsObservable();

    public IObservable<RepeatMode> RepeatModeObservable => _repeatModeSubject.AsObservable();
    public IObservable<bool> ShuffleEnabledObservable => _shuffleEnabledSubject.AsObservable();
    public IObservable<int> QueueCountObservable => _queueCountSubject.AsObservable();

    /// <summary>
    /// Реактивный поток изменения громкости.
    /// Используется TrayManager, PlayerBar и другими подписчиками.
    /// </summary>
    public IObservable<int> VolumeObservable => _volumeSubject.AsObservable();

    public IObservable<(bool IsPlaying, bool IsPaused)> PlaybackStateObservable =>
        _isPlayingSubject.CombineLatest(_isPausedSubject, (p, u) => (p, u));

    /// <summary>
    /// Сигнал для подписчиков: "обнови все состояния без TrackReset".
    /// Вызывается при восстановлении из трея / minimize.
    /// </summary>
    public IObservable<Unit> ForceSyncObservable => _forceSyncSubject.AsObservable();

    /// <summary>
    /// Сигнал запроса Resume. MainWindow подписывается и вызывает RestoreFromTray.
    /// Используется когда пользователь взаимодействует с UI в suspend-режиме
    /// (например, Volume popup hover/scroll).
    /// </summary>
    public IObservable<Unit> ResumeRequestObservable => _resumeRequestSubject.AsObservable();

    #endregion

    #region Commands

    public async Task PlayPauseAsync()
    {
        try
        {
            await _audio.SetPlaybackStateAsync(!_audio.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] PlayPause error: {ex.Message}");
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _audio.PlayNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Next error: {ex.Message}");
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _audio.PlayPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerControl] Previous error: {ex.Message}");
        }
    }

    public void ToggleRepeat()
    {
        var newMode = _audio.RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.None,
            _ => RepeatMode.None
        };

        _audio.RepeatMode = newMode;
        _library.UpdateSettings(s => s.RepeatMode = newMode);
        _repeatModeSubject.OnNext(newMode);

        Log.Debug($"[PlayerControl] RepeatMode changed to {newMode}");
    }

    public void ShuffleQueue()
    {
        _audio.ShuffleQueue();
        Log.Debug("[PlayerControl] Queue shuffled");
    }

    /// <summary>
    /// Переключает авто-перемешивание (toggle).
    /// Синхронизирует AudioEngine, сохраняет в настройки, публикует в Subject.
    /// </summary>
    public void ToggleAutoShuffle()
    {
        bool newState = !_audio.ShuffleEnabled;
        _audio.ShuffleEnabled = newState;
        _library.UpdateSettings(s => s.ShuffleEnabled = newState);
        _shuffleEnabledSubject.OnNext(newState);

        Log.Debug($"[PlayerControl] AutoShuffle changed to {newState}");
    }

    /// <summary>
    /// Устанавливает состояние авто-перемешивания напрямую.
    /// Используется из PlaylistViewModel и других мест, которые хотят
    /// явно установить shuffle = false перед стартом очереди.
    /// 
    /// <para><b>ВАЖНО:</b> Все изменения ShuffleEnabled должны идти через этот метод
    /// или ToggleAutoShuffle(), чтобы BehaviorSubject оставался синхронизированным.</para>
    /// </summary>
    /// <param name="enabled">Новое состояние авто-перемешивания.</param>
    public void SetShuffleEnabled(bool enabled)
    {
        if (_audio.ShuffleEnabled == enabled)
            return;

        _audio.ShuffleEnabled = enabled;
        _library.UpdateSettings(s => s.ShuffleEnabled = enabled);
        _shuffleEnabledSubject.OnNext(enabled);

        Log.Debug($"[PlayerControl] ShuffleEnabled set to {enabled}");
    }

    /// <summary>
    /// Быстрое изменение громкости без сохранения на диск.
    /// Предназначено для вызова из tight loops (mouse hook callback).
    /// 
    /// <para><b>Почему отдельный метод:</b> Стандартный <see cref="AdjustVolume"/>
    /// вызывает <c>SaveVolumeNow()</c> и <c>UpdateSettings()</c> на каждый тик колёсика.
    /// В mouse hook callback это создаёт задержку (файловый I/O).
    /// Этот метод только меняет значение в памяти + публикует в Subject.</para>
    /// 
    /// <para>Вызывайте <see cref="CommitVolume"/> после завершения серии scroll events
    /// для сохранения на диск.</para>
    /// </summary>
    /// <param name="delta">Положительный = громче, отрицательный = тише.</param>
    /// <returns>Новое значение громкости (0–MaxVolume).</returns>
    public int AdjustVolumeFast(int delta)
    {
        int currentVolume = (int)Math.Round(_audio.GetVolume());
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int newVolume = Math.Clamp(currentVolume + delta, 0, maxVolume);

        if (newVolume != currentVolume)
        {
            _audio.SetVolumeInstant(newVolume);
            _volumeSubject.OnNext(newVolume);
        }

        return newVolume;
    }

    /// <summary>
    /// Сохраняет текущую громкость на диск.
    /// Вызывается после серии быстрых изменений (scroll end).
    /// </summary>
    public void CommitVolume()
    {
        int volume = (int)Math.Round(_audio.GetVolume());
        _library.UpdateSettings(s => s.LastVolume = volume);
        _audio.SaveVolumeNow();
        Log.Debug($"[PlayerControl] Volume committed: {volume}");
    }

    /// <summary>
    /// Изменяет громкость на указанный шаг.
    /// Используется для scroll на tray icon и горячих клавиш.
    /// Публикует новое значение в <see cref="VolumeObservable"/>.
    /// </summary>
    /// <param name="delta">Положительный = громче, отрицательный = тише.</param>
    /// <returns>Новое значение громкости (0–MaxVolume).</returns>
    public int AdjustVolume(int delta)
    {
        int currentVolume = (int)Math.Round(_audio.GetVolume());
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int newVolume = Math.Clamp(currentVolume + delta, 0, maxVolume);

        if (newVolume != currentVolume)
        {
            _audio.SetVolumeInstant(newVolume);
            _library.UpdateSettings(s => s.LastVolume = newVolume);
            _audio.SaveVolumeNow();
            _volumeSubject.OnNext(newVolume);
        }

        return newVolume;
    }

    /// <summary>
    /// Устанавливает громкость напрямую (для слайдера PlayerBar).
    /// Публикует новое значение в <see cref="VolumeObservable"/>.
    /// </summary>
    /// <param name="volume">Значение громкости (0–MaxVolume).</param>
    public void SetVolume(int volume)
    {
        int maxVolume = _library.Settings.MaxVolumeLimit;
        if (maxVolume <= 0) maxVolume = 100;

        int clamped = Math.Clamp(volume, 0, maxVolume);
        int current = (int)Math.Round(_audio.GetVolume());

        if (clamped != current)
        {
            _audio.SetVolumeInstant(clamped);
            _library.UpdateSettings(s => s.LastVolume = clamped);
            _audio.SaveVolumeNow();
            _volumeSubject.OnNext(clamped);
        }
    }

    /// <summary>
    /// Возвращает текущую громкость из AudioEngine (округлённую до int).
    /// Предпочитайте свойство <see cref="CurrentVolume"/> или <see cref="VolumeObservable"/>.
    /// </summary>
    public int GetCurrentVolume() => (int)Math.Round(_audio.GetVolume());

    /// <summary>
    /// Возвращает максимальную громкость из настроек.
    /// </summary>
    public int GetMaxVolume()
    {
        int max = _library.Settings.MaxVolumeLimit;
        return max > 0 ? max : 100;
    }

    /// <summary>
    /// Запрашивает Resume у MainWindow (через ResumeRequestObservable).
    /// Вызывается когда пользователь взаимодействует с UI в suspend-режиме.
    /// </summary>
    public void RequestResume()
    {
        _resumeRequestSubject.OnNext(Unit.Default);
    }

    #endregion

    #region AudioEngine Event Handlers

    private void OnPlaybackStateChanged(bool isPlaying, bool isPaused)
    {
        _isPlayingSubject.OnNext(isPlaying);
        _isPausedSubject.OnNext(isPaused);
    }

    private void OnTrackChanged(TrackInfo? track)
    {
        _currentTrackSubject.OnNext(track);
    }

    private void OnQueueChanged()
    {
        _queueCountSubject.OnNext(_audio.Queue.Count);
    }

    private void OnLoadingStateChanged(bool isLoading)
    {
        _isLoadingSubject.OnNext(isLoading);
    }

    #endregion

    #region Sync

    /// <summary>
    /// Принудительная синхронизация всех состояний при восстановлении из трея.
    /// 
    /// <para><b>ВАЖНО:</b> НЕ переиздаёт CurrentTrack если трек тот же самый.
    /// Это предотвращает ложный BeginTrackReset → замораживание UI.</para>
    /// 
    /// <para>Вместо этого публикует ForceSyncObservable, на который PlayerBarViewModel
    /// подписывается для мягкого обновления (позиция, буфер, стрим-инфо).</para>
    /// 
    /// <para><b>Shuffle sync:</b> Всегда перечитывает _audio.ShuffleEnabled и публикует,
    /// чтобы исправить рассинхронизацию если кто-то менял через _audio напрямую.</para>
    /// </summary>
    public void ForceSync()
    {
        _isPlayingSubject.OnNext(_audio.IsPlaying);
        _isPausedSubject.OnNext(_audio.IsPaused);
        _isLoadingSubject.OnNext(_audio.IsLoading);
        _repeatModeSubject.OnNext(_audio.RepeatMode);
        _shuffleEnabledSubject.OnNext(_audio.ShuffleEnabled);
        _queueCountSubject.OnNext(_audio.Queue.Count);
        _volumeSubject.OnNext((int)Math.Round(_audio.GetVolume()));

        // НЕ переиздаём CurrentTrack — это вызвало бы HandleTrackChanged → BeginTrackReset
        // Вместо этого сигнализируем "мягкую" синхронизацию
        _forceSyncSubject.OnNext(Unit.Default);

        Log.Debug("[PlayerControl] Forced sync completed (soft, no track reset)");
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audio.OnPlaybackStateChanged -= OnPlaybackStateChanged;
        _audio.OnTrackChanged -= OnTrackChanged;
        _audio.OnQueueChanged -= OnQueueChanged;
        _audio.OnLoadingStateChanged -= OnLoadingStateChanged;

        _isPlayingSubject.Dispose();
        _isPausedSubject.Dispose();
        _isLoadingSubject.Dispose();
        _currentTrackSubject.Dispose();
        _repeatModeSubject.Dispose();
        _shuffleEnabledSubject.Dispose();
        _queueCountSubject.Dispose();
        _volumeSubject.Dispose();
        _forceSyncSubject.Dispose();
        _resumeRequestSubject.Dispose();

        Log.Debug("[PlayerControl] Service disposed");
    }

    #endregion
}