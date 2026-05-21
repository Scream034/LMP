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
/// в <see cref="AudioCallback"/>, НЕ через hardware volume backend'а.
/// Это обеспечивает:</para>
/// <list type="bullet">
///   <item>Корректную работу volume curves (Quadratic, Cubic, etc.)</item>
///   <item>Boost выше 100% (gain > 1.0)</item>
///   <item>Audio normalization в том же callback (zero-copy)</item>
/// </list>
///
/// <para><b>Нормализация (EBU R128 / ITU-R BS.1770-4):</b></para>
/// <para>Делегирована <see cref="EbuR128Analyzer"/> — отдельному модулю,
/// отвечающему за K-weighted LUFS анализ, gating blocks и gain state machine.
/// Pipeline вызывает <see cref="EbuR128Analyzer.ProcessSamples"/> из fill thread
/// для получения norm gain, который комбинируется с volume gain.</para>
///
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item>Decoder loop — dedicated thread (AboveNormal priority)</item>
///   <item>AudioCallback — вызывается из fill thread NAudioBackend</item>
///   <item><see cref="_gain"/> — volatile float, lock-free read/write</item>
///   <item>Normalization state — инкапсулирован в <see cref="EbuR128Analyzer"/></item>
/// </list>
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

    /// <summary>EBU R128 анализатор нормализации (отдельный модуль).</summary>
    private readonly EbuR128Analyzer _analyzer;

    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private volatile bool _disposed;

    /// <summary>Количество фреймов, которые нужно пропустить после seek (encoder delay).</summary>
    private int _skipFramesCounter;

    /// <summary>Общее количество успешно декодированных и отправленных в буфер сэмплов.</summary>
    private long _decodedSamples;

    /// <summary>Target timestamp для точного позиционирования. Фреймы с timestamp ниже этого пропускаются.</summary>
    private long _seekTargetMs = -1;

    /// <summary>
    /// Текущий gain применяемый к PCM сэмплам в audio callback.
    /// Записывается из UI/engine потока, читается из fill thread backend'а.
    /// Gain применяется мгновенно к следующему chunk (~50ms).
    /// Плавность обеспечивается provider buffer (~500ms) в NAudioBackend.
    /// </summary>
    private volatile float _gain = 1.0f;

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

    /// <summary>
    /// EBU R128 анализатор нормализации.
    /// Используется внешним кодом для конфигурации нормализации,
    /// установки gain из метаданных и callback'ов фиксации.
    /// </summary>
    public EbuR128Analyzer Analyzer => _analyzer;

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
            int bufferSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(rawSize, 16));
            var pcmBuffer = new LockFreeRingBuffer<float>(bufferSize);

            decodeBuffer = ArrayPool<float>.Shared.Rent(DecoderBufferFrames * decoder.Channels);

            var streamInfo = BuildStreamInfo(source, trackId, bitrateHint);

            var pipeline = new AudioPipeline(
                source, decoder, sharedBackend, pcmBuffer, decodeBuffer,
                streamInfo, lifetimeCts);

            sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);

            if (sharedBackend is NAudioBackend naBackend)
            {
                naBackend.SetDeviceLostCallback(pipeline.NotifyDeviceLost);
                naBackend.SetStarvationCallback(pipeline.NotifyStarvation);
            }

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
                catch (UrlExpiredException) when (urlRefresher != null)
                {
                    var newUrl = await urlRefresher(ct);
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        Log.Info("[AudioPipeline] URL refreshed in decoder loop");
                        continue;
                    }
                    throw;
                }
                catch (ChunkDownloadFatalException) { throw; }
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

    /// <summary>
    /// Выполняет pre-scan через <see cref="EbuR128Analyzer.PreScanAsync"/>.
    /// Фиксирует gain и возвращает source в начало для playback.
    /// </summary>
    /// <remarks>
    /// <para><b>Условия пропуска:</b></para>
    /// <list type="bullet">
    ///   <item>Нормализация отключена</item>
    ///   <item>Source не поддерживает seek (стриминг без кэша)</item>
    ///   <item>Gain уже зафиксирован (YouTube metadata или DB cache)</item>
    /// </list>
    /// <para><b>Fallback:</b> при ошибке gain не фиксируется,
    /// real-time анализ в <see cref="EbuR128Analyzer.ProcessSamples"/> работает как fallback.</para>
    /// </remarks>
    public async Task PreScanNormalizationAsync(CancellationToken ct)
    {
        if (!_analyzer.IsEnabled || !_source.CanSeek)
            return;

        if (_analyzer.IsGainLocked)
        {
            Log.Debug("[AudioPipeline] Pre-scan skipped: gain already locked");
            return;
        }

        try
        {
            float rawGain = await _analyzer.PreScanAsync(_source, _decoder, _decodeBuffer, ct);

            _analyzer.LockGain(rawGain);

            await _source.SeekAsync(0, ct);

            int skipFrames = _source.Codec == AudioCodec.Opus ? SkipFramesAfterSeekOpus : 0;
            Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
            Volatile.Write(ref _seekTargetMs, -1L);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Pre-scan failed (fallback to real-time): {ex.Message}");
        }
    }

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

    /// <summary>
    /// Запускает воспроизведение и возобновляет сетевой буфер источника.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(true); // Открываем сетевой затвор
        _backend.Start();
        Log.Debug("[AudioPipeline] Backend started");
    }

    /// <summary>
    /// Приостанавливает воспроизведение и замораживает сетевой буфер источника.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;
        _source.SetPlaybackActive(false); // Закрываем сетевой затвор на паузе
        _backend.Stop();
    }

    public void Flush()
    {
        if (_disposed) return;
        _backend.Flush();
        _pcmBuffer.Clear();
    }

    /// <summary>
    /// Вызывается когда backend не получает данных > 1 секунды при открытом gate.
    /// Логирует диагностику состояния decoder / source / ring buffer
    /// для пост-мортем анализа причины starvation.
    /// </summary>
    internal void NotifyStarvation()
    {
        if (_disposed) return;

        var decoderTask = _decoderTask;
        bool decoderAlive = decoderTask is { IsCompleted: false };
        int ringCount = _pcmBuffer.Count;
        int ringAvailable = _pcmBuffer.Available;

        Log.Error($"[AudioPipeline] Starvation: decoder={(decoderAlive ? "alive" : "dead")}, " +
                  $"ring={ringCount}/{ringCount + ringAvailable}, " +
                  $"source={_source.GetType().Name}, " +
                  $"pos={_source.PositionMs}ms/{_source.DurationMs}ms");

        // Если decoder мёртв (завершился) но ring buffer пуст и track не закончился —
        // это ненормальная ситуация. Source мог потерять данные.
        if (!decoderAlive && ringCount == 0 && _source.PositionMs < _source.DurationMs - 1000)
        {
            Log.Error("[AudioPipeline] Decoder died prematurely — likely I/O starvation or unhandled exception");
        }
    }

    /// <summary>
    /// Устанавливает gain мгновенно.
    /// Gain применяется программно к PCM сэмплам в <see cref="AudioCallback"/>.
    /// </summary>
    /// <remarks>
    /// <para>Новый gain применяется к следующему chunk (~50ms). Уже буферизованный
    /// PCM в provider (до 500ms) доиграет со старым gain — это обеспечивает
    /// естественную плавность без артефактов.</para>
    /// </remarks>
    /// <param name="gain">Gain множитель (0.0 = тишина, 1.0 = 100%, до MaxVolumeGain).</param>
    public void SetGain(float gain)
    {
        if (_disposed) return;
        _gain = Math.Clamp(gain, 0f, MaxVolumeGain);
    }

    /// <summary>
    /// Подготавливает pipeline к seek-операции: устанавливает skip frames,
    /// target timestamp и сбрасывает анализ нормализации (если gain не зафиксирован).
    /// </summary>
    public void PrepareForSeek(long targetMs = -1)
    {
        int skipFrames = _source.Codec == AudioCodec.Opus ? SkipFramesAfterSeekOpus : 0;
        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _seekTargetMs, targetMs);

        _analyzer.PrepareForSeek();
    }

    public void SetDecodedSamplesPosition(long samples)
    {
        Interlocked.Exchange(ref _decodedSamples, samples);
    }

    /// <summary>
    /// Возвращает зафиксированный gain нормализации текущего трека.
    /// Используется AudioPlayer для передачи в следующий pipeline через
    /// <see cref="SetInitialNormalizationGain"/>, устраняя cold-start скачок
    /// при смене трека.
    /// </summary>
    /// <returns>
    /// Locked gain если фаза анализа завершена; текущий smoothed gain если
    /// анализ ещё идёт; 1.0f если нормализация отключена.
    /// </returns>
    public float GetLockedNormalizationGain() => _analyzer.GetLockedGain();

    /// <summary>
    /// Устанавливает начальный gain нормализации для нового трека.
    /// Вызывается из AudioPlayer сразу после создания pipeline, передавая
    /// locked gain предыдущего трека как стартовую точку.
    /// Устраняет cold-start скачок: первый callback использует этот gain
    /// вместо 1.0f, пока не завершится первый gating block (~400ms).
    /// </summary>
    /// <param name="gain">Locked gain предыдущего трека или 1.0f если нет предыдущего.</param>
    public void SetInitialNormalizationGain(float gain)
    {
        if (_disposed) return;
        _analyzer.SetInitialGain(gain);
    }

    #endregion

    #region Audio Callback

    /// <summary>
    /// Callback вызываемый из fill thread backend'а для заполнения аудио-буфера.
    /// </summary>
    /// <remarks>
    /// <para><b>Порядок обработки:</b></para>
    /// <list type="number">
    ///   <item>Чтение PCM из ring buffer</item>
    ///   <item>K-weighted LUFS анализ на RAW сигнале → normGain (без модификации сэмплов)</item>
    ///   <item>combinedGain = normGain × volumeGain</item>
    ///   <item>True Peak Limiter: chunk-level peak scan → safe combined gain без дисторции</item>
    /// </list>
    /// <para><b>Zero-alloc.</b></para>
    /// </remarks>
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

        if (read > 0)
        {
            var samples = buffer[..read];

            float normGain = _analyzer.IsEnabled
                ? _analyzer.ProcessSamples(samples)
                : 1.0f;

            float combinedGain = normGain * _gain;

            ApplyGainWithTruePeak(samples, combinedGain);
        }

        return read / _decoder.Channels;
    }

    /// <summary>
    /// Применяет gain к chunk с True Peak защитой от клиппинга.
    /// </summary>
    /// <remarks>
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Scan: найти максимальный абсолютный сэмпл в chunk</item>
    ///   <item>Limit: если <c>peak × gain &gt; ceiling</c> → снизить gain до <c>ceiling / peak</c></item>
    ///   <item>Apply: умножить все сэмплы на safeGain равномерно</item>
    /// </list>
    /// <para><b>SIMD-оптимизация:</b> оба прохода (peak scan и gain apply) используют
    /// <see cref="Vector{T}"/> для обработки <see cref="Vector{Single}.Count"/> сэмплов
    /// за одну инструкцию (8 float на AVX2, 4 на SSE2). Обращение к <paramref name="samples"/>
    /// через <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)"/> исключает bounds check и копирование.</para>
    /// <para><b>Почему лучше tanh/SoftClip:</b></para>
    /// <para>tanh — нелинейный вейвшейпер, вносит нечётные гармоники (3-я, 5-я, 7-я).
    /// На транзиентах это воспринимается как треск и крякание. True Peak Limiter
    /// снижает gain равномерно для всего chunk: форма волны не искажается,
    /// chunk просто тише — артефактов нет.</para>
    /// <para><b>Zero-alloc. Два прохода по samples — scan + apply.</b></para>
    /// </remarks>
    /// <param name="samples">PCM сэмплы для обработки (in-place).</param>
    /// <param name="gain">Желаемый gain множитель.</param>
    /// <param name="ceiling">True Peak ceiling (по умолчанию 1.0 = 0 dBFS).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyGainWithTruePeak(Span<float> samples, float gain, float ceiling = 1.0f)
    {
        if (gain < 0.0005f)
        {
            samples.Clear();
            return;
        }

        if (MathF.Abs(gain - 1f) < 0.0005f)
            return;

        int length = samples.Length;
        ref float samplesRef = ref MemoryMarshal.GetReference(samples);

        // ═══ Pass 1: Peak scan ═══
        float peak = 0f;
        int i = 0;

        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            var vPeak = Vector<float>.Zero;
            int vectorEnd = length - length % Vector<float>.Count;

            for (; i < vectorEnd; i += Vector<float>.Count)
            {
                var v = Unsafe.As<float, Vector<float>>(
                    ref Unsafe.Add(ref samplesRef, i));
                vPeak = Vector.Max(vPeak, Vector.Abs(v));
            }

            for (int j = 0; j < Vector<float>.Count; j++)
            {
                if (vPeak[j] > peak) peak = vPeak[j];
            }
        }

        for (; i < length; i++)
        {
            float abs = MathF.Abs(Unsafe.Add(ref samplesRef, i));
            if (abs > peak) peak = abs;
        }

        float safeGain = gain;
        if (peak > 0f && peak * gain > ceiling)
            safeGain = ceiling / peak;

        // ═══ Pass 2: Apply gain ═══
        i = 0;

        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(safeGain);
            int vectorEnd = length - length % Vector<float>.Count;

            for (; i < vectorEnd; i += Vector<float>.Count)
            {
                ref var v = ref Unsafe.As<float, Vector<float>>(
                    ref Unsafe.Add(ref samplesRef, i));
                v *= vGain;
            }
        }

        for (; i < length; i++)
            Unsafe.Add(ref samplesRef, i) *= safeGain;
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