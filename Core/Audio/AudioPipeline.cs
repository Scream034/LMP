using System.Buffers;
using System.Runtime.CompilerServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using LMP.Core.Exceptions;
using LMP.Core.Models;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Полный конвейер воспроизведения: Source → Decoder → PCM Buffer → Backend.
/// </summary>
public sealed class AudioPipeline : IAsyncDisposable
{
    #region Constants

    /// <summary>Максимальная длина TrackId для логирования.</summary>
    private const int ShortTrackIdLength = 8;

    /// <summary>Максимальное количество пропусков цикла перед принудительной задержкой при полном буфере.</summary>
    private const int MaxYieldsBeforeDelay = 4;

    /// <summary>Задержка (мс) потока декодера, если буфер полностью заполнен.</summary>
    private const int BufferFullDelayMs = 5;

    /// <summary>Задержка (мс) при ожидании опустошения буфера (drain).</summary>
    private const int DrainDelayMs = 50;

    /// <summary>Задержка (мс) при ожидании минимального заполнения буфера при старте.</summary>
    private const int WaitBufferDelayMs = 10;

    /// <summary>
    /// HResult код ERROR_FILE_NOT_FOUND (0x80070002).
    /// Используется для идентификации удалённого кэш-файла через IOException.
    /// </summary>
    private const int HResultFileNotFound = unchecked((int)0x80070002);

    /// <summary>
    /// HResult код ERROR_PATH_NOT_FOUND (0x80070003).
    /// Используется для идентификации удалённой директории кэша через IOException.
    /// </summary>
    private const int HResultPathNotFound = unchecked((int)0x80070003);

    #endregion

    #region Fields

    /// <summary>Источник сырых аудио-фреймов (сеть, кэш, файл).</summary>
    private readonly IAudioSource _source;

    /// <summary>Декодер (Opus, AAC).</summary>
    private readonly IAudioDecoder _decoder;

    /// <summary>Абстракция над системным аудио (WaveOut, WASAPI, etc).</summary>
    private readonly IPlaybackBackend _backend;

    /// <summary>Потокобезопасный циклический буфер для PCM сэмплов (float).</summary>
    private readonly LockFreeRingBuffer<float> _pcmBuffer;

    /// <summary>Временный массив для хранения данных одной операции декодирования.</summary>
    private readonly float[] _decodeBuffer;

    /// <summary>Метаинформация об аудиопотоке (битрейт, кодек и т.д.).</summary>
    private readonly AudioStreamInfo _streamInfo;

    /// <summary>Общий CTS для контроля времени жизни пайплайна.</summary>
    private readonly CancellationTokenSource _lifetimeCts;

    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private volatile bool _disposed;

    /// <summary>Количество фреймов, которые нужно пропустить после seek (encoder delay).</summary>
    private int _skipFramesCounter;

    /// <summary>Общее количество успешно декодированных и отправленных в буфер сэмплов.</summary>
    private long _decodedSamples;

    /// <summary>Target timestamp для точного позиционирования. Фреймы с timestamp ниже этого пропускаются.</summary>
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

    /// <summary>Токен отмены времени жизни pipeline. Отменяется при Dispose или потере устройства.</summary>
    public CancellationToken LifetimeToken => _lifetimeCts.Token;

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
    }

    #endregion

    #region Factory

    /// <summary>
    /// Создаёт pipeline с SHARED backend (рекомендуемый путь).
    /// Backend переиспользуется через <see cref="IPlaybackBackend.Reinitialize"/>
    /// и НЕ уничтожается при dispose pipeline.
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
                url,
                Http.SharedHttpClient.Instance,
                urlRefresher,
                trackId,
                bitrateHint,
                options.StreamingConfig,
                lifetimeCts.Token);

            if (!await source.InitializeAsync(lifetimeCts.Token))
            {
                lifetimeCts.Token.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                throw new AudioSourceException("Failed to initialize audio source");
            }

            decoder = CreateDecoder(source);

            int rawSize = decoder.SampleRate * decoder.Channels * BufferSizeSeconds;
            int bufferSize = RoundUpToPowerOf2(rawSize);
            var pcmBuffer = new LockFreeRingBuffer<float>(bufferSize);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);

            var streamInfo = BuildStreamInfo(source, trackId, bitrateHint);

            var pipeline = new AudioPipeline(
                source, decoder, sharedBackend, pcmBuffer, decodeBuffer,
                streamInfo, lifetimeCts);

            sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);

            if (sharedBackend is NAudioBackend naBackend)
                naBackend.SetDeviceLostCallback(pipeline.NotifyDeviceLost);

            Log.Info($"[AudioPipeline] Created (shared backend): {streamInfo.FormatDisplay}");

            return pipeline;
        }
        catch (OperationCanceledException)
        {
            CleanupOnErrorPartial(source, decoder, decodeBuffer, lifetimeCts);
            throw;
        }
        catch (Youtube.Exceptions.StreamUnavailableException)
        {
            CleanupOnErrorPartial(source, decoder, decodeBuffer, lifetimeCts);
            throw;
        }
        catch (AudioDeviceException)
        {
            CleanupOnErrorPartial(source, decoder, decodeBuffer, lifetimeCts);
            throw;
        }
        catch (Exception ex)
        {
            CleanupOnErrorPartial(source, decoder, decodeBuffer, lifetimeCts);

            if (IsAnyCancellation(ex, ct))
                throw new OperationCanceledException("Pipeline creation cancelled", ex, ct);

            if (ex is AudioSourceException)
                throw;

            throw new AudioSourceException("Failed to initialize audio source", ex);
        }
    }

    /// <summary>
    /// Проверяет является ли исключение или любое его вложенное исключение отменой операции,
    /// либо токен уже отменён.
    /// </summary>
    private static bool IsAnyCancellation(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return true;

        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException or TaskCanceledException)
                return true;
        }

        return false;
    }

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
            if (decodeBuffer != null) ArrayPool<float>.Shared.Return(decodeBuffer);
            lifetimeCts.Dispose();
        }
        catch (Exception ex) { Log.Warn($"[AudioPipeline] Cleanup error: {ex.Message}"); }
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

        string container;
        if (source is Sources.LocalFileSource && cacheEntry != null)
        {
            container = cacheEntry.Format.ToString();
        }
        else
        {
            container = source.Codec switch
            {
                AudioCodec.Opus => "WebM",
                AudioCodec.Aac => "Mp4",
                _ => "Unknown"
            };
        }

        int bitrate = bitrateHint > 0 ? bitrateHint :
                      cacheEntry is { Bitrate: > 0 } ? cacheEntry.Bitrate :
                      source is Sources.CachingStreamSource { Bitrate: > 0 } cachingSource ? cachingSource.Bitrate :
                      source.Codec == AudioCodec.Opus ? 128 : 96;

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

    /// <summary>Округляет число до ближайшей большей степени двойки (требование LockFreeRingBuffer).</summary>
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

    #region Device Loss

    /// <summary>
    /// Вызывается когда аудиоустройство пропало во время воспроизведения
    /// (callback от <see cref="NAudioBackend"/>).
    /// Отменяет lifetime CTS — декодер получит OperationCanceledException и завершится.
    /// </summary>
    internal void NotifyDeviceLost()
    {
        if (_disposed) return;
        Log.Error("[AudioPipeline] Audio device lost during playback");
        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    #endregion

    #region Decoder Loop

    /// <summary>
    /// Запускает decoder loop на ВЫДЕЛЕННОМ потоке с повышенным приоритетом
    /// для предотвращения подтормаживаний от ОС.
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
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var trackIdShort = _streamInfo.TrackId?.Length > ShortTrackIdLength
            ? _streamInfo.TrackId[..ShortTrackIdLength]
            : _streamInfo.TrackId ?? "?";

        var decoderThread = new Thread(() =>
        {
            try
            {
                DecoderLoopAsync(urlRefresher, options, onTrackEnded, onError, token).GetAwaiter().GetResult();
                tcs.TrySetResult(null);
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
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

        if (cts == null || task == null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        try { await task.WaitAsync(timeout); }
        catch (TimeoutException) { Log.Warn("[AudioPipeline] Decoder stop timeout"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"[AudioPipeline] Decoder stop error: {ex.Message}"); }

        _decoderTask = null;
        _decoderCts = null;

        try { cts.Dispose(); } catch (ObjectDisposedException) { }

        Log.Debug("[AudioPipeline] Decoder stopped");
    }

    /// <summary>
    /// Основной цикл декодирования. Читает фреймы из source, декодирует и пишет в pcmBuffer.
    /// IOException с кодом "файл не найден" трактуется как фатальная ошибка кэша
    /// и выбрасывает <see cref="CacheInvalidatedException"/> без retry.
    /// </summary>
    private async Task DecoderLoopAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        var previousLatencyMode = System.Runtime.GCSettings.LatencyMode;
        try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; }
        catch (Exception ex) { Log.Debug($"[AudioPipeline] Could not set GC latency mode: {ex.Message}"); }

        int retryCount = 0;
        int backoffCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();

                if (_pcmBuffer.Available < _decoder.MaxFrameSize * _decoder.Channels)
                {
                    if (backoffCount < MaxYieldsBeforeDelay)
                    {
                        await Task.Yield();
                        backoffCount++;
                    }
                    else
                    {
                        await Task.Delay(BufferFullDelayMs, ct);
                    }
                    continue;
                }

                backoffCount = 0;
                AudioFrame? frame;

                try
                {
                    frame = await _source.ReadFrameAsync(ct);
                    retryCount = 0;
                }
                catch (OperationCanceledException) { break; }
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
                catch (Exceptions.ChunkDownloadFatalException) { throw; }
                catch (FileNotFoundException ex)
                {
                    Log.Warn($"[AudioPipeline] Cache file deleted during playback: {ex.Message}");
                    throw new CacheInvalidatedException("Cache file was deleted during playback.", ex);
                }
                catch (DirectoryNotFoundException ex)
                {
                    Log.Warn($"[AudioPipeline] Cache directory deleted during playback: {ex.Message}");
                    throw new CacheInvalidatedException("Cache directory was deleted during playback.", ex);
                }
                catch (IOException ex) when (IsCacheFileMissing(ex))
                {
                    Log.Warn($"[AudioPipeline] Cache IO error (file gone): {ex.Message}");
                    throw new CacheInvalidatedException("Cache file became unavailable during playback.", ex);
                }
                catch (Exception ex) when (retryCount++ < options.MaxRetryAttempts)
                {
                    Log.Warn($"[AudioPipeline] Read retry {retryCount}: {ex.Message}");
                    await Task.Delay(options.RetryDelay, ct);
                    continue;
                }

                if (frame == null)
                {
                    if (ct.IsCancellationRequested) break;
                    await DrainBufferAsync(ct);
                    if (ct.IsCancellationRequested) break;

                    onTrackEnded?.Invoke();
                    break;
                }

                try
                {
                    int skipCount = Volatile.Read(ref _skipFramesCounter);
                    bool needReset = skipCount > 0;

                    int samplesDecoded = needReset
                        ? _decoder.DecodeWithReset(frame.Value.Data.Span, _decodeBuffer)
                        : _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);

                    if (needReset)
                    {
                        Interlocked.Decrement(ref _skipFramesCounter);
                        continue;
                    }

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
                catch (Exception ex) { Log.Warn($"[AudioPipeline] Decode error (non-fatal): {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (CacheInvalidatedException ex)
        {
            Log.Warn($"[AudioPipeline] Playback stopped: cache invalidated ({ex.Message})");
            onError?.Invoke(ex);
        }
        catch (Exception ex)
        {
            Log.Error($"[AudioPipeline] Decoder fatal: {ex.Message}", ex);
            onError?.Invoke(ex);
        }
        finally
        {
            try { System.Runtime.GCSettings.LatencyMode = previousLatencyMode; } catch { }
        }
    }

    /// <summary>
    /// Определяет является ли IOException следствием отсутствия файла по HResult.
    /// </summary>
    private static bool IsCacheFileMissing(IOException ex) =>
        ex.HResult is HResultFileNotFound or HResultPathNotFound;

    private async Task DrainBufferAsync(CancellationToken ct)
    {
        while (!_pcmBuffer.IsEmpty && !ct.IsCancellationRequested)
        {
            await Task.Delay(DrainDelayMs, ct);
        }
    }

    #endregion

    #region Playback Control

    public void ActivateFillLoop()
    {
        if (_disposed) return;
        _backend.ActivateFillLoop();
    }

    public bool WaitForBackendWarmup(int timeoutMs = 100)
    {
        if (_disposed) return false;
        return _backend.WaitForWarmup(timeoutMs);
    }

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

    public void PrepareForSeek(long targetMs = -1)
    {
        int skipFrames = _source.Codec == AudioCodec.Opus ? SkipFramesAfterSeekOpus : 0;
        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _seekTargetMs, targetMs);
    }

    public void SetDecodedSamplesPosition(long samples)
    {
        Interlocked.Exchange(ref _decodedSamples, samples);
    }

    #endregion

    #region Audio Callback

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
            await Task.Delay(WaitBufferDelayMs, ct);
            waited += WaitBufferDelayMs;
        }
    }

    #endregion

    #region Dispose

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _lifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        if (_decoderTask != null)
        {
            try { await _decoderTask.WaitAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs)); }
            catch { }
        }

        // Never-stop: не останавливаем backend.
        // Flush выставит gate=false, provider вернёт тишину сам.
        try { _backend.Flush(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Debug($"[AudioPipeline] Backend flush on dispose: {ex.Message}"); }

        _decoder.Dispose();
        await _source.DisposeAsync();

        ArrayPool<float>.Shared.Return(_decodeBuffer);

        try { _decoderCts?.Dispose(); } catch (ObjectDisposedException) { }
        try { _lifetimeCts.Dispose(); } catch (ObjectDisposedException) { }

        Log.Debug("[AudioPipeline] Disposed");
    }

    #endregion
}