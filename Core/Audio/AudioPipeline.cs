using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Audio.Normalization;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Полный конвейер воспроизведения: Source → Decoder → PCM Buffer → Normalization → Gain → Backend.
///
/// <para><b>Архитектура громкости:</b></para>
/// <para>Gain (volume curve, boost, user dB) применяется программно к PCM сэмплам
/// в <see cref="AudioCallback"/>, НЕ через hardware volume backend'а.</para>
///
/// <para><b>Нормализация (EBU R128 / ITU-R BS.1770-4):</b></para>
/// <para>Делегирована <see cref="EbuR128Analyzer"/> — отдельному модулю,
/// отвечающему за K-weighted LUFS анализ, gating blocks и gain state machine.</para>
///
/// <para><b>Thread model (ИСПРАВЛЕНО):</b></para>
/// <list type="bullet">
///   <item>Decoder loop — <see cref="Task.Run"/> на ThreadPool (честный async).
///     Предыдущая версия использовала dedicated thread с <c>.GetAwaiter().GetResult()</c>,
///     что приводило к thread-hopping: после первого <c>await</c> continuation уходил
///     в ThreadPool, а выделенный AboveNormal поток блокировался навечно.</item>
///   <item>AudioCallback — вызывается из fill thread NAudioBackend.</item>
///   <item><see cref="_gain"/> — volatile float, lock-free read/write.</item>
///   <item>Normalization state — инкапсулирован в <see cref="EbuR128Analyzer"/>.</item>
/// </list>
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    #region Constants

    private const int ShortTrackIdLength = 8;
    private const int BufferFullDelayMs = 5;
    private const int DrainMinDelayMs = 50;
    private const int DrainMaxDelayMs = 500;
    private const int WaitBufferDelayMs = 10;
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    #endregion

    #region Fields

    private readonly IAudioSource _source;
    private readonly IAudioDecoder _decoder;
    private readonly IPlaybackBackend _backend;
    private readonly LockFreeRingBuffer<float> _pcmBuffer;
    private readonly float[] _decodeBuffer;
    private readonly AudioStreamInfo _streamInfo;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly EbuR128Analyzer _analyzer;
    private TruePeakLimiter? _truePeakLimiter;
    private GainCrossfader _gainCrossfader;

    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private volatile bool _disposed;

    private int _skipFramesCounter;
    private long _decodedSamples;
    private long _seekTargetMs = -1;
    private volatile bool _deviceLost;
    private Action? _onDeviceLostExternal;
    private Action? _onDeviceAvailableExternal;

    /// <summary>
    /// Task последнего запущенного обработчика device-события (lost / available).
    /// Не <c>volatile</c> — доступ строго через <see cref="Volatile.Read"/>
    /// и <see cref="Volatile.Write"/> для корректной публикации без
    /// конфликта с передачей по <c>ref</c>.
    /// Хранится для ожидания в <see cref="DisposeAsync"/>: исключает гонку
    /// между обработчиком, вызывающим <c>pipeline.StartDecoding</c>,
    /// и освобождением ресурсов pipeline.
    /// </summary>
    private Task? _deviceEventTask;

#if DEBUG
    /// <summary>
    /// Счётчик перезапусков decoder loop.
    /// Публикуется через <see cref="DecoderRestartCount"/> для observability.
    /// Компилируется только в DEBUG — в Release overhead отсутствует.
    /// </summary>
    private int _decoderRestartCount;
#endif

    #endregion

    #region Properties

    /// <summary>Потеряно ли аудиоустройство.</summary>
    public bool IsDeviceLost => _deviceLost;

    /// <summary>Метаинформация об аудиопотоке.</summary>
    public AudioStreamInfo StreamInfo => _streamInfo;

    /// <summary>Источник сырых аудио-фреймов.</summary>
    public IAudioSource Source => _source;

    /// <summary>Декодер аудио.</summary>
    public IAudioDecoder Decoder => _decoder;

    /// <summary>Backend системного звука.</summary>
    public IPlaybackBackend Backend => _backend;

    /// <summary>Pipeline уничтожен.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Sample rate декодера.</summary>
    public int SampleRate => _decoder.SampleRate;

    /// <summary>Количество каналов декодера.</summary>
    public int Channels => _decoder.Channels;

    /// <summary>Количество воспроизведённых сэмплов (decoded - buffered).</summary>
    public long PlayedSamples => Interlocked.Read(ref _decodedSamples) - _pcmBuffer.Count;

    /// <summary>Количество сэмплов в backend буфере.</summary>
    public int BackendBufferedSamples => _backend.BufferedSamples;

    /// <summary>Количество сэмплов в PCM ring buffer.</summary>
    public int BufferedSamples => _pcmBuffer.Count;

    /// <summary>Токен отмены времени жизни pipeline.</summary>
    public CancellationToken LifetimeToken => _lifetimeCts.Token;

    /// <summary>EBU R128 анализатор нормализации.</summary>
    public EbuR128Analyzer Analyzer => _analyzer;

#if DEBUG
    /// <summary>
    /// Количество перезапусков decoder loop с момента создания pipeline.
    /// Значение &gt; 10 указывает на нестабильный источник или сетевые проблемы.
    /// Доступно только в DEBUG-сборках.
    /// </summary>
    public int DecoderRestartCount => Volatile.Read(ref _decoderRestartCount);
#endif

    #endregion

    #region Constructor

    private AudioPipeline(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        LockFreeRingBuffer<float> pcmBuffer,
        float[] decodeBuffer,
        AudioStreamInfo streamInfo,
        CancellationTokenSource lifetimeCts)
    {
        _source = source;
        _decoder = decoder;
        _backend = backend;
        _pcmBuffer = pcmBuffer;
        _decodeBuffer = decodeBuffer;
        _streamInfo = streamInfo;
        _lifetimeCts = lifetimeCts;

        _analyzer = new EbuR128Analyzer(decoder.SampleRate, decoder.Channels);
        _truePeakLimiter = new TruePeakLimiter(decoder.SampleRate);
        _gainCrossfader = new GainCrossfader(1.0f);
    }

    #endregion

    #region Factory

    /// <summary>
    /// Создаёт pipeline с shared backend.
    /// </summary>
    public static async Task<AudioPipeline> CreateAsync(
        string url,
        string? trackId,
        int bitrateHint,
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        IPlaybackBackend sharedBackend,
        CancellationToken ct)
    {
        var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IAudioSource? source = null;
        IAudioDecoder? decoder = null;
        float[]? decodeBuffer = null;

        try
        {
            source = await AudioSourceFactory.CreateAsync(
                url, Http.SharedHttpClient.Instance, urlRefresher, trackId,
                bitrateHint, options.StreamingConfig, lifetimeCts.Token).ConfigureAwait(false);

            if (!await source.InitializeAsync(lifetimeCts.Token).ConfigureAwait(false))
            {
                lifetimeCts.Token.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                throw new AudioSourceException("Failed to initialize audio source");
            }

            decoder = CreateDecoder(source);

            int rawSize = decoder.SampleRate * decoder.Channels * BufferSizeSeconds;
            int bufferSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(rawSize, 16));
            var pcmBuffer = new LockFreeRingBuffer<float>(bufferSize);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);
            var streamInfo = BuildStreamInfo(source, trackId, bitrateHint);

            var pipeline = new AudioPipeline(
                source, decoder, sharedBackend, pcmBuffer, decodeBuffer, streamInfo, lifetimeCts);

            try
            {
                sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);
            }
            catch (AudioDeviceException ex)
            {
                pipeline._deviceLost = true;
                Log.Warn($"[AudioPipeline] Created in degraded mode: {ex.Message}");
            }

            sharedBackend.SetDeviceLostCallback(pipeline.NotifyDeviceLost);
            sharedBackend.SetStarvationCallback(pipeline.NotifyStarvation);
            sharedBackend.SetDeviceAvailableCallback(pipeline.NotifyDeviceAvailable);

            return pipeline;
        }
        catch (OperationCanceledException) { CleanupOnError(source, decoder, decodeBuffer, lifetimeCts); throw; }
        catch (AudioSourceException) { CleanupOnError(source, decoder, decodeBuffer, lifetimeCts); throw; }
        catch (Exception ex)
        {
            CleanupOnError(source, decoder, decodeBuffer, lifetimeCts);
            if (CancellationHelper.IsCancellationOrTokenCancelled(ex, ct))
                throw new OperationCanceledException("Pipeline creation cancelled", ex, ct);
            throw new AudioSourceException("Failed to initialize audio source", ex);
        }
    }

    private static void CleanupOnError(IAudioSource? s, IAudioDecoder? d, float[]? buf, CancellationTokenSource cts)
    {
        try { d?.Dispose(); } catch { }
        try { s?.Dispose(); } catch { }
        if (buf != null) ArrayPool<float>.Shared.Return(buf);
        try { cts.Dispose(); } catch { }
    }

    private static IAudioDecoder CreateDecoder(IAudioSource source)
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

    private static AudioStreamInfo BuildStreamInfo(IAudioSource source, string? trackId, int bitrateHint)
    {
        var cacheEntry = !string.IsNullOrEmpty(trackId)
            ? AudioSourceFactory.FindAnyCachedTrack(trackId)?.Entry
            : null;

        string container = source is Sources.LocalFileSource && cacheEntry != null
            ? cacheEntry.Format.ToString()
            : source.Codec switch
            {
                AudioCodec.Opus => "WebM",
                AudioCodec.Aac => "Mp4",
                _ => "Unknown"
            };

        int bitrate = bitrateHint > 0 ? bitrateHint
            : cacheEntry is { Bitrate: > 0 } ? cacheEntry.Bitrate
            : source is Sources.CachingStreamSource { Bitrate: > 0 } cs ? cs.Bitrate
            : source.Codec == AudioCodec.Opus ? 128 : 96;

        return new AudioStreamInfo
        {
            TrackId = trackId ?? "",
            Container = container,
            Codec = source.Codec.ToString(),
            Bitrate = bitrate,
            SampleRate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate,
            Channels = source.Channels > 0 ? source.Channels : DefaultChannels,
            DurationMs = source.DurationMs,
            IsFromCache = cacheEntry?.IsComplete ?? false
        };
    }

    #endregion

    #region Device Loss

    /// <summary>
    /// Вызывается когда аудиоустройство пропало.
    /// Pipeline остаётся живым, decoder останавливается.
    /// Task обработчика сохраняется в <see cref="_deviceEventTask"/>
    /// для детерминированного ожидания в <see cref="DisposeAsync"/>:
    /// исключает гонку между обработчиком (вызывает <c>StartDecoding</c>)
    /// и освобождением ресурсов pipeline.
    /// </summary>
    internal void NotifyDeviceLost()
    {
        if (_disposed || _deviceLost) return;
        _deviceLost = true;

        Log.Error("[AudioPipeline] Audio device lost — soft pause (pipeline alive)");
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        var handler = _onDeviceLostExternal;
        if (handler != null)
            Volatile.Write(ref _deviceEventTask, Task.Run(handler));
    }


    /// <summary>Регистрирует обработчик потери устройства.</summary>
    internal void SetDeviceLostHandler(Action handler) => _onDeviceLostExternal = handler;

    /// <summary>
    /// Восстанавливает pipeline после потери аудиоустройства.
    /// </summary>
    internal async Task RecoverFromDeviceLossAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        if (_disposed || !_deviceLost) return;

        await StopDecodingAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        _backend.Flush();
        _pcmBuffer.Clear();

        _backend.Reinitialize(SampleRate, Channels, AudioCallback);
        _backend.SetDeviceLostCallback(NotifyDeviceLost);
        _backend.SetStarvationCallback(NotifyStarvation);
        _backend.SetDeviceAvailableCallback(NotifyDeviceAvailable);

        ct.ThrowIfCancellationRequested();
        _deviceLost = false;

        StartDecoding(urlRefresher, options, onTrackEnded, onError);
        Log.Info("[AudioPipeline] Recovered from device loss");
    }

    /// <summary>
    /// Вызывается когда аудиоустройство снова доступно.
    /// Task обработчика сохраняется в <see cref="_deviceEventTask"/>
    /// для детерминированного ожидания в <see cref="DisposeAsync"/>.
    /// </summary>
    internal void NotifyDeviceAvailable()
    {
        if (_disposed || !_deviceLost) return;
        var handler = _onDeviceAvailableExternal;
        if (handler != null)
            Volatile.Write(ref _deviceEventTask, Task.Run(handler));
    }

    /// <summary>Регистрирует обработчик появления устройства.</summary>
    internal void SetDeviceAvailableHandler(Action handler) => _onDeviceAvailableExternal = handler;

    #endregion

    #region Decoder Loop

    /// <summary>
    /// Запускает decoder loop на ThreadPool.
    /// </summary>
    public void StartDecoding(
    Func<CancellationToken, Task<string?>>? urlRefresher,
    AudioPlayerOptions options,
    Action? onTrackEnded,
    Action<Exception>? onError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decoderTask is { IsCompleted: false }) return;

        _decoderCts?.Dispose();
        _decoderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var token = _decoderCts.Token;

        _decoderTask = Task.Run(
            () => DecoderLoopAsync(urlRefresher, options, onTrackEnded, onError, token));

#if DEBUG
        int restartCount = Interlocked.Increment(ref _decoderRestartCount);
        var trackIdShort = _streamInfo.TrackId?.Length > ShortTrackIdLength
            ? _streamInfo.TrackId[..ShortTrackIdLength]
            : _streamInfo.TrackId ?? "?";

        if (restartCount > 1)
            Log.Debug($"[AudioPipeline] Decoder restart #{restartCount}: {trackIdShort}");
        else
            Log.Debug($"[AudioPipeline] Decoder started: {trackIdShort}");
#endif
    }

    /// <summary>Останавливает decoder loop.</summary>
    public async Task StopDecodingAsync(TimeSpan timeout)
    {
        var cts = _decoderCts;
        var task = _decoderTask;
        if (cts == null || task == null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { Log.Warn("[AudioPipeline] Decoder stop timeout"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"[AudioPipeline] Decoder stop error: {ex.Message}"); }

        _decoderTask = null;
        _decoderCts = null;
        try { cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Основной цикл декодирования.
    /// </summary>
    /// <remarks>
    /// <para><b>ИСПРАВЛЕНИЕ (критическое):</b> Разделена семантика отмены.</para>
    /// <list type="bullet">
    ///   <item><c>ct.IsCancellationRequested == true</c> → преднамеренная остановка (Stop/Seek/Dispose) → break</item>
    ///   <item><c>ct.IsCancellationRequested == false</c> + <see cref="OperationCanceledException"/> →
    ///     транзиентная ошибка: HTTP timeout, epoch reset, socket disconnect → retry</item>
    /// </list>
    /// <para>Предыдущая версия ловила ВСЕ <see cref="OperationCanceledException"/>
    /// одним <c>catch</c> без проверки токена, что приводило к мгновенной
    /// смерти decoder loop при каждом HTTP timeout. Retry-ветка была недостижима.</para>
    /// </remarks>
    private async Task DecoderLoopAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        int retryCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Backpressure: если буфер полон, ждём вместо busy-spin
                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    await Task.Delay(BufferFullDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct).ConfigureAwait(false);
                    retryCount = 0;
                }
                //Только НАМЕРЕННАЯ отмена (decoder CTS) завершает loop
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                // Транзиентные OCE (HTTP timeout, epoch reset) → retry
                // TaskCanceledException наследует OperationCanceledException.
                // HttpClient бросает TCE при timeout/disconnect с ct, который НЕ отменён.
                // CachingStreamSource бросает OCE при ResetDownloadEpoch (seek/preload).
                // Обе ситуации — recoverable, decoder должен повторить чтение.
                catch (OperationCanceledException) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read transient cancel (retry {retryCount}/{options.MaxRetryAttempts})");
                    try
                    {
                        await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ct отменился во время delay — значит это реальная остановка
                        break;
                    }
                    continue;
                }
                // Превышено число retry для транзиентных OCE — фатальная ошибка
                catch (OperationCanceledException ex)
                {
                    Log.Error($"[AudioPipeline] Read failed after {retryCount} transient retries: {ex.Message}");
                    onError?.Invoke(ex);
                    break;
                }
                catch (UrlExpiredException) when (urlRefresher != null)
                {
                    var newUrl = await urlRefresher(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(newUrl)) continue;
                    throw;
                }
                catch (ChunkDownloadFatalException) { throw; }
                catch (FileNotFoundException ex)
                {
                    throw new CacheInvalidatedException("Cache file was deleted during playback.", ex);
                }
                catch (DirectoryNotFoundException ex)
                {
                    throw new CacheInvalidatedException("Cache directory was deleted during playback.", ex);
                }
                catch (IOException ex) when (ex.HResult is HResultFileNotFound or HResultPathNotFound)
                {
                    throw new CacheInvalidatedException("Cache file became unavailable during playback.", ex);
                }
                catch (Exception ex) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read retry {retryCount}: {ex.Message}");
                    try
                    {
                        await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                if (frame == null)
                {
                    if (ct.IsCancellationRequested) break;
                    await DrainBufferAsync(ct).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested) onTrackEnded?.Invoke();
                    break;
                }

                try
                {
                    int skipCount = Volatile.Read(ref _skipFramesCounter);
                    bool needReset = skipCount > 0;

                    int samplesDecoded = needReset
                        ? _decoder.DecodeWithReset(frame.Value.Data.Span, _decodeBuffer)
                        : _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);

                    if (needReset) { Interlocked.Decrement(ref _skipFramesCounter); continue; }

                    long seekTarget = Volatile.Read(ref _seekTargetMs);
                    if (seekTarget >= 0)
                    {
                        if (frame.Value.TimestampMs < seekTarget) continue;
                        Volatile.Write(ref _seekTargetMs, -1L);
                    }

                    if (samplesDecoded > 0)
                    {
                        int totalSamples = samplesDecoded * _decoder.Channels;
                        _pcmBuffer.Write(_decodeBuffer.AsSpan(0, totalSamples));
                        Interlocked.Add(ref _decodedSamples, totalSamples);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"[AudioPipeline] Decode error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (CacheInvalidatedException ex)
        {
            Log.Warn($"[AudioPipeline] Cache invalidated: {ex.Message}");
            onError?.Invoke(ex);
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
            onError?.Invoke(ex);
        }
    }

    /// <summary>
    /// Ожидает опустошения ring buffer перед завершением трека.
    /// </summary>
    private async Task DrainBufferAsync(CancellationToken ct)
    {
        while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
        {
            int remainingSamples = _pcmBuffer.Count;
            int samplesPerSecond = SampleRate * Channels;
            int estimatedMs = samplesPerSecond > 0
                ? remainingSamples * 1000 / samplesPerSecond / 2
                : DrainMinDelayMs;

            await Task.Delay(Math.Clamp(estimatedMs, DrainMinDelayMs, DrainMaxDelayMs), ct).ConfigureAwait(false);
        }
    }

    #endregion

    #region Playback Control

    /// <summary>Активирует fill loop backend.</summary>
    public void ActivateFillLoop()
    {
        if (_disposed) return;
        _backend.ActivateFillLoop();
    }

    /// <summary>Блокирующее ожидание прогрева backend.</summary>
    public bool WaitForBackendWarmup(int timeoutMs = 100)
    {
        if (_disposed) return false;
        return _backend.WaitForWarmup(timeoutMs);
    }

    /// <summary>Запускает воспроизведение и открывает сетевой буфер.</summary>
    public void Start()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(true);
        _backend.Start();
    }

    /// <summary>Приостанавливает воспроизведение и замораживает сетевой буфер.</summary>
    public void Stop()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(false);
        _backend.Stop();
    }

    /// <summary>Очищает все буферы.</summary>
    public void Flush()
    {
        if (_disposed) return;
        _backend.Flush();
        _pcmBuffer.Clear();
    }

    /// <summary>Уведомляет о starvation backend.</summary>
    internal void NotifyStarvation()
    {
        if (_disposed) return;
        var decoderAlive = _decoderTask is { IsCompleted: false };
        Log.Error($"[AudioPipeline] Starvation: decoder={(decoderAlive ? "alive" : "dead")}, ring={_pcmBuffer.Count}");
    }

    /// <summary>
    /// Подготавливает pipeline к seek.
    /// </summary>
    public void PrepareForSeek(long targetMs = -1)
    {
        int skipFrames = _source.Codec switch
        {
            AudioCodec.Opus => SkipFramesAfterSeekOpus,
            AudioCodec.Aac => SkipFramesAfterSeekAac,
            _ => 0
        };

        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _seekTargetMs, targetMs);

        _analyzer.PrepareForSeek();
        _truePeakLimiter?.Reset();

        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);
    }

    /// <summary>Выполняет pre-scan нормализации.</summary>
    public async Task PreScanNormalizationAsync(CancellationToken ct)
    {
        if (!_analyzer.IsEnabled || !_source.CanSeek) return;
        if (_analyzer.IsGainLocked) return;

        try
        {
            float rawGain = await _analyzer.PreScanAsync(_source, _decoder, _decodeBuffer, ct).ConfigureAwait(false);
            _analyzer.LockGain(rawGain);
            await _source.SeekAsync(0, ct).ConfigureAwait(false);

            int skipFrames = _source.Codec switch
            {
                AudioCodec.Opus => SkipFramesAfterSeekOpus,
                AudioCodec.Aac => SkipFramesAfterSeekAac,
                _ => 0
            };

            Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
            Volatile.Write(ref _seekTargetMs, -1L);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"[AudioPipeline] Pre-scan failed: {ex.Message}"); }
    }

    /// <summary>Устанавливает абсолютную позицию decoded samples.</summary>
    public void SetDecodedSamplesPosition(long samples) =>
        Interlocked.Exchange(ref _decodedSamples, samples);

    /// <summary>Возвращает locked gain нормализации.</summary>
    public float GetLockedNormalizationGain() => _analyzer.GetLockedGain();

    /// <summary>Устанавливает начальный gain от предыдущего трека.</summary>
    public void SetInitialNormalizationGain(float gain)
    {
        if (_disposed) return;
        _analyzer.SetInitialGain(gain);
    }

    /// <summary>Снапирует crossfader на текущий normGain до первого AudioCallback.</summary>
    public void SnapCrossfaderToGain()
    {
        if (_disposed) return;
        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);
    }

    #endregion

    #region Audio Callback

    /// <summary>
    /// Callback из fill thread backend'а.
    /// Читает PCM, анализирует LUFS, применяет normalization gain через crossfader и limiter.
    /// Volume gain НЕ применяется здесь — он в <see cref="GainWaveProvider"/>.
    /// </summary>
    private int AudioCallback(Span<float> buffer)
    {
        if (_disposed) { buffer.Clear(); return 0; }

        int read = _pcmBuffer.Read(buffer);
        if (read < buffer.Length) buffer[read..].Clear();

        if (read > 0)
        {
            var samples = buffer[..read];

            float normGain = _analyzer.IsEnabled
                ? _analyzer.ProcessSamples(samples)
                : 1.0f;

            _gainCrossfader.SetTarget(normGain, _decoder.SampleRate, _decoder.Channels);
            _truePeakLimiter!.Process(samples, ref _gainCrossfader);
        }

        return read / _decoder.Channels;
    }

    #endregion

    #region Buffer Info

    /// <summary>Ожидает минимального заполнения буфера.</summary>
    public async Task WaitForBufferAsync(int minSamples, int maxWaitMs, CancellationToken ct)
    {
        int waited = 0;
        while (_pcmBuffer.Count < minSamples && waited < maxWaitMs && !ct.IsCancellationRequested)
        {
            await Task.Delay(WaitBufferDelayMs, ct).ConfigureAwait(false);
            waited += WaitBufferDelayMs;
        }
    }

    #endregion

    #region Dispose

    /// <summary>Освобождает все ресурсы pipeline.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        // Ожидаем device-event handler: он может держать ссылку на pipeline
        var deviceTask = Volatile.Read(ref _deviceEventTask);
        if (deviceTask != null && !deviceTask.IsCompleted)
        {
            try
            {
                await deviceTask
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        if (_decoderTask != null)
        {
            try
            {
                await _decoderTask
                    .WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        _decoder.Dispose();
        await _source.DisposeAsync().ConfigureAwait(false);
        ArrayPool<float>.Shared.Return(_decodeBuffer);

        try { _decoderCts?.Dispose(); } catch (ObjectDisposedException) { }
        try { _lifetimeCts.Dispose(); } catch (ObjectDisposedException) { }

#if DEBUG
        Log.Debug($"[AudioPipeline] Disposed (decoder restarts: {_decoderRestartCount})");
#else
    Log.Info("[AudioPipeline] Disposed");
#endif
    }

    #endregion
}