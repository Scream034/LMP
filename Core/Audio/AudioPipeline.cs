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

    /// <summary>
    /// True Peak Limiter с Attack/Release envelope.
    /// Заменяет stateless chunk-level peak scan, устраняя pumping эффект.
    /// Несёт состояние между chunk'ами — gain reduction рампируется плавно.
    /// </summary>
    private TruePeakLimiter? _truePeakLimiter;

    /// <summary>
    /// Per-sample gain crossfader для плавных переходов gain нормализации и громкости.
    /// Устраняет щелчки при: lock gain, смене настроек, смене трека.
    /// Хранится как поле (value type) для zero-alloc hot path.
    /// </summary>
    private GainCrossfader _gainCrossfader;

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
    /// Признак потери аудиоустройства (BT disconnect, USB unplug и т.д.).
    /// При <c>true</c> pipeline жив (source, decoder config, normalization state сохранены),
    /// но decoder остановлен и backend gate закрыт. Восстановление через
    /// <see cref="RecoverFromDeviceLossAsync"/>.
    /// </summary>
    private volatile bool _deviceLost;

    /// <summary>
    /// Внешний обработчик события потери устройства.
    /// Вызывается из backend fill thread через <see cref="NotifyDeviceLost"/> —
    /// реализация не должна блокировать. Типичное использование:
    /// AudioPlayer переводит state в Paused.
    /// </summary>
    private Action? _onDeviceLostExternal;

    /// <summary>
    /// Внешний обработчик события появления устройства после потери.
    /// Вызывается из timer thread NAudioBackend через <see cref="NotifyDeviceAvailable"/>.
    /// Реализация не должна блокировать — внутри используется <see cref="Task.Run(Action)"/>.
    /// Типичное использование: AudioPlayer инициирует <see cref="DeviceRecoveryCommand"/>.
    /// </summary>
    private Action? _onDeviceAvailableExternal;

    #endregion

    #region Properties

    /// <summary>
    /// Потеряно ли аудиоустройство во время воспроизведения.
    /// <c>true</c> означает что pipeline жив, но decoder остановлен
    /// и backend gate закрыт до вызова <see cref="RecoverFromDeviceLossAsync"/>.
    /// </summary>
    public bool IsDeviceLost => _deviceLost;

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
        _truePeakLimiter = new TruePeakLimiter(decoder.SampleRate);
        _gainCrossfader = new GainCrossfader(1.0f);
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

            bool deviceUnavailable = false;

            try
            {
                sharedBackend.Reinitialize(decoder.SampleRate, decoder.Channels, pipeline.AudioCallback);
            }
            catch (AudioDeviceException ex)
            {
                // Backend недоступен, но source + decoder + ring buffer полностью рабочие.
                // Pipeline создаётся в degraded mode: IsDeviceLost = true.
                // AudioPlayer переведёт state в Paused + покажет info toast.
                // Device watcher в NAudioBackend уведомит о появлении устройства → auto-recovery.
                deviceUnavailable = true;
                pipeline._deviceLost = true;
                Log.Warn($"[AudioPipeline] Created in degraded mode (no audio device): {ex.Message}");
            }

            sharedBackend.SetDeviceLostCallback(pipeline.NotifyDeviceLost);
            sharedBackend.SetStarvationCallback(pipeline.NotifyStarvation);
            sharedBackend.SetDeviceAvailableCallback(pipeline.NotifyDeviceAvailable);

            Log.Info($"[AudioPipeline] Created (shared backend){(deviceUnavailable ? " [NO DEVICE]" : "")}: " +
                     $"{streamInfo.FormatDisplay}");

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
    /// </summary>
    /// <remarks>
    /// <para><b>Soft pause:</b> Pipeline остаётся живым — source, decoder config,
    /// normalization state, позиция трека сохраняются. Lifetime CTS НЕ отменяется:
    /// <see cref="WatchPipelineLifetimeAsync"/> в AudioPlayer не получает сигнала,
    /// pipeline не уничтожается.</para>
    /// <para><b>Decoder:</b> Останавливается через <see cref="_decoderCts"/>, чтобы
    /// предотвратить busy-wait заполнение ring buffer при закрытом backend gate.</para>
    /// <para><b>Идемпотентность:</b> Повторный вызов при <c>_deviceLost=true</c>
    /// игнорируется — защита от двойного срабатывания health check.</para>
    /// <para><b>Recovery:</b> AudioPlayer отправляет <see cref="DeviceRecoveryCommand"/>
    /// при следующем Resume, который вызывает <see cref="RecoverFromDeviceLossAsync"/>.</para>
    /// </remarks>
    internal void NotifyDeviceLost()
    {
        if (_disposed || _deviceLost) return;
        _deviceLost = true;

        Log.Error("[AudioPipeline] Audio device lost — soft pause (pipeline alive)");

        // Decoder необходимо остановить: backend gate закрыт (CheckDeviceHealth),
        // ring buffer перестаёт потребляться. Без остановки decoder заполнит буфер
        // и зависнет в busy-wait цикле (Task.Yield / Task.Delay(5ms)).
        // Lifetime CTS НЕ отменяется — pipeline должен пережить disconnect.
        try { _decoderCts?.Cancel(); } catch (ObjectDisposedException) { }

        // External callback вызывается ПОСЛЕ отмены decoder —
        // AudioPlayer может безопасно менять state без гонки с decoder loop.
        var handler = _onDeviceLostExternal;
        if (handler != null)
            Task.Run(handler);
    }

    /// <summary>
    /// Регистрирует внешний обработчик потери устройства.
    /// </summary>
    /// <param name="handler">
    /// Callback, вызываемый из fill thread при детекции device loss.
    /// Реализация не должна блокировать — внутри используется <see cref="Task.Run(Action)"/>.
    /// </param>
    internal void SetDeviceLostHandler(Action handler) => _onDeviceLostExternal = handler;

    /// <summary>
    /// Восстанавливает pipeline после потери аудиоустройства.
    /// </summary>
    /// <remarks>
    /// <para><b>Последовательность:</b></para>
    /// <list type="number">
    ///   <item>Ожидание завершения cancelled decoder task (graceful shutdown)</item>
    ///   <item>Flush ring buffer и backend provider (удаление stale PCM)</item>
    ///   <item>Reinitialize backend (пересоздание WaveOut с BT retry loop)</item>
    ///   <item>Регистрация device callbacks на новом WaveOut</item>
    ///   <item>Перезапуск decoder с текущей позиции source</item>
    /// </list>
    /// <para><b>Позиция:</b> Source сохраняет byte position. Decoder стартует
    /// с текущей позиции — потеря аудио ≤ <see cref="AudioConstants.BufferSizeSeconds"/>
    /// секунд (объём ring buffer на момент disconnect). Для BT reconnect (1–30с)
    /// это приемлемый компромисс.</para>
    /// </remarks>
    /// <param name="urlRefresher">Делегат обновления истёкшего URL.</param>
    /// <param name="options">Настройки плеера.</param>
    /// <param name="onTrackEnded">Callback естественного завершения трека.</param>
    /// <param name="onError">Callback фатальной ошибки декодера.</param>
    /// <param name="ct">Токен отмены (lifetime плеера).</param>
    internal async Task RecoverFromDeviceLossAsync(
        Func<CancellationToken, Task<string?>>? urlRefresher,
        AudioPlayerOptions options,
        Action? onTrackEnded,
        Action<Exception>? onError,
        CancellationToken ct)
    {
        if (_disposed || !_deviceLost) return;

        // Дожидаемся graceful shutdown decoder task.
        // NotifyDeviceLost уже отменил _decoderCts, но task может быть
        // в середине async ReadFrameAsync — нужно дать ему завершиться.
        await StopDecodingAsync(TimeSpan.FromMilliseconds(DecoderStopTimeoutMs))
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        // Stale PCM в ring buffer декодирован, но не воспроизведён.
        // Provider может содержать частично воспроизведённый chunk.
        // Оба буфера очищаются для бесшовного возобновления без артефактов.
        _backend.Flush();
        _pcmBuffer.Clear();

        // Reinitialize пересоздаёт WaveOut если _deviceLost=true на стороне backend.
        // NAudioBackend.Initialize содержит BT retry loop с линейным backoff.
        _backend.Reinitialize(SampleRate, Channels, AudioCallback);

        _backend.SetDeviceLostCallback(NotifyDeviceLost);
        _backend.SetStarvationCallback(NotifyStarvation);
        _backend.SetDeviceAvailableCallback(NotifyDeviceAvailable);

        ct.ThrowIfCancellationRequested();

        _deviceLost = false;

        // Decoder перезапускается с текущей byte-позиции source.
        // Source не сбрасывался — позиция сохранена.
        StartDecoding(urlRefresher, options, onTrackEnded, onError);

        Log.Info("[AudioPipeline] Recovered from device loss");
    }

    /// <summary>
    /// Вызывается когда аудиоустройство снова доступно после потери
    /// (callback от device watcher в <see cref="NAudioBackend"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Idempotent:</b> Если <see cref="_deviceLost"/> = false
    /// (уже восстановлен вручную) — callback не вызывается.</para>
    /// <para><b>Thread safety:</b> Вызывается из timer thread NAudioBackend.
    /// <see cref="Task.Run(Action)"/> изолирует вызывающий поток от обработчика.</para>
    /// </remarks>
    internal void NotifyDeviceAvailable()
    {
        if (_disposed || !_deviceLost) return;

        Log.Info("[AudioPipeline] Audio device available — notifying player for auto-recovery");

        var handler = _onDeviceAvailableExternal;
        if (handler != null)
            Task.Run(handler);
    }

    /// <summary>
    /// Регистрирует внешний обработчик появления устройства после потери.
    /// </summary>
    /// <param name="handler">
    /// Callback, вызываемый из timer thread при обнаружении устройства.
    /// Реализация не должна блокировать — внутри используется <see cref="Task.Run(Action)"/>.
    /// </param>
    internal void SetDeviceAvailableHandler(Action handler) => _onDeviceAvailableExternal = handler;

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
        // ИСПРАВЛЕНИЕ: Удален GCSettings.LatencyMode = SustainedLowLatency.
        // Он вызывал переполнение кучи при тяжелой расшифровке AST Solver'ом, 
        // что приводило к Stop-The-World блокировке (фризу UI-потока).

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
    /// Подготавливает pipeline к seek-операции.
    /// Сбрасывает limiter и crossfader на текущий normGain для исключения
    /// артефактов из предыдущей позиции.
    /// </summary>
    /// <remarks>
    /// <para><b>Почему normGain без volumeGain:</b> После рефакторинга
    /// volume gain живёт в <see cref="GainWaveProvider"/> на стороне backend.
    /// <see cref="GainCrossfader"/> отвечает только за normalization gain transitions.
    /// Crossfader сбрасывается на <see cref="EbuR128Analyzer.GetLockedGain"/>
    /// — то значение, которое будет применяться к следующим сэмплам после seek.</para>
    /// </remarks>
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

    /// <summary>
    /// Выполняет pre-scan через <see cref="EbuR128Analyzer.PreScanAsync"/>.
    /// Фиксирует gain и возвращает source в начало для playback.
    /// </summary>
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
            float rawGain = await _analyzer.PreScanAsync(_source, _decoder, _decodeBuffer, ct)
                .ConfigureAwait(false);

            _analyzer.LockGain(rawGain);

            await _source.SeekAsync(0, ct).ConfigureAwait(false);

            //  Аналогично PrepareForSeek — учитываем кодек
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
        catch (Exception ex)
        {
            Log.Warn($"[AudioPipeline] Pre-scan failed (fallback to real-time): {ex.Message}");
        }
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

    /// <summary>
    /// Синхронизирует начальное состояние <see cref="_gainCrossfader"/> с normGain
    /// до первого вызова <see cref="AudioCallback"/>.
    /// </summary>
    /// <remarks>
    /// <para>Снапируем только на normGain (без volume) — volume теперь
    /// в GainWaveProvider и не нуждается в crossfader snap.</para>
    /// </remarks>
    public void SnapCrossfaderToGain()
    {
        if (_disposed) return;

        float normGain = _analyzer.IsEnabled ? _analyzer.GetLockedGain() : 1.0f;
        _gainCrossfader.Reset(normGain);
    }

    #endregion

    #region Audio Callback

    /// <summary>
    /// Callback вызываемый из fill thread backend'а для заполнения аудио-буфера.
    /// </summary>
    /// <remarks>
    /// <para><b>Порядок обработки:</b></para>
    /// <list type="number">
    ///   <item>Чтение PCM из ring buffer.</item>
    ///   <item>K-weighted LUFS анализ на RAW сигнале → normGain (без модификации).</item>
    ///   <item>GainCrossfader.SetTarget(normGain) — обновить цель interpolation.</item>
    ///   <item>TruePeakLimiter.Process — per-sample: normGain apply + peak limit.</item>
    /// </list>
    /// <para><b>Volume gain НЕ применяется здесь.</b> Он применяется в
    /// <see cref="GainWaveProvider.Read"/> — при чтении WaveOut из provider.
    /// Это устраняет задержку отклика громкости с 500ms до ≤100ms.</para>
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

            // Только normalization gain — без volume gain.
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

        _decoder.Dispose();
        await _source.DisposeAsync();

        ArrayPool<float>.Shared.Return(_decodeBuffer);

        try { _decoderCts?.Dispose(); } catch (ObjectDisposedException) { }
        try { _lifetimeCts.Dispose(); } catch (ObjectDisposedException) { }

        Log.Debug("[AudioPipeline] Disposed");
    }

    #endregion
}