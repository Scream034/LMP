using System.Buffers;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Полный конвейер воспроизведения: Source → Decoder → PCM Buffer → Backend.
/// 
/// <para><b>Lifecycle:</b></para>
/// <list type="number">
///   <item><see cref="CreateAsync(string, string?, int, Func{CancellationToken, Task{string?}}?, AudioPlayerOptions, IPlaybackBackend, CancellationToken)"/>
///     — создаёт source, decoder, переиспользует shared backend</item>
///   <item><see cref="StartDecoding"/> — запускает decoder loop (выделенный поток)</item>
///   <item><see cref="Start"/> — запускает backend (NAudio playback)</item>
///   <item><see cref="StopDecodingAsync"/> — останавливает decoder loop (для seek)</item>
///   <item><see cref="Flush"/> — очищает PCM buffer и backend buffer</item>
///   <item><see cref="DisposeAsync"/> — освобождает source, decoder, buffers.
///     Shared backend НЕ уничтожается.</item>
/// </list>
/// 
/// <para><b>Shared Backend:</b></para>
/// <para>Pipeline НЕ владеет backend'ом при <c>ownsBackend=false</c>.
/// Backend переиспользуется между треками через <see cref="IPlaybackBackend.Reinitialize"/>
/// в фабричном методе. При dispose pipeline только останавливает и flush'ит backend.</para>
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    #region Fields

    private readonly IAudioSource _source;
    private readonly IAudioDecoder _decoder;
    private readonly IPlaybackBackend _backend;
    private readonly LockFreeRingBuffer<float> _pcmBuffer;
    private readonly float[] _decodeBuffer;
    private readonly AudioStreamInfo _streamInfo;
    private readonly CancellationTokenSource _lifetimeCts;

    /// <summary>
    /// true = backend создан этим pipeline и будет уничтожен при dispose.
    /// false = backend shared (принадлежит AudioPlayer), только stop+flush при dispose.
    /// </summary>
    private readonly bool _ownsBackend;

    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private volatile bool _disposed;

    private int _skipFramesCounter;
    private long _decodedSamples;

    /// <summary>
    /// Target timestamp для post-seek skipping.
    /// Фреймы с timestamp < этого значения пропускаются (не пишутся в PCM buffer).
    /// -1 = не активен.
    /// </summary>
    private long _seekTargetMs = -1;

    #endregion

    #region Properties

    public AudioStreamInfo StreamInfo => _streamInfo;
    public IAudioSource Source => _source;
    public IAudioDecoder Decoder => _decoder;
    public IPlaybackBackend Backend => _backend;
    public bool IsDisposed => _disposed;

    public int SampleRate => _decoder.SampleRate;
    public int Channels => _decoder.Channels;

    public long PlayedSamples => Interlocked.Read(ref _decodedSamples) - _pcmBuffer.Count;
    public int BackendBufferedSamples => _backend.BufferedSamples;
    public int BufferedSamples => _pcmBuffer.Count;

    #endregion

    #region Constructor

    private AudioPipeline(
        IAudioSource source,
        IAudioDecoder decoder,
        IPlaybackBackend backend,
        LockFreeRingBuffer<float> pcmBuffer,
        float[] decodeBuffer,
        AudioStreamInfo streamInfo,
        CancellationTokenSource lifetimeCts,
        bool ownsBackend)
    {
        _source = source;
        _decoder = decoder;
        _backend = backend;
        _pcmBuffer = pcmBuffer;
        _decodeBuffer = decodeBuffer;
        _streamInfo = streamInfo;
        _lifetimeCts = lifetimeCts;
        _ownsBackend = ownsBackend;
    }

    #endregion

    #region Factory

    /// <summary>
    /// Создаёт pipeline с SHARED backend (рекомендуемый путь).
    /// Backend переиспользуется через <see cref="IPlaybackBackend.Reinitialize"/>
    /// и НЕ уничтожается при dispose pipeline.
    /// </summary>
    /// <param name="sharedBackend">Shared backend из AudioPlayer. 
    /// Reinitialize вызывается автоматически — fast path (~0ms) если формат совпадает,
    /// slow path (~50-200ms) если sampleRate/channels изменились.</param>
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
                url,
                Http.SharedHttpClient.Instance,
                urlRefresher,
                trackId,
                bitrateHint,
                options.StreamingConfig,
                lifetimeCts.Token);

            if (!await source.InitializeAsync(lifetimeCts.Token))
                throw new Exceptions.AudioSourceException("Failed to initialize audio source");

            decoder = CreateDecoder(source);

            // Размер буфера — степень двойки для LockFreeRingBuffer
            int rawSize = decoder.SampleRate * decoder.Channels * BufferSizeSeconds;
            int bufferSize = RoundUpToPowerOf2(rawSize);
            var pcmBuffer = new LockFreeRingBuffer<float>(bufferSize);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);

            var streamInfo = BuildStreamInfo(source, trackId, bitrateHint);

            var pipeline = new AudioPipeline(
                source, decoder, sharedBackend, pcmBuffer, decodeBuffer,
                streamInfo, lifetimeCts, ownsBackend: false);

            // Reinitialize backend для нового трека
            // Fast path если формат совпадает (обычно Opus 48kHz/2ch → Opus 48kHz/2ch)
            sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);

            Log.Info($"[AudioPipeline] Created (shared backend): {streamInfo.FormatDisplay}");

            return pipeline;
        }
        catch (Youtube.Exceptions.StreamUnavailableException)
        {
            CleanupOnErrorPartial(source, decoder, decodeBuffer, lifetimeCts);
            throw;
        }
        catch (Exception ex)
        {
            CleanupOnErrorPartial(source, decoder, decodeBuffer, lifetimeCts);

            if (ex is Exceptions.AudioSourceException)
                throw;

            throw new Exceptions.AudioSourceException("Failed to initialize audio source", ex);
        }
    }

    /// <summary>
    /// Создаёт pipeline с СОБСТВЕННЫМ backend (legacy, для обратной совместимости).
    /// Backend уничтожается при dispose pipeline.
    /// </summary>
    public static async Task<AudioPipeline> CreateAsync(
        string url,
        string? trackId,
        int bitrateHint,
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        CancellationToken ct)
    {
        var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IAudioSource? source = null;
        IAudioDecoder? decoder = null;
        IPlaybackBackend? backend = null;
        float[]? decodeBuffer = null;

        try
        {
            source = await AudioSourceFactory.CreateAsync(
                url,
                Http.SharedHttpClient.Instance,
                urlRefresher,
                trackId,
                bitrateHint,
                options.StreamingConfig,
                lifetimeCts.Token);

            if (!await source.InitializeAsync(lifetimeCts.Token))
                throw new Exceptions.AudioSourceException("Failed to initialize audio source");

            decoder = CreateDecoder(source);
            backend = CreateBackend(options);

            int rawSize = decoder.SampleRate * decoder.Channels * BufferSizeSeconds;
            int bufferSize = RoundUpToPowerOf2(rawSize);
            var pcmBuffer = new LockFreeRingBuffer<float>(bufferSize);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);

            var streamInfo = BuildStreamInfo(source, trackId, bitrateHint);

            var pipeline = new AudioPipeline(
                source, decoder, backend, pcmBuffer, decodeBuffer,
                streamInfo, lifetimeCts, ownsBackend: true);

            backend.Initialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);

            Log.Info($"[AudioPipeline] Created (owned backend): {streamInfo.FormatDisplay}");

            return pipeline;
        }
        catch (Youtube.Exceptions.StreamUnavailableException)
        {
            CleanupOnError(source, decoder, backend, decodeBuffer, lifetimeCts);
            throw;
        }
        catch (Exception ex)
        {
            CleanupOnError(source, decoder, backend, decodeBuffer, lifetimeCts);

            if (ex is Exceptions.AudioSourceException)
                throw;

            throw new Exceptions.AudioSourceException("Failed to initialize audio source", ex);
        }
    }

    /// <summary>
    /// Cleanup при ошибке для shared backend — НЕ dispose'ит backend.
    /// </summary>
    private static void CleanupOnErrorPartial(
        IAudioSource? source,
        IAudioDecoder? decoder,
        float[]? decodeBuffer,
        CancellationTokenSource lifetimeCts)
    {
        try
        {
            decoder?.Dispose();
            source?.Dispose();
            if (decodeBuffer != null)
                ArrayPool<float>.Shared.Return(decodeBuffer);
            lifetimeCts.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleanup при ошибке для owned backend — dispose'ит backend.
    /// </summary>
    private static void CleanupOnError(
        IAudioSource? source,
        IAudioDecoder? decoder,
        IPlaybackBackend? backend,
        float[]? decodeBuffer,
        CancellationTokenSource lifetimeCts)
    {
        try
        {
            backend?.Dispose();
            decoder?.Dispose();
            source?.Dispose();
            if (decodeBuffer != null)
                ArrayPool<float>.Shared.Return(decodeBuffer);
            lifetimeCts.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Cleanup error: {ex.Message}");
        }
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
        if (source.DecoderConfig != null)
            dec.Initialize(source.DecoderConfig);
        return dec;
    }

    private static IPlaybackBackend CreateBackend(AudioPlayerOptions options)
    {
        if (options.UseNullBackend)
            return new NullAudioBackend();

        try
        {
            return new NAudioBackend();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] NAudio init failed: {ex.Message}, using NullBackend");
            return new NullAudioBackend();
        }
    }

    /// <summary>
    /// Строит <see cref="AudioStreamInfo"/> с РЕАЛЬНЫМ битрейтом (не нормализованным).
    /// 
    /// <para><b>Приоритет контейнера:</b></para>
    /// <list type="number">
    ///   <item>Определяется по кодеку SOURCE (не cacheEntry!) — гарантирует корректность 
    ///     при переключении качества (WebM→Mp4 и обратно)</item>
    ///   <item>CacheEntry.Format используется только если source — <see cref="Sources.LocalFileSource"/>
    ///     (т.е. играем из полного кэша, контейнер совпадает с записью)</item>
    /// </list>
    /// 
    /// <para><b>Приоритет битрейта:</b></para>
    /// <list type="number">
    ///   <item><paramref name="bitrateHint"/> — явно переданный при Play (РЕАЛЬНЫЙ, например 134)</item>
    ///   <item><see cref="CacheEntry.Bitrate"/> — из метаданных кэша (РЕАЛЬНЫЙ)</item>
    ///   <item><see cref="Sources.CachingStreamSource.Bitrate"/> — из source (РЕАЛЬНЫЙ)</item>
    ///   <item>Fallback 128/96 — только для <see cref="Sources.LocalFileSource"/> без кэш-метаданных</item>
    /// </list>
    /// 
    /// <para><b>ВАЖНО:</b> Нормализация (134→128) применяется ТОЛЬКО в
    /// <see cref="AudioSourceFactory.BuildCacheKey"/> для ключа кэша.
    /// StreamInfo всегда содержит РЕАЛЬНЫЙ битрейт для корректного отображения в UI
    /// и предотвращения бесконечного цикла переключения качества.</para>
    /// </summary>
    private static AudioStreamInfo BuildStreamInfo(IAudioSource source, string? trackId, int bitrateHint)
    {
        // Кэш-запись используется ТОЛЬКО для bitrate fallback и IsFromCache
        // Контейнер определяется по РЕАЛЬНОМУ source, не по cacheEntry.
        // Причина: При переключении WebM→Mp4 cacheEntry может быть от старого WebM кэша,
        // а source уже Mp4. Стало: "Mp4/Aac/49kbps".
        var cacheEntry = !string.IsNullOrEmpty(trackId)
            ? AudioSourceFactory.FindAnyCachedTrack(trackId)?.Entry
            : null;

        // КОНТЕЙНЕР: определяем по source, не по cacheEntry
        // Для LocalFileSource (полный кэш) — используем cacheEntry.Format (source не знает формат).
        // Для CachingStreamSource (стриминг) — используем кодек source (реальный формат потока).
        string container;
        if (source is Sources.LocalFileSource && cacheEntry != null)
        {
            // LocalFileSource играет из полного кэша — контейнер совпадает с записью
            container = cacheEntry.Format.ToString();
        }
        else
        {
            // CachingStreamSource или другой — определяем по кодеку (реальный формат)
            container = source.Codec switch
            {
                AudioCodec.Opus => "WebM",
                AudioCodec.Aac => "Mp4",
                _ => "Unknown"
            };
        }

        // БИТРЕЙТ: приоритетная цепочка
        int bitrate;
        if (bitrateHint > 0)
        {
            bitrate = bitrateHint;
        }
        else if (cacheEntry is { Bitrate: > 0 })
        {
            bitrate = cacheEntry.Bitrate;
        }
        else if (source is Sources.CachingStreamSource { Bitrate: > 0 } cachingSource)
        {
            bitrate = cachingSource.Bitrate;
        }
        else
        {
            bitrate = source.Codec == AudioCodec.Opus ? 128 : 96;
        }

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

    private static int RoundUpToPowerOf2(int value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    #endregion

    #region Decoder Loop

    /// <summary>
    /// Запускает decoder loop на ВЫДЕЛЕННОМ потоке с повышенным приоритетом.
    /// 
    /// <para><b>Почему не Task.Run:</b></para>
    /// <para>ThreadPool потоки деприоритезируются ОС когда приложение свёрнуто
    /// (EcoQoS на Windows 11, THREAD_PRIORITY_IDLE для фоновых процессов).
    /// Выделенный поток с <see cref="ThreadPriority.AboveNormal"/> сохраняет
    /// приоритет и гарантирует бесперебойное декодирование.</para>
    /// </summary>
    public void StartDecoding(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_decoderTask != null && !_decoderTask.IsCompleted)
            throw new InvalidOperationException("Decoder already running");

        _decoderCts?.Dispose();
        _decoderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

        var token = _decoderCts.Token;

        // TaskCompletionSource для отслеживания завершения потока
        var tcs = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var trackIdShort = _streamInfo.TrackId?.Length > 8
            ? _streamInfo.TrackId[..8]
            : _streamInfo.TrackId ?? "?";

        var decoderThread = new Thread(() =>
        {
            try
            {
                DecoderLoopAsync(urlRefresher, options, onTrackEnded, onError, token)
                    .GetAwaiter().GetResult();
                tcs.TrySetResult(null);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            Name = $"Decoder-{trackIdShort}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };

        decoderThread.Start();
        _decoderTask = tcs.Task;

        Log.Debug("[AudioPipeline] Decoder started (dedicated thread)");
    }

    public async Task StopDecodingAsync(TimeSpan timeout)
    {
        var cts = _decoderCts;
        var task = _decoderTask;

        if (cts == null || task == null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }

        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Log.Warn("[AudioPipeline] Decoder stop timeout");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Decoder stop error: {ex.Message}");
        }

        _decoderTask = null;
        _decoderCts = null;

        try { cts.Dispose(); }
        catch (ObjectDisposedException) { }

        Log.Debug("[AudioPipeline] Decoder stopped");
    }

    private async Task DecoderLoopAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        // Устанавливаем SustainedLowLatency на время декодирования.
        // Это предотвращает Gen2 blocking GC, который замораживает
        // fill loop и вызывает buffer underrun.
        var previousLatencyMode = System.Runtime.GCSettings.LatencyMode;

        try
        {
            System.Runtime.GCSettings.LatencyMode =
                System.Runtime.GCLatencyMode.SustainedLowLatency;
        }
        catch (Exception ex)
        {
            Log.Debug($"[AudioPipeline] Could not set GC latency mode: {ex.Message}");
        }

        int retryCount = 0;
        int backoffCount = 0;
        const int MaxYieldBeforeDelay = 4;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();

                // Проверка места в буфере
                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    if (backoffCount < MaxYieldBeforeDelay)
                    {
                        await Task.Yield();
                        backoffCount++;
                    }
                    else
                    {
                        await Task.Delay(5, ct);
                    }
                    continue;
                }

                backoffCount = 0;

                // Чтение фрейма из source
                AudioFrame? frame;
                try
                {
                    frame = await _source.ReadFrameAsync(ct);
                    retryCount = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exceptions.UrlExpiredException) when (urlRefresher != null)
                {
                    var newUrl = await urlRefresher(ct);
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        Log.Info("[AudioPipeline] URL refreshed in decoder loop");
                        continue;
                    }
                    throw;
                }
                catch (Exceptions.ChunkDownloadFatalException)
                {
                    throw;
                }
                catch (IOException ex) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read retry {retryCount}: {ex.Message}");
                    await Task.Delay(options.RetryDelay, ct);
                    continue;
                }
                catch (Exception ex) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read retry {retryCount}: {ex.Message}");
                    await Task.Delay(options.RetryDelay, ct);
                    continue;
                }

                if (frame == null)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Log.Debug("[AudioPipeline] Decoder cancelled, skipping track end");
                        break;
                    }

                    await DrainBufferAsync(ct);

                    if (ct.IsCancellationRequested)
                    {
                        Log.Debug("[AudioPipeline] Decoder cancelled during drain, skipping track end");
                        break;
                    }

                    onTrackEnded?.Invoke();
                    break;
                }

                // Декодирование
                try
                {
                    int skipCount = Volatile.Read(ref _skipFramesCounter);
                    bool needReset = skipCount > 0;

                    int samplesDecoded = needReset
                        ? _decoder.DecodeWithReset(frame.Value.Data.Span, _decodeBuffer)
                        : _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);

                    // Skip frames (codec warmup после seek)
                    if (needReset)
                    {
                        Interlocked.Decrement(ref _skipFramesCounter);
                        continue;
                    }

                    // POST-SEEK TIMESTAMP SKIP
                    // WebM cue points имеют шаг ~10-30 секунд.
                    // Seek к 3s → cue point 0ms → парсер начинает с 0ms.
                    // Пропускаем фреймы до целевого timestamp.
                    long seekTarget = Volatile.Read(ref _seekTargetMs);
                    if (seekTarget >= 0)
                    {
                        if (frame.Value.TimestampMs < seekTarget)
                        {
                            // Фрейм до цели — декодировали (для state), но не пишем
                            continue;
                        }

                        // Достигли или превысили цель — выключаем skip
                        Volatile.Write(ref _seekTargetMs, -1L);
                    }

                    if (samplesDecoded > 0)
                    {
                        int totalSamples = samplesDecoded * _decoder.Channels;
                        _pcmBuffer.Write(_decodeBuffer.AsSpan(0, totalSamples));
                        Interlocked.Add(ref _decodedSamples, totalSamples);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[AudioPipeline] Decode error (non-fatal): {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
            onError?.Invoke(ex);
        }
        finally
        {
            // Восстанавливаем предыдущий режим GC
            try { System.Runtime.GCSettings.LatencyMode = previousLatencyMode; }
            catch { }
        }
    }

    private async Task DrainBufferAsync(CancellationToken ct)
    {
        while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }
    }

    #endregion

    #region Playback Control

    public void Start()
    {
        if (_disposed) return;

        // Сообщаем backend что decoder работает
        _backend.Start();

        Log.Debug("[AudioPipeline] Backend started");
    }

    public void Stop()
    {
        if (_disposed) return;

        // Backend.Stop() содержит micro fade-out для предотвращения щелчков
        _backend.Stop();

        Log.Debug("[AudioPipeline] Backend stopped");
    }

    public void Flush()
    {
        if (_disposed) return;

        // Flush backend буфера — это сбросит _flushGeneration,
        // fill loop пропустит устаревшие данные из PCM buffer
        _backend.Flush();

        // Очищаем PCM ring buffer — критично для seek!
        // Без этого fill loop будет качать старые данные
        _pcmBuffer.Clear();
    }

    public void SetVolume(float volume)
    {
        if (_disposed) return;
        _backend.Volume = Math.Min(volume, 1f);
    }

    /// <summary>
    /// Подготавливает decoder к seek: устанавливает skip frames counter
    /// и target timestamp для точного позиционирования.
    /// </summary>
    /// <param name="targetMs">Целевая позиция в миллисекундах. 
    /// Фреймы до этого timestamp будут декодированы (для state decoder) но не записаны в PCM buffer.</param>
    /// <remarks>
    /// <para><b>AAC (MP4):</b> skip frames = 0. MP4 использует segment-based seek, каждый сегмент
    /// начинается с keyframe. Warmup обеспечивается через <c>_seekTargetMs</c>: фреймы от начала
    /// сегмента до целевой позиции декодируются (прогревая decoder state), но не пишутся в буфер.</para>
    /// 
    /// <para><b>Opus (WebM):</b> skip frames = N. Opus SILK layer имеет inter-frame зависимости.
    /// Первые N фреймов после reset декодируются через <see cref="IAudioDecoder.DecodeWithReset"/>
    /// для сброса внутреннего состояния декодера. Затем <c>_seekTargetMs</c> дополнительно
    /// пропускает фреймы до целевого timestamp.</para>
    /// </remarks>
    public void PrepareForSeek(long targetMs = -1)
    {
        int skipFrames = _source.Codec == AudioCodec.Opus
            ? SkipFramesAfterSeekOpus
            : 0;

        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _seekTargetMs, targetMs);
    }

    public void SetDecodedSamplesPosition(long samples)
    {
        Interlocked.Exchange(ref _decodedSamples, samples);
    }

    #endregion

    #region Audio Callback

    /// <summary>
    /// Callback вызываемый fill loop из NAudioBackend.
    /// Читает PCM данные из ring buffer и возвращает количество прочитанных фреймов.
    /// 
    /// <para>Если буфер пуст — заполняет тишиной. Это предотвращает щелчки
    /// при buffer underrun (например, при старте трека когда decoder ещё не заполнил буфер).</para>
    /// </summary>
    private int AudioCallback(Span<float> buffer)
    {
        if (_disposed)
        {
            buffer.Clear();
            return 0;
        }

        int read = _pcmBuffer.Read(buffer);

        if (read < buffer.Length)
            buffer[read..].Clear();

        return read / _decoder.Channels;
    }

    #endregion

    #region Buffer Info

    public async Task WaitForBufferAsync(int minSamples, int maxWaitMs, CancellationToken ct)
    {
        int waited = 0;
        while (_pcmBuffer.Count < minSamples && waited < maxWaitMs && !ct.IsCancellationRequested)
        {
            await Task.Delay(10, ct);
            waited += 10;
        }
    }

    #endregion

    #region Dispose

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _lifetimeCts.Cancel(); }
        catch (ObjectDisposedException) { }

        try { _decoderCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (_decoderTask != null)
        {
            try
            {
                await _decoderTask.WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs));
            }
            catch { }
        }

        // Backend: зависит от ownership
        if (_ownsBackend)
        {
            // Owned backend — полный dispose (waveOutClose)
            _backend.Dispose();
        }
        else
        {
            // Shared backend — только stop + flush, НЕ dispose.
            // Stop() содержит micro fade-out для предотвращения щелчков.
            // Backend продолжит жить для следующего трека.
            try
            {
                _backend.Stop();
                _backend.Flush();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Debug($"[AudioPipeline] Backend stop/flush on dispose: {ex.Message}");
            }
        }

        _decoder.Dispose();
        await _source.DisposeAsync();

        ArrayPool<float>.Shared.Return(_decodeBuffer);

        try { _decoderCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        try { _lifetimeCts.Dispose(); }
        catch (ObjectDisposedException) { }

        Log.Debug("[AudioPipeline] Disposed");
    }

    #endregion
}