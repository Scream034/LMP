using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Backends;
using LMP.Core.Audio.Decoders;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
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
/// <para>Двухфазная статическая нормализация, аналогичная Spotify/YouTube Music:</para>
/// <list type="bullet">
///   <item><b>Фаза анализа (~3 сек):</b> K-weighted LUFS измерение в 400 мс gating blocks.
///     Provisional gain конвергирует с каждым блоком.</item>
///   <item><b>Фиксация:</b> Relative gating (−10 LU) → финальный integrated LUFS.
///     Gain замораживается навсегда.</item>
///   <item><b>Остаток трека:</b> Константный locked gain — нет pumping, нет изменений.</item>
/// </list>
/// <para>Нормализация применяется ДО volume gain — K-weighting измеряет raw сигнал.</para>
///
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item>Decoder loop — dedicated thread (AboveNormal priority)</item>
///   <item>AudioCallback — вызывается из fill thread NAudioBackend</item>
///   <item><see cref="_gain"/> — volatile float, lock-free read/write</item>
///   <item>Normalization state — только из fill thread (single writer, нет гонок)</item>
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

    // ─── Normalization Constants (EBU R128 / ITU-R BS.1770-4) ───

    /// <summary>
    /// Длительность фазы анализа в секундах.
    /// ~3 сек даёт ≈26 gating blocks (400 мс, 75% overlap) для стабильного integrated LUFS.
    /// Provisional gain применяется сразу после первого завершённого блока (~400 мс).
    /// После завершения фазы gain фиксируется навсегда.
    /// </summary>
    private const float AnalysisPhaseSeconds = 3.0f;

    /// <summary>Минимальный gain нормализации (защита от чрезмерного подавления).</summary>
    private const float MinNormalizationGain = 0.1f;

    /// <summary>Максимальный gain нормализации по умолчанию.</summary>
    private const float DefaultMaxNormalizationGain = 3.0f;

    /// <summary>
    /// Длительность gating block в секундах (EBU R128 / ITU-R BS.1770-4: 400 мс).
    /// Overlap 75% → hop size = 100 мс.
    /// </summary>
    private const double GatingBlockSeconds = 0.4;

    /// <summary>
    /// Абсолютный порог гейтинга: −70 LUFS (ITU-R BS.1770-4 §3).
    /// Блоки тише этого уровня полностью игнорируются при анализе.
    /// </summary>
    private const double AbsoluteGateThresholdLufs = -70.0;

    /// <summary>
    /// Относительный порог гейтинга: −10 LU ниже ungated integrated loudness (ITU-R BS.1770-4 §3).
    /// Применяется поверх absolute gating при финальной фиксации gain.
    /// Устраняет влияние аномально тихих пассажей на измерение.
    /// </summary>
    private const double RelativeGateOffsetLu = -10.0;

    /// <summary>
    /// Константа из ITU-R BS.1770-4 уравнения (2): −0.691 dBFS.
    /// Компенсирует K-weighting gain на 997 Hz,
    /// обеспечивая: 0 dBFS sine @ 1 kHz → −3.01 LUFS.
    /// </summary>
    private const double LufsOffset = -0.691;

    /// <summary>
    /// Максимальное количество 400 мс gating blocks, хранимых для анализа.
    /// 3 сек / 0.1 сек hop ≈ 26 блоков + запас = 64.
    /// </summary>
    private const int MaxGatingBlocks = 64;

    /// <summary>
    /// Максимальная длительность pre-scan анализа в секундах.
    /// 120 сек даёт ~300 gating blocks без overlap — достаточно для стабильного
    /// integrated LUFS любого музыкального трека. Scan локального файла
    /// занимает ~50-150ms (Opus decode + K-weighting очень быстры).
    /// Для стриминга pre-scan не используется — fallback на real-time анализ.
    /// </summary>
    private const float MaxScanDurationSeconds = 120f;

    /// <summary>
    /// Максимальное количество gating blocks при pre-scan.
    /// 120s / 0.4s = 300 blocks + запас = 512.
    /// </summary>
    private const int MaxScanGatingBlocks = 512;

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

    // ─── Normalization State (EBU R128 / ITU-R BS.1770-4) ───
    // Все поля нормализации пишутся и читаются ТОЛЬКО из fill thread (AudioCallback).
    // Volatile не требуется — single writer, нет гонок.

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
    /// Сигнал отложенного сброса нормализации.
    /// Устанавливается из любого потока через Interlocked, читается и исполняется
    /// строго из fill thread в начале <see cref="GetNormalizationGain"/>.
    /// Это устраняет data race между command thread и fill thread без lock'ов.
    /// 1 = сброс запрошен, 0 = нет запроса.
    /// </summary>
    private volatile int _pendingNormReset;

    /// <summary>
    /// Начальный gain нормализации для нового трека.
    /// Используется как стартовая точка <see cref="_smoothedNormGain"/> до завершения
    /// первого gating block. Устанавливается через <see cref="SetInitialNormalizationGain"/>
    /// из locked gain предыдущего трека — устраняет cold-start скачок при смене трека.
    /// </summary>
    private float _startingNormGain = 1.0f;

    /// <summary>
    /// Сглаженный gain нормализации, применяемый к PCM в <see cref="AudioCallback"/>.
    /// В фазе анализа lerp-интерполирует к provisional/locked gain,
    /// устраняя слышимые скачки при конвергенции.
    /// После фиксации (<see cref="_lockedGain"/> != NaN) мгновенно принимает locked значение.
    /// Пишется и читается только из fill thread.
    /// </summary>
    private float _smoothedNormGain = 1.0f;

    /// <summary>
    /// K-weighting фильтр (ITU-R BS.1770-4): shelf + high-pass на канал.
    /// Создаётся при инициализации pipeline. Используется только из fill thread.
    /// </summary>
    private readonly KWeightingFilter _kWeightFilter;

    /// <summary>
    /// Per-channel sum of K-weighted squared samples для текущего незавершённого 400 мс блока.
    /// Обнуляется при завершении каждого gating block.
    /// </summary>
    private readonly double[] _blockChannelSumSq;

    /// <summary>Количество фреймов (на канал), накопленных в текущем незавершённом блоке.</summary>
    private int _blockFrameCount;

    /// <summary>
    /// Размер одного gating block во фреймах (sampleRate × GatingBlockSeconds).
    /// Вычисляется один раз в конструкторе.
    /// </summary>
    private readonly int _gatingBlockSizeFrames;

    /// <summary>
    /// Loudness (sum of channel mean-squares) каждого завершённого gating block,
    /// прошедшего absolute gate (−70 LUFS).
    /// </summary>
    private readonly double[] _gatingBlockPowers;

    /// <summary>Количество завершённых gating блоков в <see cref="_gatingBlockPowers"/>.</summary>
    private int _gatingBlockCount;

    /// <summary>
    /// Общее количество фреймов (на канал), обработанных нормализацией.
    /// Используется для определения конца фазы анализа.
    /// </summary>
    private long _normalizationProcessedFrames;

    /// <summary>
    /// Режим нормализации: двусторонний (Spotify) или только понижение (YouTube).
    /// Применяется в <see cref="LockNormalizationGain"/> — cap gain ≤ 1.0 для DownwardOnly.
    /// </summary>
    private NormalizationMode _normalizationMode = NormalizationMode.Bidirectional;

    /// <summary>
    /// Callback, вызываемый при фиксации gain нормализации из любого пути
    /// (metadata, pre-scan, real-time). Используется для персистирования в БД.
    /// Вызывается максимум один раз за lifetime pipeline.
    /// Пишется до StartDecoding из command thread; читается из fill thread — volatile для visibility.
    /// </summary>
    private volatile Action<float>? _onGainLocked;

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

        _kWeightFilter = new KWeightingFilter(decoder.SampleRate, decoder.Channels);
        _blockChannelSumSq = new double[decoder.Channels];
        _gatingBlockSizeFrames = (int)(decoder.SampleRate * GatingBlockSeconds);
        _gatingBlockPowers = new double[MaxGatingBlocks];
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

    /// <summary>
    /// Фиксирует gain нормализации навсегда для текущего pipeline.
    /// Единственная точка записи <see cref="_lockedGain"/> — устраняет класс багов
    /// «установил gain, забыл снять pending reset».
    /// </summary>
    /// <remarks>
    /// <para>Атомарно выполняет три действия:</para>
    /// <list type="number">
    ///   <item>Снимает <see cref="_pendingNormReset"/> — fill thread не уничтожит gain.</item>
    ///   <item>Применяет <see cref="_normalizationMode"/>: DownwardOnly cap ≤ 1.0.</item>
    ///   <item>Устанавливает <see cref="_lockedGain"/> и <see cref="_smoothedNormGain"/>.</item>
    /// </list>
    /// <para>Вызывается из трёх путей: <see cref="SetLoudnessMetadata"/>,
    /// <see cref="PreScanNormalizationAsync"/>, <see cref="GetNormalizationGain"/> (real-time).
    /// Callback <see cref="_onGainLocked"/> уведомляет о фиксации для персистирования.</para>
    /// </remarks>
    /// <param name="gain">Raw linear gain до применения режима.</param>
    private void LockNormalizationGain(float gain)
    {
        if (_normalizationMode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, _maxNormalizationGain);

        Interlocked.Exchange(ref _pendingNormReset, 0);
        _lockedGain = gain;
        _smoothedNormGain = gain;

        _onGainLocked?.Invoke(gain);
    }

    /// <summary>
    /// Устанавливает callback для уведомления о фиксации gain нормализации.
    /// Вызывается после создания pipeline, строго до <see cref="StartDecoding"/>.
    /// Callback может быть вызван из fill thread (real-time путь) — должен быть thread-safe.
    /// </summary>
    /// <param name="callback">Callback с зафиксированным linear gain, или null для сброса.</param>
    public void SetGainLockedCallback(Action<float>? callback)
    {
        _onGainLocked = callback;
    }

    /// <summary>
    /// Применяет предзагруженный gain из БД-кеша без анализа.
    /// Вызывается если <see cref="TrackInfo.HasCachedNormalizationGain"/> = true.
    /// Фиксирует gain немедленно — pre-scan и real-time анализ пропускаются.
    /// </summary>
    /// <param name="gain">Linear gain из БД (ранее вычисленный EBU R128 анализом).</param>
    public void SetCachedGain(float gain)
    {
        if (!_normalizationEnabled) return;
        if (gain <= 0f || !float.IsFinite(gain)) return;

        LockNormalizationGain(gain);

        Log.Debug($"[AudioPipeline] Gain restored from DB cache: {gain:F3}x (pre-scan skipped)");
    }

    /// <summary>
    /// Возвращает зафиксированный gain нормализации текущего трека.
    /// Используется AudioPlayer для передачи в следующий pipeline через
    /// <see cref="SetInitialNormalizationGain"/>, устраняя cold-start скачок
    /// при смене трека.
    /// </summary>
    /// <returns>
    /// Locked gain если фаза анализа завершена; <see cref="_startingNormGain"/> если
    /// анализ ещё идёт (лучше передать текущее сглаженное значение чем 1.0f);
    /// 1.0f если нормализация отключена.
    /// </returns>
    public float GetLockedNormalizationGain()
    {
        if (!_normalizationEnabled) return 1.0f;
        return float.IsNaN(_lockedGain) ? _smoothedNormGain : _lockedGain;
    }

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
        _startingNormGain = Math.Clamp(gain, MinNormalizationGain, DefaultMaxNormalizationGain);
        _smoothedNormGain = _startingNormGain;
    }

    /// <summary>
    /// Применяет loudness метаданные YouTube InnerTube API как pre-computed gain нормализации.
    /// Если метаданные валидны — фиксирует <see cref="_lockedGain"/> немедленно через
    /// <see cref="LockNormalizationGain"/>, пропуская pre-scan и real-time EBU R128 анализ.
    /// </summary>
    /// <remarks>
    /// <para><b>Семантика loudnessDb (YouTube InnerTube):</b></para>
    /// <para>Положительное = контент громче −14 LUFS, YouTube понижает.
    /// Отрицательное = тише, LMP усиливает (если режим <see cref="NormalizationMode.Bidirectional"/>).</para>
    /// <para><b>Отличие от YouTube:</b> в режиме Bidirectional (по умолчанию) тихие треки
    /// усиливаются до таргета. В DownwardOnly — поведение идентично YouTube.</para>
    /// <para><b>Порядок вызова:</b> строго после <see cref="SetNormalization"/> и до
    /// <see cref="PreScanNormalizationAsync"/>.</para>
    /// </remarks>
    /// <param name="loudnessDb">Значение поля loudnessDb из InnerTube adaptiveFormats.</param>
    public void SetLoudnessMetadata(float loudnessDb)
    {
        if (!_normalizationEnabled) return;
        if (float.IsNaN(loudnessDb) || !float.IsFinite(loudnessDb)) return;

        float gain = MathF.Pow(10f, -loudnessDb / 20f);

        LockNormalizationGain(gain);

        Log.Debug($"[AudioPipeline] Gain from YouTube metadata: " +
                  $"loudnessDb={loudnessDb:F2}dB → gain={_lockedGain:F3}x (pre-scan skipped)");
    }

    /// <summary>
    /// Выполняет полный pre-scan аудиофайла для вычисления integrated LUFS (EBU R128).
    /// Результат фиксируется через <see cref="LockNormalizationGain"/> — нормализация
    /// корректна с первого сэмпла, fill thread не перезапишет результат.
    /// </summary>
    /// <remarks>
    /// <para><b>Почему pre-scan вместо real-time анализа:</b></para>
    /// <para>Real-time видит только 3-секундный фрагмент. Если это тихое вступление
    /// трека — gain нерепрезентативен. Pre-scan анализирует до 2 минут → стабильный результат.</para>
    ///
    /// <para><b>Оптимизация:</b> использует <see cref="KWeightingFilter.ProcessBlock"/>
    /// для batch K-weighting с bounds elision, затем аккумулирует энергию из результата.
    /// Pre-allocated <see cref="_scanFilteredBuffer"/> исключает аллокации в цикле.</para>
    ///
    /// <para><b>Производительность:</b> 120 сек Opus @ 48kHz ≈ 1.4MB decode + K-weighting ≈ 50-150ms.</para>
    ///
    /// <para><b>Требования:</b></para>
    /// <list type="bullet">
    ///   <item>Source ДОЛЖЕН поддерживать seek (<see cref="IAudioSource.CanSeek"/>)</item>
    ///   <item>Вызывать СТРОГО ДО <see cref="StartDecoding"/></item>
    ///   <item>После scan source seeked обратно, decoder готов к playback</item>
    /// </list>
    ///
    /// <para><b>Fallback:</b> при ошибке gain не фиксируется,
    /// real-time анализ в <see cref="GetNormalizationGain"/> работает как fallback.</para>
    /// </remarks>
    public async Task PreScanNormalizationAsync(CancellationToken ct)
    {
        if (!_normalizationEnabled || !_source.CanSeek)
            return;

        // Gain уже зафиксирован (YouTube metadata или DB cache) — дорогой скан не нужен.
        if (!float.IsNaN(_lockedGain))
        {
            Log.Debug($"[AudioPipeline] Pre-scan skipped: gain already locked={_lockedGain:F3}x");
            return;
        }

        try
        {
            var scanFilter = new KWeightingFilter(_decoder.SampleRate, _decoder.Channels);
            var blockSumSq = new double[_decoder.Channels];
            var blockPowers = new double[MaxScanGatingBlocks];
            int blockCount = 0;
            int blockFrameCount = 0;
            long totalFrames = 0;
            long maxFrames = (long)(_decoder.SampleRate * MaxScanDurationSeconds);
            int channels = _decoder.Channels;

            // Pre-allocated буфер для K-weighted результата (reused across iterations)
            int maxDecodedSamples = DecoderBufferFrames * channels;
            var filteredBuffer = new double[maxDecodedSamples];

            while (!ct.IsCancellationRequested && totalFrames < maxFrames)
            {
                var frame = await _source.ReadFrameAsync(ct);
                if (frame == null) break;

                int decoded = _decoder.Decode(frame.Value.Data.Span, _decodeBuffer);
                if (decoded <= 0) continue;

                int framesToProcess = (int)Math.Min(decoded, maxFrames - totalFrames);
                int samplesToProcess = framesToProcess * channels;

                // Batch K-weighting: bounds elision через Unsafe.Add внутри ProcessBlock
                scanFilter.ProcessBlock(
                    _decodeBuffer.AsSpan(0, samplesToProcess),
                    filteredBuffer.AsSpan(0, samplesToProcess));

                // Energy accumulation из уже отфильтрованных данных
                ref double filteredRef = ref MemoryMarshal.GetArrayDataReference(filteredBuffer);
                ref double sumSqRef = ref MemoryMarshal.GetArrayDataReference(blockSumSq);

                for (int f = 0; f < framesToProcess; f++)
                {
                    int offset = f * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        double val = Unsafe.Add(ref filteredRef, offset + ch);
                        Unsafe.Add(ref sumSqRef, ch) += val * val;
                    }

                    if (++blockFrameCount >= _gatingBlockSizeFrames)
                    {
                        double channelPowerSum = 0.0;
                        for (int ch = 0; ch < channels; ch++)
                            channelPowerSum += Unsafe.Add(ref sumSqRef, ch) / blockFrameCount;

                        double blockLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(channelPowerSum, 1e-20));

                        if (blockLufs > AbsoluteGateThresholdLufs && blockCount < MaxScanGatingBlocks)
                            blockPowers[blockCount++] = channelPowerSum;

                        Array.Clear(blockSumSq, 0, channels);
                        blockFrameCount = 0;
                    }
                }

                totalFrames += framesToProcess;
            }

            float rawGain = ComputeIntegratedGainFromBlocks(
                blockPowers, blockCount, _normalizationTargetLufs, _maxNormalizationGain);

            // Атомарно фиксирует gain и снимает _pendingNormReset.
            LockNormalizationGain(rawGain);

            await _source.SeekAsync(0, ct);

            int skipFrames = _source.Codec == AudioCodec.Opus ? SkipFramesAfterSeekOpus : 0;
            Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
            Volatile.Write(ref _seekTargetMs, -1L);

            double scannedSeconds = totalFrames / (double)_decoder.SampleRate;
            Log.Debug($"[AudioPipeline] Pre-scan complete: gain={_lockedGain:F3}x " +
                      $"(scanned {scannedSeconds:F1}s, blocks={blockCount}, target={_normalizationTargetLufs}LUFS)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Pre-scan failed (fallback to real-time): {ex.Message}");
        }
    }

    /// <summary>
    /// Вычисляет integrated LUFS gain из массива gating block powers (EBU R128).
    /// Статическая логика, переиспользуемая pre-scan и real-time анализом.
    /// </summary>
    /// <param name="blockPowers">Массив channel power sums для блоков, прошедших absolute gate.</param>
    /// <param name="blockCount">Количество валидных блоков.</param>
    /// <param name="targetLufs">Целевой уровень LUFS.</param>
    /// <param name="maxGain">Максимальный допустимый gain.</param>
    /// <returns>Clamped linear gain для нормализации.</returns>
    private static float ComputeIntegratedGainFromBlocks(
        double[] blockPowers, int blockCount, float targetLufs, float maxGain)
    {
        if (blockCount == 0)
            return 1.0f;

        // Pass 1: ungated mean power
        double sumPower = 0.0;
        for (int i = 0; i < blockCount; i++)
            sumPower += blockPowers[i];

        double meanPower = sumPower / blockCount;
        double integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));

        // Pass 2: relative gating (−10 LU)
        double relativeThreshold = integratedLufs + RelativeGateOffsetLu;
        double relPowerThreshold = Math.Pow(10.0, (relativeThreshold - LufsOffset) / 10.0);

        double gatedSum = 0.0;
        int gatedCount = 0;
        for (int i = 0; i < blockCount; i++)
        {
            if (blockPowers[i] >= relPowerThreshold)
            {
                gatedSum += blockPowers[i];
                gatedCount++;
            }
        }

        if (gatedCount > 0)
        {
            meanPower = gatedSum / gatedCount;
            integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));
        }

        float gainDb = (float)(targetLufs - integratedLufs);
        float gain = MathF.Pow(10f, gainDb / 20f);

        return Math.Clamp(gain, MinNormalizationGain, maxGain);
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
    /// <para><b>Алгоритм (EBU R128 / ITU-R BS.1770-4):</b></para>
    /// <list type="bullet">
    ///   <item><b>Фаза анализа (~3 сек):</b> K-weighted LUFS анализ в 400 мс gating blocks.</item>
    ///   <item><b>Фиксация:</b> Relative gating (−10 LU) → финальный integrated LUFS.
    ///     Locked gain сохраняется навсегда для данного pipeline — seek его не сбрасывает.</item>
    ///   <item><b>Остаток трека:</b> Константный locked gain — нет pumping, нет изменений.</item>
    /// </list>
    /// <para>Reset запрашивается через <see cref="_pendingNormReset"/> только в трёх случаях:</para>
    /// <list type="number">
    ///   <item>Нормализация впервые включается.</item>
    ///   <item>Нормализация выключается и включается снова.</item>
    ///   <item>Параметры (targetLufs, maxGain, mode) реально изменились.</item>
    /// </list>
    /// <para>Повторный вызов с теми же параметрами не сбрасывает locked gain.</para>
    /// </remarks>
    public void SetNormalization(
        bool enabled,
        float targetLufs = -14f,
        float maxGain = DefaultMaxNormalizationGain,
        NormalizationMode mode = NormalizationMode.Bidirectional)
    {
        float clampedMaxGain = Math.Max(1f, maxGain);

        if (enabled && !_normalizationEnabled)
        {
            _normalizationTargetLufs = targetLufs;
            _maxNormalizationGain = clampedMaxGain;
            _normalizationMode = mode;
            Interlocked.Exchange(ref _pendingNormReset, 1);
            _normalizationEnabled = true;
            Log.Debug($"[AudioPipeline] Normalization ON (EBU R128): target={targetLufs}LUFS, " +
                      $"maxGain={clampedMaxGain:F1}x, mode={mode}");
        }
        else if (!enabled && _normalizationEnabled)
        {
            _normalizationEnabled = false;
            _lockedGain = float.NaN;
            _smoothedNormGain = 1.0f;
            Log.Debug("[AudioPipeline] Normalization OFF");
        }
        else if (enabled)
        {
            bool paramsChanged =
                MathF.Abs(_normalizationTargetLufs - targetLufs) > 0.01f ||
                MathF.Abs(_maxNormalizationGain - clampedMaxGain) > 0.01f ||
                _normalizationMode != mode;

            _normalizationTargetLufs = targetLufs;
            _maxNormalizationGain = clampedMaxGain;
            _normalizationMode = mode;

            if (paramsChanged)
            {
                Interlocked.Exchange(ref _pendingNormReset, 1);
                Log.Debug($"[AudioPipeline] Normalization params changed: target={targetLufs}LUFS, " +
                          $"maxGain={clampedMaxGain:F1}x, mode={mode}");
            }
        }
    }

    /// <summary>
    /// Запрашивает отложенный сброс состояния нормализации.
    /// Фактический сброс выполняется fill thread'ом в начале <see cref="GetNormalizationGain"/>
    /// через <see cref="_pendingNormReset"/> — исключает data race.
    /// </summary>
    private void RequestNormalizationReset()
    {
        Interlocked.Exchange(ref _pendingNormReset, 1);
    }

    /// <summary>
    /// Фактический сброс всего состояния нормализации.
    /// Вызывается ТОЛЬКО из fill thread — единственный writer для полей нормализации.
    /// </summary>
    private void ExecuteNormalizationReset()
    {
        _lockedGain = float.NaN;
        Array.Clear(_blockChannelSumSq, 0, _blockChannelSumSq.Length);
        _blockFrameCount = 0;
        _gatingBlockCount = 0;
        _normalizationProcessedFrames = 0;
        _kWeightFilter.Reset();
        _smoothedNormGain = _startingNormGain;
    }

    /// <summary>
    /// Сбрасывает всё состояние нормализации для начала новой фазы анализа.
    /// Вызывается при включении, смене параметров или seek.
    /// </summary>
    private void ResetNormalizationState()
    {
        _lockedGain = float.NaN;
        Array.Clear(_blockChannelSumSq, 0, _blockChannelSumSq.Length);
        _blockFrameCount = 0;
        _gatingBlockCount = 0;
        _normalizationProcessedFrames = 0;
        _kWeightFilter.Reset();
    }

    public void PrepareForSeek(long targetMs = -1)
    {
        int skipFrames = _source.Codec == AudioCodec.Opus ? SkipFramesAfterSeekOpus : 0;
        Interlocked.Exchange(ref _skipFramesCounter, skipFrames);
        Volatile.Write(ref _seekTargetMs, targetMs);

        // Сбрасываем анализ только если gain ещё не зафиксирован.
        // Locked gain валиден для всего трека независимо от позиции воспроизведения —
        // перемотка не меняет loudness profile трека.
        if (_normalizationEnabled && float.IsNaN(_lockedGain))
            RequestNormalizationReset();
    }

    public void SetDecodedSamplesPosition(long samples)
    {
        Interlocked.Exchange(ref _decodedSamples, samples);
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
    ///   <item>K-weighted LUFS анализ на RAW сигнале → normGain (без применения)</item>
    ///   <item>combinedGain = normGain × volumeGain с lerp-сглаживанием нормализации</item>
    ///   <item>True Peak Limiter: chunk-level peak scan → safe combined gain без дистории</item>
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

            // 1. Нормализация: K-weighted анализ на RAW сигнале, получаем norm gain
            float normGain = _normalizationEnabled
                ? GetNormalizationGain(samples)
                : 1.0f;

            // 2. Суммарный gain = нормализация × громкость
            float combinedGain = normGain * _gain;

            // 3. True Peak Limiter: применяем суммарный gain без дистории
            ApplyGainWithTruePeak(samples, combinedGain);
        }

        return read / _decoder.Channels;
    }

    /// <summary>
    /// Измеряет K-weighted LUFS текущего chunk и возвращает сглаженный gain нормализации.
    /// НЕ применяет gain к сэмплам — только анализирует raw сигнал.
    /// </summary>
    /// <remarks>
    /// <para><b>Deferred reset:</b> в начале метода проверяется флаг <see cref="_pendingNormReset"/>.
    /// Если установлен — выполняется <see cref="ExecuteNormalizationReset"/> прямо здесь,
    /// в fill thread, исключая data race с command thread.</para>
    ///
    /// <para><b>Sub-block provisional gain:</b> оценка LUFS вычисляется из накопленных
    /// данных текущего неполного блока сразу с первого callback (~50ms).
    /// Это устраняет cold-start скачок вместо ожидания первого полного блока (400ms).</para>
    ///
    /// <para><b>Smoothing:</b> в фазе анализа <see cref="_smoothedNormGain"/> lerp-интерполирует
    /// к provisional gain с коэффициентом ~0.35/chunk (≈5 chunks = 250ms для 98%).
    /// После фиксации locked gain применяется мгновенно — pumping исключён.</para>
    ///
    /// <para><b>Bounds elision:</b> доступ к <see cref="_blockChannelSumSq"/> через
    /// <see cref="Unsafe.Add{T}(ref T, int)"/> исключает bounds check в per-sample цикле.
    /// Коэффициенты K-weighting кэшируются в локальных переменных для register promotion.</para>
    ///
    /// <para><b>Fast path:</b> если gain зафиксирован — мгновенный return без фильтрации
    /// и без lerp.</para>
    /// </remarks>
    /// <returns>Сглаженный norm gain множитель.</returns>
    private float GetNormalizationGain(ReadOnlySpan<float> samples)
    {
        // Deferred reset: выполняется строго из fill thread, нет data race
        if (Interlocked.Exchange(ref _pendingNormReset, 0) == 1)
            ExecuteNormalizationReset();

        // Fast path: gain зафиксирован — основной режим работы (≥3 сек от начала трека)
        if (!float.IsNaN(_lockedGain))
        {
            _smoothedNormGain = _lockedGain;
            return _lockedGain;
        }

        // ──── Фаза анализа: K-weighted LUFS + provisional gain ────

        int channels = _decoder.Channels;
        int frames = samples.Length / channels;

        ref float sampleRef = ref MemoryMarshal.GetReference(samples);
        ref double sumSqRef = ref MemoryMarshal.GetArrayDataReference(_blockChannelSumSq);

        // K-weighting per sample + energy accumulation into current gating block
        for (int f = 0; f < frames; f++)
        {
            int offset = f * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                double filtered = _kWeightFilter.ProcessSample(
                    ch, Unsafe.Add(ref sampleRef, offset + ch));
                Unsafe.Add(ref sumSqRef, ch) += filtered * filtered;
            }

            if (++_blockFrameCount >= _gatingBlockSizeFrames)
                FinalizeGatingBlock();
        }

        _normalizationProcessedFrames += frames;

        // Sub-block provisional: оценка из текущего неполного блока даже если ни один
        // gating block ещё не завершён. Устраняет задержку первой оценки (0→400ms).
        float provisionalGain = ComputeProvisionalGain();

        // Завершение фазы анализа → финальный gain с relative gating
        long analysisFrameThreshold = (long)(_decoder.SampleRate * AnalysisPhaseSeconds);
        if (_normalizationProcessedFrames >= analysisFrameThreshold)
        {
            float rawGain = ComputeIntegratedGain(applyRelativeGating: true);
            _kWeightFilter.Reset();

            LockNormalizationGain(rawGain);

            Log.Debug($"[AudioPipeline] Normalization locked: gain={_lockedGain:F3}x " +
                      $"(analyzed {_normalizationProcessedFrames / (double)_decoder.SampleRate:F1}s, " +
                      $"blocks={_gatingBlockCount}, target={_normalizationTargetLufs}LUFS)");

            return _lockedGain;
        }

        // Lerp-сглаживание к provisional gain в фазе анализа.
        // Коэффициент 0.35: ~5 chunk'ов (250ms @ 50ms/chunk) для достижения 98% цели.
        // Предотвращает слышимые скачки при конвергенции provisional gain между блоками.
        const float LerpFactor = 0.35f;
        _smoothedNormGain += (provisionalGain - _smoothedNormGain) * LerpFactor;

        return _smoothedNormGain;
    }

    /// <summary>
    /// Вычисляет provisional gain включая данные текущего незавершённого gating block.
    /// Позволяет получить оценку уже с первого callback (~50ms) без ожидания
    /// полного блока (400ms).
    /// </summary>
    /// <remarks>
    /// Если завершённых блоков нет и текущий блок пуст — возвращает <see cref="_startingNormGain"/>
    /// (начальный gain, переданный от предыдущего трека) вместо 1.0f.
    /// Это ключевое отличие от <see cref="ComputeIntegratedGain"/> — нет cold-start jump.
    /// </remarks>
    private float ComputeProvisionalGain()
    {
        bool hasCompletedBlocks = _gatingBlockCount > 0;
        bool hasPartialBlock = _blockFrameCount > 0;

        if (!hasCompletedBlocks && !hasPartialBlock)
            return _startingNormGain;

        // Provisional оценка из текущего неполного блока
        if (hasPartialBlock)
        {
            int channels = _decoder.Channels;
            double partialChannelPowerSum = 0.0;
            for (int ch = 0; ch < channels; ch++)
                partialChannelPowerSum += _blockChannelSumSq[ch] / _blockFrameCount;

            double partialLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(partialChannelPowerSum, 1e-20));

            // Объединяем силу завершённых блоков с частичным блоком для взвешенной оценки
            double combinedPower;
            if (hasCompletedBlocks)
            {
                double completedSumPower = 0.0;
                for (int i = 0; i < _gatingBlockCount; i++)
                    completedSumPower += _gatingBlockPowers[i];

                // Вес частичного блока пропорционален его заполненности
                double partialWeight = (double)_blockFrameCount / _gatingBlockSizeFrames;
                combinedPower = (completedSumPower + partialChannelPowerSum * partialWeight)
                                / (_gatingBlockCount + partialWeight);
            }
            else
            {
                // Только частичный блок — используем его напрямую если прошёл absolute gate
                if (partialLufs <= AbsoluteGateThresholdLufs)
                    return _startingNormGain;

                combinedPower = partialChannelPowerSum;
            }

            double provisionalLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(combinedPower, 1e-20));
            float gainDb = (float)(_normalizationTargetLufs - provisionalLufs);
            float gain = MathF.Pow(10f, gainDb / 20f);
            return Math.Clamp(gain, MinNormalizationGain, _maxNormalizationGain);
        }

        // Только завершённые блоки — стандартный путь
        return ComputeIntegratedGain(applyRelativeGating: false);
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

            // Reduce: извлекаем скалярный максимум из SIMD-регистра
            for (int j = 0; j < Vector<float>.Count; j++)
            {
                if (vPeak[j] > peak) peak = vPeak[j];
            }
        }

        // Scalar tail
        for (; i < length; i++)
        {
            float abs = MathF.Abs(Unsafe.Add(ref samplesRef, i));
            if (abs > peak) peak = abs;
        }

        // True Peak Limiting: если gain приведёт к клиппингу — снижаем gain
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

        // Scalar tail
        for (; i < length; i++)
            Unsafe.Add(ref samplesRef, i) *= safeGain;
    }

    /// <summary>
    /// Завершает накопление текущего 400 мс gating block:
    /// вычисляет block loudness, применяет absolute gate (−70 LUFS),
    /// сохраняет block power для дальнейшего integrated LUFS расчёта.
    /// </summary>
    /// <remarks>
    /// <para>Block loudness: L_j = −0.691 + 10 × log₁₀(Σ_ch(meanSquare_ch))</para>
    /// <para>Gᵢ = 1.0 для L/R stereo (ITU-R BS.1770-4, Table 3).
    /// Для 5.1 surround потребовались бы веса 1.41 для Ls/Rs —
    /// стерео-контент из YouTube этого не требует.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinalizeGatingBlock()
    {
        int channels = _decoder.Channels;

        double channelPowerSum = 0.0;
        for (int ch = 0; ch < channels; ch++)
            channelPowerSum += _blockChannelSumSq[ch] / _blockFrameCount; // mean square per channel

        // Block loudness (ITU-R BS.1770-4, eq. 2)
        double blockLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(channelPowerSum, 1e-20));

        // Absolute gating: −70 LUFS (ITU-R BS.1770-4 §3, step 1)
        if (blockLufs > AbsoluteGateThresholdLufs && _gatingBlockCount < MaxGatingBlocks)
            _gatingBlockPowers[_gatingBlockCount++] = channelPowerSum;

        Array.Clear(_blockChannelSumSq, 0, channels);
        _blockFrameCount = 0;
    }

    /// <summary>
    /// Вычисляет integrated LUFS из накопленных real-time gating blocks и возвращает gain множитель.
    /// Делегирует в <see cref="ComputeIntegratedGainFromBlocks"/> для единообразия с pre-scan.
    /// </summary>
    /// <param name="applyRelativeGating">
    /// <c>true</c> — применить relative gate (−10 LU, финальный расчёт при фиксации).
    /// <c>false</c> — только absolute gate (provisional gain во время анализа).
    /// </param>
    /// <returns>Clamp'нутый linear gain для нормализации к <see cref="_normalizationTargetLufs"/>.</returns>
    private float ComputeIntegratedGain(bool applyRelativeGating)
    {
        if (applyRelativeGating)
            return ComputeIntegratedGainFromBlocks(
                _gatingBlockPowers, _gatingBlockCount, _normalizationTargetLufs, _maxNormalizationGain);

        // Provisional: только absolute gating, без relative pass
        if (_gatingBlockCount == 0)
            return 1.0f;

        double sumPower = 0.0;
        for (int i = 0; i < _gatingBlockCount; i++)
            sumPower += _gatingBlockPowers[i];

        double meanPower = sumPower / _gatingBlockCount;
        double integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));

        float gainDb = (float)(_normalizationTargetLufs - integratedLufs);
        float gain = MathF.Pow(10f, gainDb / 20f);

        return Math.Clamp(gain, MinNormalizationGain, _maxNormalizationGain);
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