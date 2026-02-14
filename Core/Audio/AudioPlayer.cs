using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Http;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Sources;
using LMP.Core.Exceptions;
using LMP.Core.Helpers;

namespace LMP.Core.Audio;

/// <summary>
/// Опции конфигурации аудиоплеера.
/// </summary>
public sealed class AudioPlayerOptions
{
    /// <summary>Callback для обновления протухших ссылок (YouTube/CDN).</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? UrlRefreshCallback { get; init; }
    
    /// <summary>Интервал обновления события PositionChanged.</summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    
    /// <summary>Максимальное количество попыток переподключения при разрыве сети.</summary>
    public int MaxRetryAttempts { get; init; } = 3;
    
    /// <summary>Задержка между попытками переподключения.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    
    /// <summary>Использовать NullBackend (без звука) для тестов или headless режима.</summary>
    public bool UseNullBackend { get; init; }
}

/// <summary>
/// Высокопроизводительный аудио-плеер.
/// Координирует работу Source -> Decoder -> Buffer -> Backend.
/// </summary>
public sealed class AudioPlayer : IAudioPlayer
{
    #region Constants

    private const int DefaultSampleRate = 48000;
    private const int DefaultChannels = 2;
    private const int BufferSizeSeconds = 2;     // 2 секунды PCM буфера (достаточно для стабильности)
    private const int MinBufferMs = 500;         // Минимальный буфер для старта (0.5с)
    private const int StopTimeoutMs = 2000;      // Таймаут ожидания остановки потоков
    private const int DecoderBufferFrames = 8192;// Размер буфера декодирования (фреймов)

    #endregion

    #region Fields

    private readonly AudioPlayerOptions _options;
    private readonly AudioCacheManager? _cacheManager;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    // Pipeline Components
    private IAudioSource? _source;
    private IAudioDecoder? _decoder;
    private IPlaybackBackend? _backend;

    // Data Buffers
    private CircularBuffer<float>? _pcmBuffer;
    private float[]? _decodeBuffer;

    // State
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private volatile float _volume = 1.0f;
    private volatile bool _disposed;
    
    // Timekeeping
    private long _durationMs;
    private long _playedSamples; // Количество семплов, переданных в backend (basis for Position)

    // Context
    private string? _currentTrackId;
    private string? _currentUrl;

    // Tasks & Timers
    private CancellationTokenSource? _playbackCts;
    private Task? _decoderTask;
    private Timer? _positionTimer;

    #endregion

    #region Properties

    /// <inheritdoc />
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 2f);
            if (_backend != null)
            {
                // Передаем в backend громкость <= 1.0 (аппаратное/драйверное управление).
                // Если volume > 1.0, усиление применяется программно в AudioCallback.
                _backend.Volume = Math.Min(_volume, 1f);
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Position
    {
        get
        {
            if (_decoder == null || _decoder.SampleRate <= 0 || _backend == null)
                return TimeSpan.Zero;

            // Алгоритм точного времени:
            // 1. Берем сколько всего семплов мы "скормили" бэкенду (_playedSamples).
            // 2. Вычитаем то, что бэкенд еще держит в своем внутреннем буфере (BufferedSamples).
            // 3. Получаем реальное количество семплов, ушедших на динамики.
            
            long totalWritten = Volatile.Read(ref _playedSamples);
            int backendBuffered = _backend.BufferedSamples;
            long heardSamples = Math.Max(0, totalWritten - backendBuffered);

            double seconds = (double)heardSamples / (_decoder.SampleRate * _decoder.Channels);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    /// <inheritdoc />
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Volatile.Read(ref _durationMs));

    /// <inheritdoc />
    public PlaybackState State => _state;

    public double BufferProgress => _source?.BufferProgress ?? 0;
    public bool IsFullyBuffered => _source?.IsFullyBuffered ?? false;

    #endregion

    #region Events

    public event Action<TimeSpan>? PositionChanged;
    public event Action<PlaybackState>? StateChanged;
    public event Action? TrackEnded;
    public event Action<Exception>? ErrorOccurred;

    #endregion

    public AudioPlayer(AudioPlayerOptions? options = null, AudioCacheManager? cacheManager = null)
    {
        _options = options ?? new AudioPlayerOptions();
        _cacheManager = cacheManager;
    }

    #region Public Methods

    /// <inheritdoc />
    public async Task PlayAsync(string url, string? trackId = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateLock.WaitAsync(ct);
        try
        {
            // Сброс предыдущего состояния
            await StopInternalAsync();

            _currentUrl = url;
            _currentTrackId = trackId;

            SetState(PlaybackState.Loading);

            // Инициализация пайплайна
            await InitializePlaybackAsync(url, ct);

            // Запуск декодера
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _decoderTask = Task.Run(() => DecoderLoopAsync(_playbackCts.Token), _playbackCts.Token);

            // Ожидание предварительной буферизации
            SetState(PlaybackState.Buffering);
            await WaitForBufferAsync(_playbackCts.Token);

            // Старт воспроизведения
            _backend!.Start();

            // Таймер UI обновлений
            _positionTimer = new Timer(
                _ => PositionChanged?.Invoke(Position),
                null,
                0,
                (int)_options.PositionUpdateInterval.TotalMilliseconds);

            SetState(PlaybackState.Playing);
            Log.Info($"[AudioPlayer] Started track: {trackId ?? "unknown"}");
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Play failed: {ex.Message}", ex);
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(ex);
            
            // Чистим ресурсы при ошибке старта
            await StopInternalAsync();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_state != PlaybackState.Playing) return;
        _backend?.Stop();
        SetState(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Resume()
    {
        if (_state != PlaybackState.Paused) return;
        _backend?.Start();
        SetState(PlaybackState.Playing);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_state == PlaybackState.Stopped) return;
        // Fire-and-forget, но безопасно
        _ = StopAsync();
    }

    /// <summary>
    /// Асинхронная остановка с ожиданием завершения потоков.
    /// </summary>
    public async Task StopAsync()
    {
        if (_state == PlaybackState.Stopped) return;
        
        await _stateLock.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _stateLock.Release();
        }
        Log.Info("[AudioPlayer] Stopped");
    }

    /// <inheritdoc />
    public async ValueTask SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_source == null || !_source.CanSeek || _decoder == null) return;

        await _stateLock.WaitAsync(ct);
        try
        {
            var wasPlaying = _state == PlaybackState.Playing;
            SetState(PlaybackState.Buffering);

            // 1. Останавливаем вывод звука и чистим буферы
            _backend?.Stop();
            _pcmBuffer?.Clear();

            // 2. Выполняем seek на источнике
            long posMs = (long)position.TotalMilliseconds;
            bool success = await _source.SeekAsync(posMs, ct);

            if (success)
            {
                // 3. Атомарно обновляем счетчик семплов для корректного отображения времени
                // Position = (PlayedSamples - Buffered) / Rate
                // Устанавливаем PlayedSamples так, чтобы формула вернула запрошенное время
                long newSampleCount = (long)(position.TotalSeconds * _decoder.SampleRate * _decoder.Channels);
                Volatile.Write(ref _playedSamples, newSampleCount);

                Log.Debug($"[AudioPlayer] Seeked to {posMs}ms");
            }

            // 4. Возобновляем воспроизведение
            if (wasPlaying)
            {
                await WaitForBufferAsync(ct);
                _backend?.Start();
                SetState(PlaybackState.Playing);
            }
            else
            {
                SetState(PlaybackState.Paused);
            }

            // Мгновенное обновление UI
            PositionChanged?.Invoke(position);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    #endregion

    #region Internal Logic

    private async Task InitializePlaybackAsync(string url, CancellationToken ct)
    {
        // 1. Создание источника (с кэшем или без)
        if (_cacheManager != null && !string.IsNullOrEmpty(_currentTrackId))
        {
            _source = await AudioSourceFactory.CreateWithCacheAsync(
                url, _currentTrackId, SharedHttpClient.Instance, _cacheManager, CreateUrlRefresher(), ct);
        }
        else
        {
            _source = await AudioSourceFactory.CreateAsync(
                url, SharedHttpClient.Instance, CreateUrlRefresher(), _currentTrackId, ct);
        }

        if (!await _source.InitializeAsync(ct))
            throw new AudioSourceException("Failed to initialize audio source");

        // 2. Создание декодера
        _decoder = CreateDecoder(_source);
        Volatile.Write(ref _durationMs, _source.DurationMs);

        // 3. Создание буферов
        // 2 секунды буфера достаточно для компенсации джиттера сети, не создавая большой задержки
        int bufferSize = _decoder.SampleRate * _decoder.Channels * BufferSizeSeconds;
        _pcmBuffer = new CircularBuffer<float>(bufferSize);
        _decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * _decoder.Channels);

        // 4. Создание бэкенда
        _backend = CreateBackend();
        _backend.Volume = Math.Min(_volume, 1f);
        
        // Инициализация бэкенда с передачей callback-функции
        _backend.Initialize(_decoder.SampleRate, _decoder.Channels, AudioCallback);

        Log.Debug($"[AudioPlayer] Initialized: {_source.Codec} -> PCM {_decoder.SampleRate}Hz -> {_backend.Name}");
    }

    private IAudioDecoder CreateDecoder(IAudioSource source)
    {
        int rate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate;
        int ch = source.Channels > 0 ? source.Channels : DefaultChannels;

        return source.Codec switch
        {
            AudioCodec.Opus => new OpusDecoder(rate, ch),
            AudioCodec.Aac => CreateAacDecoder(source, rate, ch),
            _ => throw new NotSupportedException($"Codec {source.Codec} not supported")
        };
    }

    private static AacDecoder CreateAacDecoder(IAudioSource source, int rate, int ch)
    {
        var dec = new AacDecoder(rate, ch);
        if (source.DecoderConfig != null) dec.Initialize(source.DecoderConfig);
        return dec;
    }

    private IPlaybackBackend CreateBackend()
    {
        if (_options.UseNullBackend) return new NullAudioBackend();
        
        try 
        { 
            return new NAudioBackend(); 
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] NAudio init failed: {ex.Message}, falling back to NullBackend");
            return new NullAudioBackend();
        }
    }

    private async Task DecoderLoopAsync(CancellationToken ct)
    {
        if (_source == null || _decoder == null || _pcmBuffer == null || _decodeBuffer == null) return;

        int retryCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Проверка места в буфере
                // Если буфер полон, ждем. Используем Task.Delay для разгрузки CPU.
                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                // 2. Чтение фрейма
                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct);
                    retryCount = 0; // Сброс счетчика ошибок при успехе
                }
                catch (UrlExpiredException)
                {
                    if (await TryRefreshUrlAsync(ct)) continue;
                    throw;
                }
                catch (Exception) when (retryCount++ < _options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPlayer] Read retry {retryCount}/{_options.MaxRetryAttempts}");
                    await Task.Delay(_options.RetryDelay, ct);
                    continue;
                }

                // 3. Конец потока
                if (frame == null)
                {
                    // Ждем пока буфер опустеет (доиграет)
                    while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(50, ct);
                    }
                    OnTrackEnded();
                    break;
                }

                // 4. Декодирование
                try
                {
                    int samplesDecoded = _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                    
                    if (samplesDecoded > 0)
                    {
                        _pcmBuffer.Write(_decodeBuffer.AsSpan(0, samplesDecoded * _decoder.Channels));
                        // Примечание: Position не обновляется здесь, он зависит от AudioCallback
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioPlayer] Decode frame failed: {ex.Message}");
                    // Пропускаем битый фрейм
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальная остановка
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPlayer] Decoder loop fatal error: {ex.Message}", ex);
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(ex);
        }
    }

    /// <summary>
    /// Callback, вызываемый бэкендом (аудио-потоком) для получения PCM данных.
    /// Критический путь: никаких аллокаций, блокировок или тяжелых операций.
    /// </summary>
    private int AudioCallback(Span<float> buffer)
    {
        if (_state != PlaybackState.Playing || _pcmBuffer == null)
        {
            buffer.Clear();
            return 0;
        }

        // Читаем данные из циклического буфера
        int read = _pcmBuffer.Read(buffer);

        if (read > 0)
        {
            // Увеличиваем глобальный счетчик воспроизведенных семплов.
            // Это основа для свойства Position.
            Interlocked.Add(ref _playedSamples, read);

            // Применяем программную громкость (SIMD оптимизация)
            // Backend применяет Hardware Volume (<= 1.0), мы применяем Software Boost (> 1.0)
            if (_volume > 1.0f)
            {
                ApplyVolumeSimd(buffer[..read], _volume);
            }
        }

        // Заполняем остаток тишиной (для стабильности драйверов)
        if (read < buffer.Length)
        {
            buffer[read..].Clear();
        }

        // Возвращаем количество фреймов (семплы / каналы)
        return read / (_decoder?.Channels ?? 2);
    }

    /// <summary>
    /// Применяет громкость к буферу с использованием векторных инструкций (AVX/SSE).
    /// </summary>
    private static void ApplyVolumeSimd(Span<float> data, float volume)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vecVol = new Vector<float>(volume);
            var min = new Vector<float>(-1.0f);
            var max = new Vector<float>(1.0f);
            int vecSize = Vector<float>.Count;
            
            var span = MemoryMarshal.Cast<float, Vector<float>>(data);
            for (int j = 0; j < span.Length; j++)
            {
                var v = span[j] * vecVol;
                // Clamp значения между -1.0 и 1.0
                v = Vector.Min(Vector.Max(v, min), max);
                span[j] = v;
            }
            i = span.Length * vecSize;
        }

        // Обработка хвоста (или если SIMD недоступен)
        for (; i < data.Length; i++)
        {
            data[i] = Math.Clamp(data[i] * volume, -1f, 1f);
        }
    }

    private async Task WaitForBufferAsync(CancellationToken ct)
    {
        if (_decoder == null || _pcmBuffer == null) return;
        
        // Ждем заполнения минимум 500мс или полной загрузки трека
        int threshold = _decoder.SampleRate * _decoder.Channels * MinBufferMs / 1000;

        while (_pcmBuffer.Count < threshold && !ct.IsCancellationRequested && _source?.IsFullyBuffered == false)
        {
            await Task.Delay(10, ct);
        }
    }

    private async Task<bool> TryRefreshUrlAsync(CancellationToken ct)
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return false;

        try
        {
            var newUrl = await _options.UrlRefreshCallback(_currentTrackId, ct);
            if (!string.IsNullOrEmpty(newUrl))
            {
                _currentUrl = newUrl;
                Log.Info("[AudioPlayer] URL refreshed successfully");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPlayer] URL refresh failed: {ex.Message}");
        }
        return false;
    }

    private Func<CancellationToken, Task<string?>>? CreateUrlRefresher()
    {
        if (_options.UrlRefreshCallback == null || string.IsNullOrEmpty(_currentTrackId))
            return null;

        var trackId = _currentTrackId;
        var callback = _options.UrlRefreshCallback;
        return ct => callback(trackId, ct).AsTask();
    }

    private async Task StopInternalAsync()
    {
        _playbackCts?.Cancel();

        if (_decoderTask != null)
        {
            try { await _decoderTask.WaitAsync(TimeSpan.FromMilliseconds(StopTimeoutMs)); }
            catch { /* Игнорируем ошибки ожидания */ }
        }

        _positionTimer?.Dispose();
        _positionTimer = null;

        _backend?.Dispose();
        _backend = null;

        _decoder?.Dispose();
        _decoder = null;

        if (_source != null)
        {
            await _source.DisposeAsync();
            _source = null;
        }

        if (_decodeBuffer != null)
        {
            ArrayPool<float>.Shared.Return(_decodeBuffer);
            _decodeBuffer = null;
        }

        _pcmBuffer = null; // GC соберет
        
        // Сброс счетчиков
        Volatile.Write(ref _playedSamples, 0);
        Volatile.Write(ref _durationMs, 0);
        
        _currentTrackId = null;
        _currentUrl = null;
        
        _playbackCts?.Dispose();
        _playbackCts = null;
        _decoderTask = null;

        SetState(PlaybackState.Stopped);
    }

    private void OnTrackEnded()
    {
        if (_state != PlaybackState.Stopped)
        {
            TrackEnded?.Invoke();
        }
        SetState(PlaybackState.Stopped);
    }

    private void SetState(PlaybackState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(newState);
    }

    #endregion

    #region Dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; // Fix CS0649

        // Синхронный стоп
        Stop();
        _stateLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; // Fix CS0649

        await StopAsync();
        _stateLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}