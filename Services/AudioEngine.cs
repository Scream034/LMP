using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MyLiteMusicPlayer.Models;
using PlaybackState = NAudio.Wave.PlaybackState;

namespace MyLiteMusicPlayer.Services;

public class AudioEngine : IDisposable
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;
    private readonly DownloadService _downloadService;

    // Устройство вывода - пересоздаём при ошибках
    private IWavePlayer? _outputDevice;
    private readonly object _deviceLock = new();

    private WaveStream? _currentStream;
    private VolumeSampleProvider? _volumeControl;

    // Ключевое: семафор гарантирует что только ОДНА операция воспроизведения активна
    private readonly SemaphoreSlim _playbackSemaphore = new(1, 1);

    private CancellationTokenSource? _positionLoopCts;
    private volatile int _sessionVersion; // Атомарный счётчик сессий
    private bool _isManualStop;
    private float _userVolumeMultiplier = 1.0f;

    private readonly Queue<TrackInfo> _queue = new();
    private readonly List<TrackInfo> _history = [];
    private int _historyIndex = -1;

    public TrackInfo? CurrentTrack { get; private set; }
    public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _outputDevice?.PlaybackState == PlaybackState.Paused;

    private TimeSpan _currentPosition;
    public TimeSpan CurrentPosition => _currentPosition;
    public TimeSpan TotalDuration => _currentStream?.TotalTime ?? CurrentTrack?.Duration ?? TimeSpan.Zero;

    private volatile bool _isLoading;
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

    public event Action<bool>? OnLoadingChanged;
    public event Action<TrackInfo?>? OnTrackChanged;
    public event Action? OnPlaybackStopped;
    public event Action<string>? OnError;
    public event Action<TimeSpan>? OnPositionChanged;

    public AudioEngine(
        YoutubeProvider youtube,
        LibraryService library,
        DownloadService downloadService)
    {
        _youtube = youtube;
        _library = library;
        _downloadService = downloadService;

        InitializeOutputDevice();

        _userVolumeMultiplier = _library.Data.Volume;
        ShuffleEnabled = _library.Data.ShuffleEnabled;
        RepeatMode = _library.Data.RepeatMode;
    }

    private void InitializeOutputDevice()
    {
        lock (_deviceLock)
        {
            try
            {
                _outputDevice?.Dispose();
            }
            catch { }

            _outputDevice = new WaveOutEvent
            {
                DesiredLatency = 150,
                NumberOfBuffers = 3
            };
            _outputDevice.PlaybackStopped += OnDevicePlaybackStopped;
        }
    }

    #region Volume Control

    public void SetVolume(float multiplier)
    {
        _userVolumeMultiplier = Math.Clamp(multiplier, 0f, 4f);

        _library.Data.Volume = _userVolumeMultiplier;
        _library.Save();

        if (_volumeControl != null)
        {
            float dbGain = _library.Data.TargetGainDb;
            float dbMultiplier = (float)Math.Pow(10, dbGain / 20.0);
            _volumeControl.Volume = _userVolumeMultiplier * dbMultiplier;
        }
    }

    public float GetVolume() => _userVolumeMultiplier;

    #endregion

    #region Playback

    public async Task PlayTrackAsync(TrackInfo track)
    {
        if (track == null) return;

        // 1. Инкрементируем версию сессии - все старые операции станут невалидными
        int mySession = Interlocked.Increment(ref _sessionVersion);

        Debug.WriteLine($"[AudioEngine] === Session {mySession}: Starting '{track.Title}' ===");

        // 2. Показываем UI сразу
        IsLoading = true;
        CurrentTrack = track;
        _currentPosition = TimeSpan.Zero;
        OnTrackChanged?.Invoke(track);

        // 3. Ждём семафор - гарантирует что предыдущая операция завершена
        bool acquired = await _playbackSemaphore.WaitAsync(TimeSpan.FromSeconds(10));
        if (!acquired)
        {
            Debug.WriteLine($"[AudioEngine] Session {mySession}: Semaphore timeout, forcing...");
            // Принудительно сбрасываем состояние
            ForceReset();
            await _playbackSemaphore.WaitAsync();
        }

        try
        {
            // 4. Проверяем актуальность сессии
            if (_sessionVersion != mySession)
            {
                Debug.WriteLine($"[AudioEngine] Session {mySession}: Already superseded, aborting");
                return;
            }

            // 5. Останавливаем текущее воспроизведение
            StopCurrentPlayback();

            // 6. Создаём поток (может занять время)
            var sw = Stopwatch.StartNew();
            WaveStream? reader = null;

            try
            {
                reader = await CreateStreamReaderAsync(track, mySession);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[AudioEngine] Session {mySession}: Cancelled during stream creation");
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Session {mySession}: Stream error: {ex.Message}");
                OnError?.Invoke($"Ошибка загрузки: {ex.Message}");
                return;
            }

            // 7. Ещё раз проверяем сессию ПОСЛЕ создания потока
            if (_sessionVersion != mySession)
            {
                Debug.WriteLine($"[AudioEngine] Session {mySession}: Superseded after stream creation");
                SafeDispose(reader);
                return;
            }

            sw.Stop();
            Debug.WriteLine($"[AudioEngine] Session {mySession}: Stream ready in {sw.ElapsedMilliseconds}ms");

            // 8. Настраиваем воспроизведение
            if (!SetupPlayback(reader, mySession))
            {
                SafeDispose(reader);
                return;
            }

            // 9. Запускаем
            AddToHistory(track);
            StartPositionLoop(mySession);
        }
        finally
        {
            // Освобождаем семафор только если мы актуальная сессия
            if (_sessionVersion == mySession)
            {
                IsLoading = false;
            }
            _playbackSemaphore.Release();
        }
    }

    private async Task<WaveStream> CreateStreamReaderAsync(TrackInfo track, int session)
    {
        // Локальный файл - быстрый путь
        if (track.IsDownloaded && File.Exists(track.LocalPath))
        {
            Debug.WriteLine($"[AudioEngine] Session {session}: Using local file");
            return new AudioFileReader(track.LocalPath);
        }

        // Получаем stream URL
        string? streamUrl = track.StreamUrl;
        if (string.IsNullOrEmpty(streamUrl))
        {
            Debug.WriteLine($"[AudioEngine] Session {session}: Refreshing stream URL...");

            using var urlCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            streamUrl = await _youtube.RefreshStreamUrlAsync(track, urlCts.Token);
        }

        if (string.IsNullOrEmpty(streamUrl))
            throw new Exception("Не удалось получить ссылку на аудиопоток");

        // Проверяем сессию перед долгой операцией
        if (_sessionVersion != session)
            throw new OperationCanceledException();

        // Создаём MediaFoundationReader
        // ВАЖНО: Это синхронная блокирующая операция, её нельзя отменить!
        Debug.WriteLine($"[AudioEngine] Session {session}: Creating MediaFoundationReader...");
        var sw = Stopwatch.StartNew();

        var reader = await Task.Run(() =>
        {
            try
            {
                return new MediaFoundationReader(streamUrl, new MediaFoundationReader.MediaFoundationReaderSettings
                {
                    RequestFloatOutput = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Session {session}: MFR creation failed: {ex.Message}");
                throw;
            }
        });

        sw.Stop();
        Debug.WriteLine($"[AudioEngine] Session {session}: MFR created in {sw.ElapsedMilliseconds}ms");

        return reader;
    }

    private bool SetupPlayback(WaveStream reader, int session)
    {
        lock (_deviceLock)
        {
            if (_sessionVersion != session)
            {
                Debug.WriteLine($"[AudioEngine] Session {session}: Superseded in SetupPlayback");
                return false;
            }

            try
            {
                _currentStream = reader;

                var sampleProvider = reader.ToSampleProvider();
                _volumeControl = new VolumeSampleProvider(sampleProvider);
                SetVolume(_userVolumeMultiplier);

                // Пересоздаём устройство если нужно
                if (_outputDevice == null)
                {
                    InitializeOutputDevice();
                }

                _outputDevice!.Init(_volumeControl);
                _outputDevice.Play();

                Debug.WriteLine($"[AudioEngine] Session {session}: Playback started");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Session {session}: Setup failed: {ex.Message}");

                // Пробуем пересоздать устройство
                try
                {
                    InitializeOutputDevice();
                    _outputDevice!.Init(_volumeControl);
                    _outputDevice.Play();
                    Debug.WriteLine($"[AudioEngine] Session {session}: Recovered after device reinit");
                    return true;
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"[AudioEngine] Session {session}: Recovery failed: {ex2.Message}");
                    OnError?.Invoke($"Ошибка воспроизведения: {ex.Message}");
                    return false;
                }
            }
        }
    }

    private void StopCurrentPlayback()
    {
        // Останавливаем position loop
        _positionLoopCts?.Cancel();
        _positionLoopCts = null;

        lock (_deviceLock)
        {
            // Останавливаем устройство
            try
            {
                _outputDevice?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioEngine] Stop device error: {ex.Message}");
            }

            // Диспозим поток
            var oldStream = _currentStream;
            _currentStream = null;
            _volumeControl = null;

            if (oldStream != null)
            {
                _ = Task.Run(() => SafeDispose(oldStream));
            }
        }
    }

    private void ForceReset()
    {
        Debug.WriteLine($"[AudioEngine] Force reset!");

        _positionLoopCts?.Cancel();
        _positionLoopCts = null;

        lock (_deviceLock)
        {
            try { _outputDevice?.Stop(); } catch { }
            try { _outputDevice?.Dispose(); } catch { }
            _outputDevice = null;

            var oldStream = _currentStream;
            _currentStream = null;
            _volumeControl = null;

            if (oldStream != null)
            {
                _ = Task.Run(() => SafeDispose(oldStream));
            }
        }

        InitializeOutputDevice();
    }

    private void StartPositionLoop(int session)
    {
        _positionLoopCts?.Cancel();
        _positionLoopCts = new CancellationTokenSource();
        var ct = _positionLoopCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && _sessionVersion == session)
                {
                    if (_outputDevice?.PlaybackState == PlaybackState.Playing && _currentStream != null)
                    {
                        try
                        {
                            _currentPosition = _currentStream.CurrentTime;
                            OnPositionChanged?.Invoke(_currentPosition);
                        }
                        catch { }
                    }
                    await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private static void SafeDispose(IDisposable? disposable)
    {
        if (disposable == null) return;

        try
        {
            var sw = Stopwatch.StartNew();
            disposable.Dispose();
            sw.Stop();

            if (sw.ElapsedMilliseconds > 100)
            {
                Debug.WriteLine($"[AudioEngine] ⚠️ Slow dispose: {sw.ElapsedMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Dispose error (ignored): {ex.Message}");
        }
    }

    #endregion

    #region Queue Management

    public void Enqueue(TrackInfo track)
    {
        if (_outputDevice?.PlaybackState == PlaybackState.Stopped && !IsLoading)
            _ = PlayTrackAsync(track);
        else
            _queue.Enqueue(track);
    }

    public void EnqueueRange(IEnumerable<TrackInfo> tracks)
    {
        foreach (var t in tracks) Enqueue(t);
    }

    public void ClearQueue()
    {
        _queue.Clear();
    }

    public async Task PlayNextAsync()
    {
        TrackInfo? next = null;

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

        if (next != null)
        {
            await PlayTrackAsync(next);
        }
        else
        {
            Stop();
        }
    }

    public async Task PlayPreviousAsync()
    {
        TrackInfo? prev = null;

        if (_historyIndex > 0)
        {
            _historyIndex--;
            prev = _history[_historyIndex];
        }

        if (prev != null)
        {
            await PlayTrackAsync(prev);
        }
    }

    private void AddToHistory(TrackInfo track)
    {
        if (_history.Count > 0 && _history.Last().Id == track.Id) return;

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(track);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    #endregion

    #region Transport Controls

    public void TogglePlayPause()
    {
        lock (_deviceLock)
        {
            if (_outputDevice == null) return;

            if (IsPlaying)
            {
                _outputDevice.Pause();
            }
            else if (IsPaused)
            {
                _outputDevice.Play();
            }
            else if (CurrentTrack != null)
            {
                _ = PlayTrackAsync(CurrentTrack);
            }
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_currentStream == null) return;

        var target = position;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (target > _currentStream.TotalTime) target = _currentStream.TotalTime;

        try
        {
            _currentStream.CurrentTime = target;
            _currentPosition = target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Seek error: {ex.Message}");
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _sessionVersion);
        _isManualStop = true;

        StopCurrentPlayback();

        CurrentTrack = null;
        _currentPosition = TimeSpan.Zero;
        OnTrackChanged?.Invoke(null);
        IsLoading = false;

        _isManualStop = false;
    }

    #endregion

    #region Event Handlers

    private void OnDevicePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_isManualStop || IsLoading) return;

        if (e.Exception != null)
        {
            Debug.WriteLine($"[AudioEngine] Device Error: {e.Exception.Message}");
            OnError?.Invoke(e.Exception.Message);
            return;
        }

        // Проверяем, действительно ли трек закончился
        bool finished = _currentStream != null &&
                        _currentStream.TotalTime.TotalSeconds > 0 &&
                        (_currentStream.TotalTime - _currentStream.CurrentTime).TotalSeconds < 1.5;

        if (finished)
        {
            Debug.WriteLine($"[AudioEngine] Track finished, playing next");
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
        Interlocked.Increment(ref _sessionVersion);
        _positionLoopCts?.Cancel();

        lock (_deviceLock)
        {
            if (_outputDevice != null)
            {
                _outputDevice.PlaybackStopped -= OnDevicePlaybackStopped;
                _outputDevice.Dispose();
            }
            _currentStream?.Dispose();
        }

        _positionLoopCts?.Dispose();
        _playbackSemaphore.Dispose();
    }
}