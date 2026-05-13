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
/// Полный конвейер воспроизведения: Source → Decoder → PCM Buffer → Gain/Normalization → Backend.
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
/// <para><b>Нормализация (EBU R128-inspired):</b></para>
/// <para>Двухфазная статическая нормализация, аналогичная Spotify/YouTube Music:</para>
/// <list type="bullet">
///   <item><b>Фаза анализа</b> (~3 сек): быстрое измерение integrated LUFS трека</item>
///   <item><b>Фаза фиксации</b>: gain замораживается и не меняется до конца трека</item>
/// </list>
/// <para>Gain стабилен и предсказуем — одно и то же место трека всегда звучит одинаково.</para>
/// 
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item>Decoder loop — dedicated thread (AboveNormal priority)</item>
///   <item>AudioCallback — вызывается из fill thread NAudioBackend</item>
///   <item><see cref="_gain"/> — volatile float, lock-free read/write</item>
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

    // ─── Normalization Constants (EBU R128-inspired) ───

    /// <summary>
    /// Длительность фазы анализа в секундах.
    /// За это время накапливаются данные для расчёта LUFS — gain НЕ применяется.
    /// После завершения gain вычисляется мгновенно и фиксируется навсегда.
    /// </summary>
    private const float AnalysisPhaseSeconds = 3.0f;

    /// <summary>Минимальный gain нормализации.</summary>
    private const float MinNormalizationGain = 0.1f;

    /// <summary>Максимальный gain нормализации по умолчанию.</summary>
    private const float DefaultMaxNormalizationGain = 3.0f;

    /// <summary>
    /// Порог мощности ниже которого блок считается тишиной (EBU R128 absolute gate: -70 LUFS).
    /// </summary>
    private const float SilenceGatingPower = 1e-7f;

    /// <summary>
    /// Длина блока для LUFS-анализа в миллисекундах.
    /// EBU R128 использует 400ms блоки. Мы используем размер callback chunk (~50ms)
    /// и агрегируем в скользящее окно.
    /// </summary>
    private const int LufsBlockMs = 400;

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

    /// <summary>
    /// Текущий gain применяемый к PCM сэмплам в audio callback.
    /// Записывается из UI/engine потока, читается из fill thread backend'а.
    /// Gain применяется мгновенно к следующему chunk (~50ms).
    /// Плавность обеспечивается provider buffer (~500ms) в NAudioBackend.
    /// </summary>
    private volatile float _gain = 1.0f;

    // ─── Normalization State (EBU R128-inspired static normalization) ───

    /// <summary>Включена ли нормализация аудио.</summary>
    private volatile bool _normalizationEnabled;

    /// <summary>Целевой уровень LUFS для нормализации.</summary>
    private float _normalizationTargetLufs = -14f;

    /// <summary>Максимальный gain нормализации.</summary>
    private float _maxNormalizationGain = DefaultMaxNormalizationGain;

    /// <summary>
    /// Зафиксированный gain после завершения фазы анализа.
    /// NaN = ещё не зафиксирован (фаза анализа).
    /// </summary>
    private float _lockedGain = float.NaN;

    /// <summary>
    /// Суммарная мощность (sum of squares) всех не-тихих блоков за фазу анализа.
    /// Используется для вычисления integrated LUFS.
    /// </summary>
    private double _analysisSumPower;

    /// <summary>
    /// Количество не-тихих сэмплов за фазу анализа.
    /// </summary>
    private long _analysisSampleCount;

    /// <summary>
    /// Общее количество сэмплов, обработанных в нормализации (для определения конца фазы анализа).
    /// </summary>
    private long _normalizationProcessedSamples;

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
    /// Включает/выключает нормализацию аудио на лету.
    /// </summary>
    /// <remarks>
    /// <para><b>Алгоритм (двухфазный, без всплеска):</b></para>
    /// <list type="bullet">
    ///   <item><b>Фаза анализа (~3 сек):</b> накапливаем LUFS данные, gain = 1.0 (не применяется).
    ///     Пользователь слышит трек без изменений — нет резкого скачка.</item>
    ///   <item><b>Фиксация:</b> gain вычисляется мгновенно из accumulated LUFS и фиксируется.
    ///     Provider buffer (~500ms) естественно сглаживает переход с 1.0 → locked gain.</item>
    ///   <item><b>Остаток трека:</b> gain константен — нет pumping, нет изменений.</item>
    /// </list>
    /// </remarks>
    public void SetNormalization(bool enabled, float targetLufs = -14f, float maxGain = DefaultMaxNormalizationGain)
    {
        _normalizationTargetLufs = targetLufs;
        _maxNormalizationGain = Math.Max(1f, maxGain);

        if (enabled && !_normalizationEnabled)
        {
            ResetNormalizationState();
            _normalizationEnabled = true;
            Log.Debug($"[AudioPipeline] Normalization ON: target={targetLufs}LUFS, maxGain={maxGain:F1}x");
        }
        else if (!enabled && _normalizationEnabled)
        {
            _normalizationEnabled = false;
            _lockedGain = float.NaN;
            Log.Debug("[AudioPipeline] Normalization OFF");
        }
        else if (enabled)
        {
            // Параметры изменились — перезапуск анализа
            ResetNormalizationState();
            Log.Debug($"[AudioPipeline] Normalization params updated: target={targetLufs}LUFS, maxGain={maxGain:F1}x");
        }
    }

    /// <summary>
    /// Сбрасывает состояние нормализации для начала новой фазы анализа.
    /// </summary>
    private void ResetNormalizationState()
    {
        _lockedGain = float.NaN;
        _analysisSumPower = 0;
        _analysisSampleCount = 0;
        _normalizationProcessedSamples = 0;
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

    /// <summary>
    /// Callback вызываемый из fill thread backend'а для заполнения аудио-буфера.
    /// Применяет software gain и нормализацию.
    /// </summary>
    /// <remarks>
    /// <para><b>Порядок обработки:</b></para>
    /// <list type="number">
    ///   <item>Чтение PCM из ring buffer</item>
    ///   <item>Применение volume gain (мгновенный)</item>
    ///   <item>Применение нормализации (статический gain после фазы анализа)</item>
    /// </list>
    /// <para><b>Zero-alloc:</b> Никаких аллокаций в hot path.</para>
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

            ApplyGain(samples);

            if (_normalizationEnabled)
                ApplyNormalization(samples);
        }

        return read / _decoder.Channels;
    }

    /// <summary>
    /// Применяет volume gain к сэмплам. Мгновенный (без интерполяции).
    /// </summary>
    /// <remarks>
    /// <para>Плавность при изменении громкости обеспечивается архитектурно:
    /// NAudioBackend.BufferedWaveProvider хранит ~500ms уже записанных данных
    /// со старым gain. Новый gain влияет только на следующий chunk (~50ms).</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyGain(Span<float> samples)
    {
        float currentGain = _gain;

        if (MathF.Abs(currentGain - 1f) < 0.0005f)
            return;

        if (currentGain < 0.0005f)
        {
            samples.Clear();
            return;
        }

        if (currentGain <= 1f)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= currentGain;
            return;
        }

        for (int i = 0; i < samples.Length; i++)
            samples[i] = SoftClip(samples[i] * currentGain);
    }

    /// <summary>
    /// Мягкое ограничение сэмпла через tanh для значений за пределами [-1, 1].
    /// Предотвращает цифровой клиппинг при boost/normalization gain > 1.0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SoftClip(float sample)
    {
        if (sample is > -1f and < 1f) return sample;
        return MathF.Tanh(sample);
    }

    /// <summary>
    /// Применяет статическую нормализацию к блоку PCM сэмплов.
    /// </summary>
    /// <remarks>
    /// <para><b>Алгоритм (двухфазный с provisional gain):</b></para>
    ///
    /// <para><b>Фаза 1 — Анализ с provisional gain (первые ~3 сек):</b></para>
    /// <list type="number">
    ///   <item>Мощность блока измеряется ДО применения gain (оригинальный сигнал)</item>
    ///   <item>Provisional gain вычисляется из накопленных LUFS и применяется сразу</item>
    ///   <item>Gain уточняется с каждым блоком — плавная конвергенция к финальному значению</item>
    ///   <item>Блоки тише -70 LUFS игнорируются (EBU R128 absolute gating)</item>
    /// </list>
    ///
    /// <para><b>Момент фиксации (~3 сек):</b></para>
    /// <list type="number">
    ///   <item>Gain вычисляется финально из accumulated LUFS</item>
    ///   <item>Фиксируется в <see cref="_lockedGain"/> — дальнейшие изменения невозможны</item>
    ///   <item>Переход незаметен: provisional gain уже близок к финальному</item>
    /// </list>
    ///
    /// <para><b>Фаза 2 — Воспроизведение:</b></para>
    /// <list type="number">
    ///   <item>Применяется константный locked gain — нет pumping, нет изменений</item>
    ///   <item>Fast path: одна проверка + умножение/soft-clip</item>
    /// </list>
    ///
    /// <para><b>Почему provisional, а не ожидание 3 сек:</b>
    /// Ожидание без gain приводит к громкому всплеску на старте для треков
    /// с высоким LUFS (-6…-8). Provisional gain устраняет это: первый блок (~50ms)
    /// даёт грубую оценку, которая моментально конвергирует за 200-400ms.</para>
    ///
    /// <para><b>Формула:</b> gain = clamp(10^((target - measured_lufs) / 20), Min, Max)</para>
    /// <para><b>Zero-alloc:</b> Никаких аллокаций.</para>
    /// </remarks>
    private void ApplyNormalization(Span<float> samples)
    {
        // Fast path: gain зафиксирован — просто применяем (основной режим работы)
        if (!float.IsNaN(_lockedGain))
        {
            ApplyNormGainToSamples(samples, _lockedGain);
            return;
        }

        // ─── Фаза анализа: измеряем оригинальный сигнал, затем применяем provisional gain ───

        // 1. Мощность блока (mean square) — измеряем ДО любого normalization gain
        float sumSquares = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sumSquares += s * s;
        }

        float blockPower = samples.Length > 0 ? sumSquares / samples.Length : 0f;

        // 2. EBU R128 absolute gating: пропускаем блоки тише -70 LUFS
        if (blockPower > SilenceGatingPower)
        {
            _analysisSumPower += sumSquares;
            _analysisSampleCount += samples.Length;
        }

        _normalizationProcessedSamples += samples.Length;

        // 3. Вычисляем provisional gain из накопленных данных
        float currentGain = 1.0f;

        if (_analysisSampleCount > 0)
        {
            float integratedPower = (float)(_analysisSumPower / _analysisSampleCount);
            float measuredLufs = PowerToLufs(integratedPower);
            float rawGain = MathF.Pow(10f, (_normalizationTargetLufs - measuredLufs) / 20f);
            currentGain = Math.Clamp(rawGain, MinNormalizationGain, _maxNormalizationGain);
        }

        // 4. Проверяем завершение фазы анализа → фиксируем gain навсегда
        long analysisSamplesThreshold = (long)(_decoder.SampleRate * _decoder.Channels * AnalysisPhaseSeconds);

        if (_normalizationProcessedSamples >= analysisSamplesThreshold)
        {
            _lockedGain = currentGain;

            Log.Debug($"[AudioPipeline] Normalization locked: gain={_lockedGain:F3} " +
                      $"(analyzed {_normalizationProcessedSamples / ((long)_decoder.SampleRate * _decoder.Channels):F1}s, " +
                      $"target={_normalizationTargetLufs}LUFS)");
        }

        // 5. Применяем gain (provisional или только что зафиксированный) к текущему блоку
        ApplyNormGainToSamples(samples, currentGain);
    }

    /// <summary>
    /// Конвертирует линейную мощность (mean square) в LUFS.
    /// LUFS ≈ 10 × log10(meanSquare) — упрощённая формула без K-weighting.
    /// Для музыки отклонение от полного EBU R128 составляет 1-3 dB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PowerToLufs(float meanSquare)
    {
        if (meanSquare < 1e-10f) return -70f;
        return 10f * MathF.Log10(meanSquare);
    }

    /// <summary>
    /// Применяет normalization gain ко всем сэмплам с soft-clip при необходимости.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyNormGainToSamples(Span<float> samples, float gain)
    {
        if (MathF.Abs(gain - 1f) < 0.001f) return;

        if (gain <= 1f)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= gain;
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] = SoftClip(samples[i] * gain);
        }
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