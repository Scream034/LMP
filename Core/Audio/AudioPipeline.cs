using System.Buffers;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Полный конвейер воспроизведения: Source → Decoder → PCM Buffer → Backend.
/// 
/// <para><b>Lifecycle:</b></para>
/// <list type="number">
///   <item><see cref="CreateAsync"/> — создаёт source, decoder, backend, PCM buffer</item>
///   <item><see cref="StartDecoding"/> — запускает decoder loop (фоновый Task)</item>
///   <item><see cref="Start"/> — запускает backend (NAudio playback)</item>
///   <item><see cref="StopDecodingAsync"/> — останавливает decoder loop (для seek)</item>
///   <item><see cref="Flush"/> — очищает PCM buffer и backend buffer</item>
///   <item><see cref="DisposeAsync"/> — освобождает все ресурсы</item>
/// </list>
/// 
/// <para><b>Seek sequence (вызывается из AudioPlayer.HandleSeekAsync):</b></para>
/// <code>
/// StopDecodingAsync()    — decoder loop завершается
/// Stop()                 — backend paused
/// Flush()                — PCM buffer + backend buffer очищены
/// PrepareForSeek()       — skip frames counter установлен
/// Source.SeekAsync()     — epoch reset, stream repositioned
/// StartDecoding()        — новый decoder loop
/// WaitForBufferAsync()   — ждём минимум данных
/// Start()                — backend resumed
/// </code>
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    #region Fields

    private readonly IAudioSource _source;
    private readonly IAudioDecoder _decoder;
    private readonly IPlaybackBackend _backend;
    private readonly CircularBuffer<float> _pcmBuffer;
    private readonly float[] _decodeBuffer;
    private readonly AudioStreamInfo _streamInfo;

    /// <summary>
    /// Lifetime CTS — отменяется только при Dispose всего pipeline.
    /// </summary>
    private readonly CancellationTokenSource _lifetimeCts;

    /// <summary>
    /// Decoder CTS — отменяется при каждом StopDecoding, пересоздаётся при StartDecoding.
    /// </summary>
    private CancellationTokenSource? _decoderCts;

    private Task? _decoderTask;
    private volatile bool _disposed;

    private int _skipFramesCounter;
    private long _decodedSamples;

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
        CircularBuffer<float> pcmBuffer,
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
    }

    #endregion

    #region Factory

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
                lifetimeCts.Token);

            if (!await source.InitializeAsync(lifetimeCts.Token))
                throw new Exceptions.AudioSourceException("Failed to initialize audio source");

            decoder = CreateDecoder(source);
            backend = CreateBackend(options);

            int bufferSize = decoder.SampleRate * decoder.Channels * BufferSizeSeconds;
            var pcmBuffer = new CircularBuffer<float>(bufferSize);
            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);

            var streamInfo = BuildStreamInfo(source, trackId, bitrateHint);

            var pipeline = new AudioPipeline(
                source, decoder, backend, pcmBuffer, decodeBuffer, streamInfo, lifetimeCts);

            backend.Initialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);

            Log.Info($"[AudioPipeline] Created: {streamInfo.FormatDisplay}");

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

    private static AudioStreamInfo BuildStreamInfo(IAudioSource source, string? trackId, int bitrateHint)
    {
        var cacheEntry = !string.IsNullOrEmpty(trackId)
            ? AudioSourceFactory.FindAnyCachedTrack(trackId)?.Entry
            : null;

        string container = cacheEntry?.Format.ToString() ?? source.Codec switch
        {
            AudioCodec.Opus => "WebM",
            AudioCodec.Aac => "Mp4",
            _ => "Unknown"
        };

        int bitrate = bitrateHint > 0 ? bitrateHint
            : cacheEntry?.Bitrate ?? (source.Codec == AudioCodec.Opus ? 128 : 96);

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

    #region Decoder Loop

    /// <summary>
    /// Запускает decoder loop в фоновом потоке.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Pipeline disposed.</exception>
    /// <exception cref="InvalidOperationException">Decoder уже запущен.</exception>
    public void StartDecoding(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_decoderTask != null && !_decoderTask.IsCompleted)
            throw new InvalidOperationException("Decoder already running");

        // Dispose старый CTS (если есть) и создаём новый
        _decoderCts?.Dispose();
        _decoderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

        var token = _decoderCts.Token;

        _decoderTask = Task.Run(
            () => DecoderLoopAsync(urlRefresher, options, onTrackEnded, onError, token),
            token);

        Log.Debug("[AudioPipeline] Decoder started");
    }

    /// <summary>
    /// Останавливает decoder loop и ожидает его завершения.
    /// </summary>
    /// <remarks>
    /// <para><b>Безопасность CTS dispose:</b></para>
    /// Старый <see cref="_decoderCts"/> dispose'ится только ПОСЛЕ подтверждённого
    /// завершения <see cref="_decoderTask"/>. Это предотвращает use-after-dispose
    /// если таска всё ещё использует токен.
    /// </remarks>
    public async Task StopDecodingAsync(TimeSpan timeout)
    {
        var cts = _decoderCts;
        var task = _decoderTask;

        if (cts == null || task == null)
            return;

        // Отменяем decoder CTS
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }

        // Ждём завершения таски
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Log.Warn("[AudioPipeline] Decoder stop timeout — task still running");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Decoder stop error: {ex.Message}");
        }

        // Обнуляем ПЕРЕД dispose — чтобы StartDecoding не увидел disposed CTS
        _decoderTask = null;
        _decoderCts = null;

        // Dispose ПОСЛЕ обнуления и ПОСЛЕ завершения таски
        try { cts.Dispose(); }
        catch (ObjectDisposedException) { }

        Log.Debug("[AudioPipeline] Decoder stopped");
    }

    /// <summary>
    /// Основной цикл декодирования: читает фреймы из source, декодирует, пишет в PCM buffer.
    /// </summary>
    private async Task DecoderLoopAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        int retryCount = 0;
        bool useResetDecode = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();

                int skipCount = Interlocked.CompareExchange(ref _skipFramesCounter, 0, 0);
                if (skipCount > 0)
                    useResetDecode = true;

                // Ждём место в буфере
                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    await Task.Delay(5, ct);
                    continue;
                }

                // Читаем фрейм из source
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
                catch (IOException ex) when (retryCount++ < options.MaxRetryAttempts)
                {
                    // IOException от ReadAtAsync — сеть недоступна, retry
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

                // Конец трека
                if (frame == null)
                {
                    await DrainBufferAsync(ct);
                    onTrackEnded?.Invoke();
                    break;
                }

                // Декодируем фрейм
                try
                {
                    int samplesDecoded = useResetDecode
                        ? _decoder.DecodeWithReset(frame.Value.Data.Span, _decodeBuffer)
                        : _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);

                    useResetDecode = false;

                    // Пропуск фреймов после seek (pre-skip / encoder delay)
                    skipCount = Interlocked.CompareExchange(ref _skipFramesCounter, 0, 0);
                    if (skipCount > 0)
                    {
                        Interlocked.Decrement(ref _skipFramesCounter);
                        continue;
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
    }

    /// <summary>
    /// Ожидает пока PCM буфер не будет воспроизведён (drain при конце трека).
    /// </summary>
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
        _backend.Start();
        Log.Debug("[AudioPipeline] Backend started");
    }

    public void Stop()
    {
        if (_disposed) return;
        _backend.Stop();
        Log.Debug("[AudioPipeline] Backend stopped");
    }

    public void Flush()
    {
        if (_disposed) return;
        _backend.Flush();
        _pcmBuffer.Clear();
    }

    public void SetVolume(float volume)
    {
        if (_disposed) return;
        _backend.Volume = Math.Min(volume, 1f);
    }

    /// <summary>
    /// Подготавливает decoder к seek: устанавливает skip frames counter.
    /// </summary>
    public void PrepareForSeek()
    {
        int skipFrames = _source.Codec == AudioCodec.Aac
            ? SkipFramesAfterSeekAac
            : SkipFramesAfterSeekOpus;

        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
    }

    /// <summary>
    /// Устанавливает позицию decoded samples (для корректного Position reporting после seek).
    /// </summary>
    public void SetDecodedSamplesPosition(long samples)
    {
        Interlocked.Exchange(ref _decodedSamples, samples);
    }

    #endregion

    #region Audio Callback

    /// <summary>
    /// Callback вызывается из NAudioBackend fill loop для получения PCM данных.
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

    /// <summary>
    /// Ожидает накопления минимального количества PCM данных в буфере.
    /// </summary>
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

        // Отменяем всё
        try { _lifetimeCts.Cancel(); }
        catch (ObjectDisposedException) { }

        try { _decoderCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        // Ждём decoder loop
        if (_decoderTask != null)
        {
            try
            {
                await _decoderTask.WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs));
            }
            catch { /* Timeout or cancelled — OK */ }
        }

        _backend.Dispose();
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