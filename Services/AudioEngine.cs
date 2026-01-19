using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MyLiteMusicPlayer.Models;
using PlaybackState = NAudio.Wave.PlaybackState;

namespace MyLiteMusicPlayer.Services;

/// <summary>
/// Основное ядро аудио-движка приложения. 
/// Обеспечивает воспроизведение, стриминг, управление очередью и расширенную обработку звука.
/// </summary>
/// <remarks>
/// Реализует программное усиление до 400% через <see cref="VolumeSampleProvider"/> 
/// и нормализацию громкости на основе децибел (дБ).
/// </remarks>
public class AudioEngine : IDisposable
{
    private readonly IWavePlayer _outputDevice;
    private readonly YoutubeProvider _youtube;
    private readonly PipedProvider _piped;
    private readonly LibraryService _library;
    private readonly DownloadService _downloadService;

    // Цепочка обработки звука
    private WaveStream? _currentStream;
    private VolumeSampleProvider? _volumeControl;

    // Управление состоянием
    private CancellationTokenSource? _streamCts;
    private readonly object _lock = new();
    private Guid _currentSessionId;
    private bool _isManualStop;
    private float _userVolumeMultiplier = 1.0f; // Громкость от пользователя (0.0 - 4.0)

    // Очередь и история
    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    // Публичные свойства состояния
    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying => _outputDevice.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice.PlaybackState == PlaybackState.Paused;

    private TimeSpan _currentPosition;
    public TimeSpan CurrentPosition => _currentPosition;
    public TimeSpan TotalDuration => _currentStream?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnLoadingChanged?.Invoke(value);
            }
        }
    }

    public bool ShuffleEnabled { get; set; }
    public RepeatMode RepeatMode { get; set; }

    // События для UI и других сервисов
    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;

    /// <summary>
    /// Инициализирует новый экземпляр аудио-движка.
    /// </summary>
    public AudioEngine(
        YoutubeProvider youtube,
        PipedProvider piped,
        LibraryService library,
        DownloadService downloadService)
    {
        _youtube = youtube;
        _piped = piped;
        _library = library;
        _downloadService = downloadService;

        // Настройка устройства вывода. DesiredLatency 200мс — баланс между стабильностью стриминга и откликом.
        _outputDevice = new WaveOutEvent
        {
            DesiredLatency = 200,
            NumberOfBuffers = 3
        };
        _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;

        // Восстановление настроек из библиотеки
        _userVolumeMultiplier = _library.Data.Volume;
        ShuffleEnabled = _library.Data.ShuffleEnabled;
        RepeatMode = _library.Data.RepeatMode;
    }

    #region Управление громкостью и звуком

    /// <summary>
    /// Устанавливает уровень громкости с учетом программного усиления и нормализации дБ.
    /// </summary>
    /// <param name="multiplier">Множитель громкости (от 0.0 до 4.0, где 1.0 = 100%).</param>
    public void SetVolume(float multiplier)
    {
        lock (_lock)
        {
            _userVolumeMultiplier = Math.Clamp(multiplier, 0f, 4f);

            // Сохраняем в настройки
            _library.Data.Volume = _userVolumeMultiplier;
            _library.Save();

            if (_volumeControl != null)
            {
                // Рассчитываем итоговый коэффициент: Пользовательская громкость * Коэффициент дБ
                // Формула перевода дБ в множитель: 10^(db/20)
                float dbGain = _library.Data.TargetGainDb;
                float dbMultiplier = (float)Math.Pow(10, dbGain / 20.0);

                _volumeControl.Volume = _userVolumeMultiplier * dbMultiplier;
            }
        }
    }

    /// <summary>
    /// Возвращает текущий пользовательский множитель громкости.
    /// </summary>
    public float GetVolume() => _userVolumeMultiplier;

    #endregion

    #region Воспроизведение

    /// <summary>
    /// Основной асинхронный метод запуска трека.
    /// </summary>
    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return;

        // Создаем новый уникальный ID сессии для отмены устаревших задач
        var sessionId = Guid.NewGuid();
        lock (_lock) _currentSessionId = sessionId;

        // Отменяем предыдущие асинхронные операции (например, получение URL)
        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        IsLoading = true;
        CurrentTrack = track;
        _currentPosition = TimeSpan.Zero;
        OnTrackChanged?.Invoke(track);

        try
        {
            // Останавливаем текущий поток перед открытием нового
            StopInternal(false);

            WaveStream reader = await Task.Run(async () =>
            {
                if (track.IsDownloaded && File.Exists(track.LocalPath))
                {
                    Debug.WriteLine($"[AudioEngine] Opening local file: {track.LocalPath}");
                    return (WaveStream)new AudioFileReader(track.LocalPath);
                }
                else
                {
                    string? streamUrl = track.StreamUrl;

                    // Получение URL
                    if (string.IsNullOrEmpty(streamUrl))
                    {
                        streamUrl = await _piped.GetStreamUrlAsync(track.Id.Replace("yt_", ""), ct);
                        if (string.IsNullOrEmpty(streamUrl))
                        {
                            streamUrl = await _youtube.RefreshStreamUrlAsync(track);
                        }
                    }

                    if (string.IsNullOrEmpty(streamUrl))
                        throw new Exception("Не удалось получить ссылку на аудиопоток.");

                    Debug.WriteLine($"[AudioEngine] Opening network stream: {streamUrl.Substring(0, 20)}...");

                    // MediaFoundationReader конструктор блокирует поток - вызываем в Task.Run
                    return new MediaFoundationReader(streamUrl);
                }
            }, ct);

            // Проверка: не сменился ли трек, пока мы ждали URL?
            if (ct.IsCancellationRequested || _currentSessionId != sessionId)
            {
                reader.Dispose();
                return;
            }

            lock (_lock)
            {
                _currentStream = reader;

                // Создаем SampleProvider для возможности усиления > 1.0 (NAudio WaveOut по умолчанию ограничен 1.0)
                var sampleProvider = reader.ToSampleProvider();
                _volumeControl = new VolumeSampleProvider(sampleProvider);

                // Применяем громкость (включая усиление и дБ)
                SetVolume(_userVolumeMultiplier);

                _outputDevice.Init(_volumeControl);
                _outputDevice.Play();
            }

            AddToHistory(track);

            // Запускаем отслеживание позиции
            _ = TrackPositionLoopAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Playback Error: {ex.Message}");
            OnError?.Invoke(ex.Message);
            StopInternal(true);
        }
        finally
        {
            if (_currentSessionId == sessionId)
                IsLoading = false;
        }
    }

    /// <summary>
    /// Цикл обновления текущей позиции воспроизведения.
    /// </summary>
    private async Task TrackPositionLoopAsync(Guid sessionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _currentSessionId == sessionId)
        {
            if (_outputDevice.PlaybackState == PlaybackState.Playing && _currentStream != null)
            {
                _currentPosition = _currentStream.CurrentTime;
                OnPositionChanged?.Invoke(_currentPosition);
            }
            await Task.Delay(250, ct);
        }
    }

    #endregion

    #region Управление очередью

    public void Enqueue(TrackInfo track)
    {
        lock (_lock)
        {
            if (_outputDevice.PlaybackState == PlaybackState.Stopped && !IsLoading)
                _ = PlayTrackAsync(track);
            else
                _queue.Enqueue(track);
        }
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var t in tracks) Enqueue(t);
    }

    public void ClearQueue() { lock (_lock) _queue.Clear(); }

    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;
        lock (_lock)
        {
            if (RepeatMode == RepeatMode.RepeatOne && CurrentTrack != null)
            {
                next = CurrentTrack;
            }
            else if (ShuffleEnabled && _queue.Count > 0)
            {
                var list = _queue.ToList();
                int idx = Random.Shared.Next(list.Count);
                next = list[idx];
                list.RemoveAt(idx);
                _queue.Clear();
                foreach (var t in list) _queue.Enqueue(t);
            }
            else if (_queue.TryDequeue(out var queued))
            {
                next = queued;
            }
        }

        if (next != null) await PlayTrackAsync(next);
        else Stop();
    }

    public async Task PlayPreviousAsync()
    {
        TrackInfo? prev = null;
        lock (_lock)
        {
            if (_historyIndex > 0)
            {
                _historyIndex--;
                prev = _history[_historyIndex];
            }
        }
        if (prev != null) await PlayTrackAsync(prev);
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.Count > 0 && _history.Last().Id == track.Id) return;

        // Если мы переключились назад и начали играть что-то новое, обрезаем "будущую" историю
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(track);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    #endregion

    #region Транспортные функции

    public void TogglePlayPause()
    {
        if (IsPlaying) _outputDevice.Pause();
        else if (IsPaused) _outputDevice.Play();
        else if (CurrentTrack != null) _ = PlayTrackAsync(CurrentTrack);
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_currentStream == null) return;

            // Ограничение в рамках длительности трека
            var target = position;
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (target > _currentStream.TotalTime) target = _currentStream.TotalTime;

            _currentStream.CurrentTime = target;
            _currentPosition = target;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isManualStop = true;
            _currentSessionId = Guid.NewGuid(); // Сброс сессии
            StopInternal(true);
            _isManualStop = false;
        }
    }

    private void StopInternal(bool clearCurrent)
    {
        _outputDevice.Stop();

        _currentStream?.Dispose();
        _currentStream = null;
        _volumeControl = null;

        if (clearCurrent)
        {
            CurrentTrack = null;
            _currentPosition = TimeSpan.Zero;
            OnTrackChanged?.Invoke(null);
        }
    }

    #endregion

    #region Обработка событий NAudio

    private void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_isManualStop || IsLoading) return;

        if (e.Exception != null)
        {
            Debug.WriteLine($"[AudioEngine] Device Error: {e.Exception.Message}");
            OnError?.Invoke(e.Exception.Message);
        }

        // Проверяем, завершился ли трек естественным образом (осталось менее 1 сек)
        bool finished = _currentStream != null &&
                        (_currentStream.TotalTime - _currentStream.CurrentTime).TotalSeconds < 1.5;

        if (finished)
        {
            _ = PlayNextAsync();
        }
        else
        {
            OnPlaybackStopped?.Invoke();
        }
    }

    #endregion

    public void Dispose()
    {
        _streamCts?.Cancel();
        _outputDevice.PlaybackStopped -= OnDevicePlaybackStopped;
        _outputDevice.Dispose();
        _currentStream?.Dispose();
        _streamCts?.Dispose();
    }
}