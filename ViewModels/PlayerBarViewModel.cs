// PlayerBarViewModel.cs
// ViewModel для панели плеера (нижняя панель управления воспроизведением)

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.ViewModels;

/// <summary>
/// ViewModel для панели управления воспроизведением.
/// Отвечает за:
/// - Отображение информации о текущем треке
/// - Управление воспроизведением (Play/Pause/Next/Previous)
/// - Управление громкостью
/// - Отображение прогресса и буферизации
/// - Переключение форматов
/// </summary>
public class PlayerBarViewModel : ViewModelBase, IDisposable
{
    // ЗАВИСИМОСТИ

    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly IClipboardService _clipboard;
    private readonly YoutubeProvider _youtube;

    // ТАЙМЕРЫ

    private readonly DispatcherTimer _speedUpdateTimer;
    private readonly DispatcherTimer _fallbackPositionTimer;

    // СОСТОЯНИЕ УПРАВЛЕНИЯ

    /// <summary>Пользователь тянет слайдер позиции</summary>
    private bool _isSeeking;

    /// <summary>Только что закончил тянуть слайдер</summary>
    private bool _justFinishedSeeking;

    /// <summary>Громкость до мута</summary>
    private float _volumeBeforeMute;

    // ТАЙМИНГИ И СЧЕТЧИКИ

    private DateTime _lastSeekTime = DateTime.MinValue;
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedCheck = DateTime.MinValue;

    private const int SeekCooldownMs = 250;

    // СВОЙСТВА ТРЕКА

    /// <summary>Текущий трек</summary>
    [Reactive] public TrackInfo? CurrentTrack { get; private set; }

    /// <summary>Идет загрузка</summary>
    [Reactive] public bool IsLoading { get; private set; }

    /// <summary>Воспроизводится</summary>
    [Reactive] public bool IsPlaying { get; private set; }

    /// <summary>На паузе</summary>
    [Reactive] public bool IsPaused { get; private set; }

    /// <summary>Есть активный трек</summary>
    [Reactive] public bool HasTrack { get; private set; }

    /// <summary>Трек в избранном</summary>
    [Reactive] public bool IsLiked { get; private set; }

    /// <summary>Безопасное название трека</summary>
    public string SafeTitle => CurrentTrack?.Title ?? "Not Playing";

    /// <summary>Безопасное имя исполнителя</summary>
    public string SafeAuthor => CurrentTrack?.Author ?? "";

    /// <summary>URL обложки</summary>
    public string? SafeThumbnail => CurrentTrack?.ThumbnailUrl;

    /// <summary>Доступные форматы для переключения</summary>
    public ObservableCollection<StreamOption> AvailableFormats { get; } = [];

    // ПРОГРЕСС И ВРЕМЯ

    /// <summary>Текущая позиция</summary>
    [Reactive] public TimeSpan Position { get; set; }

    /// <summary>Общая длительность</summary>
    [Reactive] public TimeSpan Duration { get; private set; }

    /// <summary>Текущая позиция в секундах (для слайдера)</summary>
    [Reactive] public double PositionSeconds { get; set; }

    /// <summary>Общая длительность в секундах</summary>
    [Reactive] public double DurationSeconds { get; private set; }

    /// <summary>Буферизовано секунд</summary>
    [Reactive] public double BufferedSeconds { get; private set; }

    /// <summary>Идет перемотка</summary>
    [Reactive] public bool IsSeekBusy { get; private set; }

    // ГРОМКОСТЬ

    /// <summary>Текущая громкость (0-MaxVolume)</summary>
    [Reactive] public int Volume { get; set; }

    /// <summary>Максимальная громкость</summary>
    [Reactive] public int MaxVolume { get; private set; } = 100;

    /// <summary>Звук выключен</summary>
    [Reactive] public bool IsMuted { get; private set; }

    /// <summary>Включено перемешивание</summary>
    [Reactive] public bool ShuffleEnabled { get; set; }

    /// <summary>Режим повтора</summary>
    [Reactive] public RepeatMode RepeatMode { get; set; }

    /// <summary>Ширина слайдера громкости (расширяется при MaxVolume > 100)</summary>
    public double VolumeSliderWidth => 100 + ((MaxVolume - 100) * 0.5);

    // ВИЗУАЛИЗАЦИЯ ГРОМКОСТИ

    [Reactive] public double Bar1Opacity { get; set; } = 0.3;
    [Reactive] public double Bar2Opacity { get; set; } = 0.3;
    [Reactive] public double Bar3Opacity { get; set; } = 0.3;
    [Reactive] public double Bar4Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Opacity { get; set; } = 0.3;
    [Reactive] public double Bar5Thickness { get; set; } = 4;
    [Reactive] public string VolumeBarBrushKey { get; set; } = "TextSecondaryBrush";

    // ИНФОРМАЦИЯ О ПОТОКЕ

    /// <summary>Информация о текущем потоке (формат/битрейт)</summary>
    [Reactive] public string StreamInfo { get; private set; } = "";

    /// <summary>Показывать информацию о потоке</summary>
    [Reactive] public bool ShowStreamInfo { get; private set; }

    /// <summary>Скорость загрузки</summary>
    [Reactive] public string DownloadSpeedText { get; private set; } = "";

    // КОМАНДЫ

    /// <summary>Play/Pause</summary>
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }

    /// <summary>Предыдущий трек</summary>
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }

    /// <summary>Следующий трек</summary>
    public ReactiveCommand<Unit, Unit> NextCommand { get; }

    /// <summary>Переключить перемешивание</summary>
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }

    /// <summary>Переключить режим повтора</summary>
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }

    /// <summary>Добавить/убрать из избранного</summary>
    public ReactiveCommand<Unit, Unit> ToggleLikeCommand { get; }

    /// <summary>Включить/выключить звук</summary>
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }

    /// <summary>Копировать ссылку на трек</summary>
    public ReactiveCommand<Unit, Unit> CopyLinkCommand { get; }

    /// <summary>Загрузить доступные форматы</summary>
    public ReactiveCommand<Unit, Unit> LoadFormatsCommand { get; }

    /// <summary>Переключить формат</summary>
    public ReactiveCommand<StreamOption, Unit> SwitchFormatCommand { get; }

    // КОНСТРУКТОР

    /// <summary>
    /// Создает ViewModel панели плеера
    /// </summary>
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

        // Инициализация громкости
        MaxVolume = _library.Data.MaxVolumeLimit;
        if (MaxVolume < 100) MaxVolume = 100;

        Volume = (int)_audio.GetVolume();
        _volumeBeforeMute = Volume > 5 ? Volume : 50;

        // Инициализация режимов
        ShuffleEnabled = _audio.ShuffleEnabled;
        RepeatMode = _audio.RepeatMode;

        UpdateVolumeBars();

        Log.Info($"[PlayerBar] Initialized. MaxVol: {MaxVolume}, CurrentVol: {Volume}");


        // ПОДПИСКИ НА СОБЫТИЯ АУДИО ДВИЖКА


        // ★ Главная подписка: изменение состояния воспроизведения
        // Состояние передаётся напрямую из события для избежания race condition
        _audio.OnPlaybackStateChanged += (isPlaying, isPaused) =>
            Dispatcher.UIThread.Post(() => SyncPlaybackState(isPlaying, isPaused));

        // Изменение позиции воспроизведения
        _audio.OnPositionChanged += pos => Dispatcher.UIThread.Post(() =>
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

        // Изменение громкости пользователем
        this.WhenAnyValue(x => x.Volume)
            .Skip(1)
            .Subscribe(v =>
            {
                _audio.SetVolumeInstant(v);
                IsMuted = v < 1;
                UpdateVolumeBars();
            });

        // Изменение максимальной громкости в настройках
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

        // Смена трека
        _audio.OnTrackChanged += t => Dispatcher.UIThread.Post(() => HandleTrackChanged(t));

        // Изменение состояния загрузки
        _audio.OnLoadingChanged += l => Dispatcher.UIThread.Post(() =>
        {
            IsLoading = l;
            IsSeekBusy = l;
        });

        // Информация о потоке готова
        _audio.OnStreamInfoReady += () => Dispatcher.UIThread.Post(UpdateStreamInfo);


        // ТАЙМЕРЫ


        // Таймер обновления позиции (fallback)
        _fallbackPositionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fallbackPositionTimer.Tick += (_, _) => FallbackPositionUpdate();
        _fallbackPositionTimer.Start();

        // Таймер обновления скорости загрузки
        _speedUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedUpdateTimer.Tick += (_, _) => UpdateDownloadSpeed();
        _speedUpdateTimer.Start();


        // ПОДПИСКА НА ПРОГРЕСС ЗАГРУЗКИ


        Observable.FromEvent<Action<string, float>, (string, float)>(
            h => (id, p) => h((id, p)),
            h => _downloads.OnProgress += h,
            h => _downloads.OnProgress -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                if (CurrentTrack?.Id == x.Item1)
                {
                    BufferedSeconds = DurationSeconds * x.Item2;
                }
            });


        // КОМАНДЫ


        var canExecute = this.WhenAnyValue(x => x.HasTrack);

        // Play/Pause - простая отправка команды, состояние придёт через событие
        PlayPauseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            bool wantsToPlay = !_audio.IsPlaying;
            Log.Info($"[PlayerBar] PlayPause -> {(wantsToPlay ? "PLAY" : "PAUSE")}");
            await _audio.SetPlaybackStateAsync(wantsToPlay);
        }, canExecute);

        // Следующий трек
        NextCommand = ReactiveCommand.CreateFromTask(
            () => _audio.PlayNextAsync(),
            canExecute);

        // Предыдущий трек
        PreviousCommand = ReactiveCommand.CreateFromTask(
            () => _audio.PlayPreviousAsync(),
            canExecute);

        // Переключение перемешивания
        ToggleShuffleCommand = ReactiveCommand.Create(() =>
        {
            ShuffleEnabled = !ShuffleEnabled;
            _audio.ShuffleEnabled = ShuffleEnabled;
            _library.Data.ShuffleEnabled = ShuffleEnabled;
            _library.Save();

            Log.Info($"[PlayerBar] Shuffle: {ShuffleEnabled}");
        });

        // Переключение режима повтора
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

            Log.Info($"[PlayerBar] RepeatMode: {RepeatMode}");
        });

        // Включение/выключение звука
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

        // Добавление в избранное
        ToggleLikeCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentTrack != null)
            {
                _library.ToggleLike(CurrentTrack);
                IsLiked = CurrentTrack.IsLiked;
            }
        }, canExecute);

        // Копирование ссылки
        CopyLinkCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (CurrentTrack?.Url != null)
            {
                await _clipboard.SetTextAsync(CurrentTrack.Url);
                Log.Info($"[PlayerBar] Link copied: {CurrentTrack.Url}");
            }
        }, canExecute);

        // Загрузка доступных форматов
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

            Log.Info($"[PlayerBar] Loaded {formats.Count} formats for {CurrentTrack.Title}");
        });

        // Переключение формата
        SwitchFormatCommand = ReactiveCommand.CreateFromTask<StreamOption>(async (option) =>
        {
            if (option == null) return;
            Log.Info($"[PlayerBar] Switching format to {option.DisplayName}");
            await _audio.SwitchQualityAsync(option.Container, (int)option.Bitrate);
        });
    }

    // СИНХРОНИЗАЦИЯ СОСТОЯНИЯ ВОСПРОИЗВЕДЕНИЯ

    /// <summary>
    /// Синхронизирует UI состояние с переданными значениями.
    /// Вызывается автоматически при получении события OnPlaybackStateChanged.
    /// </summary>
    /// <param name="isPlaying">Воспроизводится ли трек</param>
    /// <param name="isPaused">На паузе ли трек</param>
    private void SyncPlaybackState(bool isPlaying, bool isPaused)
    {
        // Логируем только при реальном изменении
        if (IsPlaying != isPlaying || IsPaused != isPaused)
        {
            Log.Debug($"[PlayerBar] State sync: Play={isPlaying}, Pause={isPaused}");
        }

        IsPlaying = isPlaying;
        IsPaused = isPaused;
    }

    // ОБНОВЛЕНИЕ ВИЗУАЛИЗАЦИИ ГРОМКОСТИ

    /// <summary>
    /// Обновляет визуальное отображение баров громкости
    /// </summary>
    private void UpdateVolumeBars()
    {
        double vol = Volume;

        Bar1Opacity = vol > 0 ? 1.0 : 0.3;
        Bar2Opacity = vol >= 20 ? 1.0 : 0.3;
        Bar3Opacity = vol >= 40 ? 1.0 : 0.3;
        Bar4Opacity = vol >= 60 ? 1.0 : 0.3;
        Bar5Opacity = vol >= 80 ? 1.0 : 0.3;

        // Визуализация boost (>100%)
        if (vol > 100)
        {
            double boost = (vol - 100) / 100.0;
            Bar5Thickness = 4 + (boost * 6);
            if (Bar5Thickness > 12) Bar5Thickness = 12;

            // Используем акцентный цвет системы для буста
            VolumeBarBrushKey = "AccentBrush";
        }
        else
        {
            Bar5Thickness = 4;
            VolumeBarBrushKey = "TextSecondaryBrush";
        }
    }

    // ОБРАБОТКА СМЕНЫ ТРЕКА

    /// <summary>
    /// Обрабатывает событие смены трека
    /// </summary>
    private void HandleTrackChanged(TrackInfo? track)
    {
        CurrentTrack = track;
        HasTrack = track != null;

        // Уведомляем UI об изменении свойств
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

            // Показываем "Loading..." пока информация о потоке не готова
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

        // Синхронизируем состояние воспроизведения
        // SyncPlaybackState(_audio.IsPlaying, _audio.IsPaused);
    }

    // ОБНОВЛЕНИЕ ИНФОРМАЦИИ О ПОТОКЕ

    /// <summary>
    /// Обновляет отображение информации о текущем потоке
    /// </summary>
    private void UpdateStreamInfo()
    {
        if (CurrentTrack == null)
        {
            ShowStreamInfo = false;
            StreamInfo = "";
            return;
        }

        var (format, bitrate, isReady) = _audio.GetCurrentStreamInfo();

        // Убрали проверку на "Unknown" - показываем что есть
        if (!isReady || string.IsNullOrEmpty(format))
        {
            StreamInfo = L["Stream_Loading"] ?? "Loading...";
            ShowStreamInfo = true;
            return;
        }

        // Локальный файл
        if (CurrentTrack.IsDownloaded)
        {
            StreamInfo = $"{format} • {L["Stream_LocalFile"] ?? "Local File"}";
        }
        else
        {
            // Стриминг - показываем кодек и битрейт
            // Показываем даже если битрейт 0
            StreamInfo = bitrate > 0
                ? $"{format} • {bitrate}kbps"
                : format;
        }

        ShowStreamInfo = true;

        Log.Debug($"[PlayerBar] StreamInfo updated: {StreamInfo}");
    }

    // ОБНОВЛЕНИЕ СКОРОСТИ ЗАГРУЗКИ

    /// <summary>
    /// Обновляет отображение скорости загрузки
    /// </summary>
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

    // FALLBACK ОБНОВЛЕНИЕ ПОЗИЦИИ

    /// <summary>
    /// Fallback обновление позиции и длительности по таймеру
    /// </summary>
    private void FallbackPositionUpdate()
    {
        if (!HasTrack || _isSeeking || _justFinishedSeeking) return;

        // Обновляем длительность если она изменилась
        var realDur = _audio.TotalDuration;
        if (Math.Abs(DurationSeconds - realDur.TotalSeconds) > 1 && realDur.TotalSeconds > 0)
        {
            Duration = realDur;
            DurationSeconds = Duration.TotalSeconds;
        }

        // Обновляем прогресс буферизации
        if (_audio.BufferProgress > 0)
        {
            BufferedSeconds = DurationSeconds * (_audio.BufferProgress / 100.0);
        }
    }

    // ЛОГИКА ПЕРЕМОТКИ (SEEK)

    /// <summary>
    /// Начало перемотки (пользователь начал тянуть слайдер)
    /// </summary>
    public void StartSeek()
    {
        _isSeeking = true;
        _justFinishedSeeking = false;
    }

    /// <summary>
    /// Обновление позиции во время перемотки
    /// </summary>
    public void UpdateSeekPosition(double seconds)
    {
        if (!_isSeeking) return;

        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionSeconds = seconds;
        Position = TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Завершение перемотки (пользователь отпустил слайдер)
    /// </summary>
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

        // Cooldown для предотвращения спама
        var delta = DateTime.UtcNow - _lastSeekTime;
        if (delta.TotalMilliseconds < SeekCooldownMs)
        {
            await Task.Delay(SeekCooldownMs - (int)delta.TotalMilliseconds);
        }

        _lastSeekTime = DateTime.UtcNow;
        IsSeekBusy = true;

        Log.Info($"[PlayerBar] Seek end -> {target}s");

        await _audio.SeekAsync(TimeSpan.FromSeconds(target));

        await Task.Delay(300);
        IsSeekBusy = false;
        _justFinishedSeeking = false;
    }

    // ДОПОЛНИТЕЛЬНЫЕ МЕТОДЫ

    /// <summary>
    /// Вызывается при завершении изменения громкости (отпускание слайдера)
    /// </summary>
    public void OnVolumeChangeComplete()
    {
        Log.Info("[PlayerBar] Volume drag end. Saving...");
        _audio.SaveVolumeNow();
    }

    // ОСВОБОЖДЕНИЕ РЕСУРСОВ

    /// <summary>
    /// Освобождает ресурсы ViewModel
    /// </summary>
    public void Dispose()
    {
        _fallbackPositionTimer.Stop();
        _speedUpdateTimer.Stop();
        _audio.SaveVolumeNow();

        Log.Info("[PlayerBar] Disposed");
    }
}