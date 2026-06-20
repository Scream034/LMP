using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LMP.Core.Audio.Helpers;
using LMP.Core.Audio.Interfaces;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// EBU R128 / ITU-R BS.1770-4 анализатор громкости с двухфазной нормализацией.
///
/// <para><b>Алгоритм (аналогичен Spotify / YouTube Music):</b></para>
/// <list type="bullet">
///   <item><b>Фаза анализа (~3 сек):</b> K-weighted LUFS измерение в 400 мс gating blocks.
///     Provisional gain конвергирует с каждым блоком.</item>
///   <item><b>Фиксация:</b> Relative gating (−10 LU) → финальный integrated LUFS.
///     Gain замораживается навсегда.</item>
///   <item><b>Остаток трека:</b> Константный locked gain — нет pumping, нет изменений.</item>
/// </list>
///
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item><see cref="ProcessSamples"/> — вызывается из fill thread NAudioBackend (hot path)</item>
///   <item><see cref="Configure"/>, <see cref="LockFromCache"/>, <see cref="LockFromMetadata"/> —
///     вызываются из command thread</item>
///   <item><see cref="_pendingNormReset"/> — volatile int, lock-free sync между потоками</item>
///   <item>Все поля анализа — single writer (fill thread), нет гонок</item>
/// </list>
///
/// <para><b>Pre-scan:</b> <see cref="PreScanAsync"/> использует отдельный временный DSP-стек,
/// не затрагивая real-time состояние. Результат фиксируется через <see cref="LockGain"/>.</para>
/// </summary>
public sealed class EbuR128Analyzer
{
    #region Constants (EBU R128 / ITU-R BS.1770-4)

    /// <summary>Длительность фазы real-time анализа в секундах.</summary>
    private const float AnalysisPhaseSeconds = 3.0f;

    /// <summary>Минимальный gain нормализации (защита от чрезмерного подавления).</summary>
    private const float MinNormalizationGain = 0.1f;

    /// <summary>Максимальный gain нормализации по умолчанию.</summary>
    private const float DefaultMaxNormalizationGain = 3.0f;

    /// <summary>Длительность gating block (EBU R128: 400 мс).</summary>
    private const double GatingBlockSeconds = 0.4;

    /// <summary>Абсолютный порог гейтинга: −70 LUFS (ITU-R BS.1770-4 §3).</summary>
    private const double AbsoluteGateThresholdLufs = -70.0;

    /// <summary>Относительный порог гейтинга: −10 LU (ITU-R BS.1770-4 §3).</summary>
    private const double RelativeGateOffsetLu = -10.0;

    /// <summary>Константа из ITU-R BS.1770-4 уравнения (2): −0.691 dBFS.</summary>
    private const double LufsOffset = -0.691;

    /// <summary>Макс. количество gating blocks для real-time анализа.</summary>
    private const int MaxGatingBlocks = 64;

    /// <summary>Макс. количество gating blocks для pre-scan.</summary>
    private const int MaxScanGatingBlocks = 512;

    /// <summary>Макс. длительность pre-scan (секунд).</summary>
    private const float MaxScanDurationSeconds = 120f;

    #endregion

    #region Immutable State

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _gatingBlockSizeFrames;

    #endregion

    #region Analysis State (fill thread only — single writer)

    private readonly KWeightingFilter _kWeightFilter;
    private readonly double[] _blockChannelSumSq;
    private readonly double[] _gatingBlockPowers;
    private int _blockFrameCount;
    private int _gatingBlockCount;
    private long _normalizationProcessedFrames;

    #endregion

    #region Gain State

    /// <summary>
    /// Зафиксированный gain. NaN = фаза анализа.
    /// Единственная точка записи: <see cref="LockGain"/>.
    /// </summary>
    private float _lockedGain = float.NaN;

    /// <summary>Сглаженный gain, применяемый к PCM (fill thread only).</summary>
    private float _smoothedNormGain = 1.0f;

    /// <summary>Начальный gain от предыдущего трека (устраняет cold-start скачок).</summary>
    private float _startingNormGain = 1.0f;

    /// <summary>
    /// Сигнал отложенного сброса. 1 = сброс запрошен, 0 = нет.
    /// Устанавливается из command thread, исполняется fill thread'ом.
    /// </summary>
    private volatile int _pendingNormReset;

    /// <summary>
    /// Callback фиксации gain. Вызывается максимум один раз за pipeline.
    /// Пишется до StartDecoding; читается из fill thread — volatile для visibility.
    /// </summary>
    private volatile Action<float>? _onGainLocked;

#if DEBUG
    /// <summary>
    /// Последний залогированный gain из кэша. Используется для дедупликации
    /// повторных вызовов <see cref="LockFromCachedGain"/> с тем же значением
    /// (возникает при повторных срабатываниях UI-биндингов в Settings).
    /// <para><c>float.NaN</c> = ещё не логировался (начальное состояние или после reset).</para>
    /// </summary>
    private float _lastLoggedCacheGain = float.NaN;

    /// <summary>
    /// Последний залогированный mode при вызове <see cref="LockFromCachedGain"/>.
    /// Вместе с <see cref="_lastLoggedCacheGain"/> образует ключ дедупликации.
    /// </summary>
    private NormalizationMode _lastLoggedCacheMode;
#endif

    #endregion

    #region Configuration

    private volatile bool _enabled;
    private float _targetLufs = -14f;
    private float _maxGain = DefaultMaxNormalizationGain;
    private NormalizationMode _mode = NormalizationMode.Bidirectional;

    /// <summary>
    /// Reference-обёртка для атомарной публикации <see cref="NormalizationConfig"/>
    /// через volatile поле. Необходима потому что <c>volatile</c> применим только
    /// к reference types и примитивам ≤ sizeof(nint), а <see cref="NormalizationConfig"/>
    /// — value type из 4 полей (16 байт).
    /// <para>Аллокация происходит только при смене настроек пользователем (редко).</para>
    /// </summary>
    private sealed record ConfigSnapshot(NormalizationConfig Value);

    /// <summary>
    /// Pending конфиг для атомарной публикации из command thread.
    /// <c>null</c> = нет ожидающего обновления. Читается из fill thread
    /// в начале <see cref="ProcessSamples"/> через volatile read.
    /// </summary>
    private volatile ConfigSnapshot? _pendingConfig;

    #endregion

    #region Public Properties

    /// <summary>Включена ли нормализация.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Зафиксирован ли gain (фаза анализа завершена).</summary>
    public bool IsGainLocked => !float.IsNaN(_lockedGain);

    /// <summary>Текущая конфигурация (snapshot).</summary>
    public NormalizationConfig CurrentConfig => new(_enabled, _targetLufs, _maxGain, _mode);

    #endregion

    #region Constructor

    public EbuR128Analyzer(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _gatingBlockSizeFrames = (int)(sampleRate * GatingBlockSeconds);

        _kWeightFilter = new KWeightingFilter(sampleRate, channels);
        _blockChannelSumSq = new double[channels];
        _gatingBlockPowers = new double[MaxGatingBlocks];
    }

    #endregion

    #region Configuration API

    /// <summary>
    /// Применяет конфигурацию нормализации атомарно.
    /// </summary>
    /// <remarks>
    /// <para><b>Thread safety:</b> Публикует новый конфиг через <c>volatile</c> write
    /// (<see cref="_pendingConfig"/>). Fill thread читает его в начале
    /// следующего <see cref="ProcessSamples"/> вызова через <c>volatile</c> read.
    /// Это гарантирует что fill thread всегда видит целостную конфигурацию,
    /// а не частично обновлённый набор полей.</para>
    ///
    /// <para><b>Recalculate vs Reset:</b> При изменении targetLufs или mode
    /// накопленные gating blocks СОХРАНЯЮТСЯ и пересчитываются с новыми параметрами
    /// вместо полного сброса. Это исключает 3-секундный период блуждающего
    /// provisional gain после смены настроек.</para>
    /// </remarks>
    public void Configure(NormalizationConfig config)
    {
        float clampedMaxGain = Math.Max(1f, config.MaxGain);
        var normalizedConfig = config with { MaxGain = clampedMaxGain };

        if (!config.Enabled && _enabled)
        {
            _enabled = false;
            _lockedGain = float.NaN;
            _smoothedNormGain = 1.0f;
            Log.Debug("[EbuR128] Normalization OFF");
            return;
        }

        if (!config.Enabled) return;

        bool wasEnabled = _enabled;
        bool changed = !wasEnabled
            || MathF.Abs(_targetLufs - normalizedConfig.TargetLufs) > 0.01f
            || MathF.Abs(_maxGain - normalizedConfig.MaxGain) > 0.01f
            || _mode != normalizedConfig.Mode;

        if (!changed) return;

        bool needsReset = !wasEnabled; // Только при включении с нуля — полный reset
        bool needsRecalc = wasEnabled && changed; // При изменении параметров — пересчёт

        // Атомарная публикация конфига: fill thread читает после fence,
        // всегда видит согласованное состояние.
        _pendingConfig = new ConfigSnapshot(normalizedConfig);
        _enabled = true;

        if (needsReset)
        {
            Interlocked.Exchange(ref _pendingNormReset, 1);
            Log.Debug($"[EbuR128] Normalization ON: target={normalizedConfig.TargetLufs}LUFS, " +
                      $"maxGain={normalizedConfig.MaxGain:F1}x, mode={normalizedConfig.Mode}");
        }
        else if (needsRecalc)
        {
            Interlocked.Exchange(ref _pendingNormReset, 2); // 2 = recalc, не полный reset
            Log.Debug($"[EbuR128] Params changed (recalc): target={normalizedConfig.TargetLufs}LUFS, " +
                      $"maxGain={normalizedConfig.MaxGain:F1}x, mode={normalizedConfig.Mode}");
        }
    }

    /// <summary>
    /// Применяет конфигурацию из <see cref="_pendingConfig"/> и обновляет локальные поля.
    /// Вызывается строго из fill thread в начале <see cref="ProcessSamples"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyPendingConfig()
    {
        var snapshot = _pendingConfig;
        if (snapshot == null) return;

        var cfg = snapshot.Value;
        _targetLufs = cfg.TargetLufs;
        _maxGain = Math.Max(1f, cfg.MaxGain);
        _mode = cfg.Mode;
    }

    /// <summary>Устанавливает callback фиксации gain (для персистирования в БД).</summary>
    public void SetGainLockedCallback(Action<float>? callback) => _onGainLocked = callback;

    /// <summary>
    /// Устанавливает начальный gain от предыдущего трека.
    /// Устраняет cold-start скачок (первый callback использует этот gain
    /// вместо 1.0f до завершения первого gating block).
    /// </summary>
    public void SetInitialGain(float gain)
    {
        _startingNormGain = Math.Clamp(gain, MinNormalizationGain, DefaultMaxNormalizationGain);
        _smoothedNormGain = _startingNormGain;
    }

    /// <summary>Запрашивает сброс анализа при seek (если gain ещё не зафиксирован).</summary>
    public void PrepareForSeek()
    {
        if (_enabled && float.IsNaN(_lockedGain))
            Interlocked.Exchange(ref _pendingNormReset, 1);
    }

    #endregion

    #region Gain Locking

    /// <summary>
    /// Применяет предзагруженный gain без вызова <see cref="_onGainLocked"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Исправление stale _mode race:</b> <see cref="_mode"/> обновляется
    /// лениво fill thread'ом через <see cref="ApplyPendingConfig"/>.
    /// При вызове из command thread сразу после <see cref="Configure"/>
    /// <see cref="_mode"/> содержит старое значение.
    /// Метод читает mode из <see cref="_pendingConfig"/> если он есть —
    /// это гарантирует применение <b>нового</b> mode к gain constraint.</para>
    ///
    /// <para><b>Лог-дедупликация:</b> Повторные вызовы с тем же gain/mode
    /// (типично при UI-биндингах Settings слайдеров) не порождают лог-строк.
    /// Dedup сбрасывается в <see cref="ExecuteReset"/> при смене трека.</para>
    /// </remarks>
    /// <param name="gain">Linear gain из кэша или из <see cref="NormalizationGainResolver"/>.</param>
    public void LockFromCachedGain(float gain)
    {
        if (!_enabled) return;
        if (gain <= 0f || !float.IsFinite(gain)) return;

        // Читаем mode из pending конфига если он есть (свежее обновление от command thread),
        // иначе используем текущий _mode (уже синхронизирован fill thread'ом).
        var pendingSnapshot = _pendingConfig;
        var effectiveMode = pendingSnapshot?.Value.Mode ?? _mode;
        var effectiveMaxGain = pendingSnapshot != null
            ? Math.Max(1f, pendingSnapshot.Value.MaxGain)
            : _maxGain;

        if (effectiveMode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, effectiveMaxGain);

        // Idempotent state writes — безопасны при повторных вызовах
        Interlocked.Exchange(ref _pendingNormReset, 0);
        _lockedGain = gain;
        _smoothedNormGain = gain;

#if DEBUG
        // Лог-дедупликация: пишем только при фактическом изменении gain или mode.
        // Устраняет ~100 строк спама при UI-манипуляциях в Settings.
        if (MathF.Abs(gain - _lastLoggedCacheGain) > 0.0005f || effectiveMode != _lastLoggedCacheMode)
        {
            _lastLoggedCacheGain = gain;
            _lastLoggedCacheMode = effectiveMode;
            Log.Debug($"[EbuR128] Gain from cache: {gain:F3}x (mode={effectiveMode}, analysis skipped)");
        }
#endif
    }

    /// <summary>
    /// Фиксирует gain из реального EBU R128 анализа.
    /// </summary>
    /// <remarks>
    /// <para>Режим DownwardOnly применяется ДО записи в <see cref="_lockedGain"/>,
    /// что согласовано с <see cref="ComputeProvisionalGain"/> и
    /// <see cref="ComputeIntegratedGainFromBlocks"/> — не будет скачка при переходе
    /// из provisional в locked фазу.</para>
    /// </remarks>
    public void LockGain(float gain)
    {
        if (_mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, _maxGain);

        Interlocked.Exchange(ref _pendingNormReset, 0);
        _lockedGain = gain;
        // Намеренно НЕ обновляем _smoothedNormGain здесь.
        // GainCrossfader в AudioPipeline плавно перейдёт к новому значению.

        _onGainLocked?.Invoke(gain);
    }

    /// <summary>
    /// Возвращает зафиксированный или текущий сглаженный gain.
    /// </summary>
    public float GetLockedGain()
    {
        if (!_enabled) return 1.0f;
        return float.IsNaN(_lockedGain) ? _smoothedNormGain : _lockedGain;
    }

    #endregion

    #region Hot Path: Real-Time Analysis

    /// <summary>
    /// Измеряет K-weighted LUFS и возвращает целевой gain нормализации.
    /// НЕ модифицирует сэмплы — только анализирует raw сигнал.
    /// </summary>
    /// <remarks>
    /// <para><b>ВЫЗЫВАТЬ ТОЛЬКО ИЗ FILL THREAD.</b></para>
    /// <para><b>Zero-alloc.</b></para>
    /// <para><b>Fast path:</b> gain зафиксирован → мгновенный return без фильтрации.</para>
    /// <para><b>Deferred ops:</b> В начале каждого вызова обрабатываются отложенные
    /// операции из command thread: применение конфига, reset или recalculate.
    /// Этот паттерн гарантирует что все мутации состояния происходят
    /// строго из fill thread — нет concurrent writes.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ProcessSamples(ReadOnlySpan<float> samples)
    {
        // Deferred config apply: атомарно читаем конфиг из command thread
        ApplyPendingConfig();

        // Deferred reset/recalc: исполняется строго из fill thread
        int pendingOp = Interlocked.Exchange(ref _pendingNormReset, 0);
        if (pendingOp == 1)
            ExecuteReset();
        else if (pendingOp == 2)
            ExecuteRecalculate();

        // Fast path: gain зафиксирован — основной режим (≥3 сек от начала трека)
        if (!float.IsNaN(_lockedGain))
            return _lockedGain;

        //  Фаза анализа: K-weighted LUFS + provisional gain 

        int channels = _channels;
        int frames = samples.Length / channels;

        ref float sampleRef = ref MemoryMarshal.GetReference(samples);
        ref double sumSqRef = ref MemoryMarshal.GetArrayDataReference(_blockChannelSumSq);

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

        float provisionalGain = ComputeProvisionalGain();

        // Завершение фазы → финальный gain с relative gating
        long analysisThreshold = (long)(_sampleRate * AnalysisPhaseSeconds);
        if (_normalizationProcessedFrames >= analysisThreshold)
        {
            float rawGain = ComputeIntegratedGainFromBlocks(
                _gatingBlockPowers, _gatingBlockCount, _targetLufs, _maxGain);
            _kWeightFilter.Reset();
            LockGain(rawGain);

            Log.Debug($"[EbuR128] Real-time locked: gain={_lockedGain:F3}x " +
                      $"(analyzed {_normalizationProcessedFrames / (double)_sampleRate:F1}s, " +
                      $"blocks={_gatingBlockCount}, mode={_mode})");
            return _lockedGain;
        }

        // Lerp убран: сглаживание делегировано GainCrossfader в AudioPipeline
        // (per-sample вместо per-chunk). Возвращаем raw provisional gain.
        return provisionalGain;
    }

    #endregion

    #region Pre-Scan (offline EBU R128 analysis)

    /// <summary>
    /// Полный pre-scan аудиофайла для вычисления integrated LUFS.
    /// Использует отдельный временный DSP-стек — не затрагивает real-time состояние.
    /// </summary>
    /// <remarks>
    /// <para><b>Производительность:</b> 120 сек Opus @ 48kHz ≈ 50-150ms.</para>
    /// <para><b>Результат:</b> raw gain (НЕ locked). Вызывающий код решает
    /// когда зафиксировать через <see cref="LockGain"/>.</para>
    /// </remarks>
    /// <returns>Raw linear gain, или <c>float.NaN</c> если scan не удался.</returns>
    public async Task<float> PreScanAsync(
        IAudioSource source,
        IAudioDecoder decoder,
        float[] decodeBuffer,
        CancellationToken ct)
    {
        var scanFilter = new KWeightingFilter(_sampleRate, _channels);
        var blockSumSq = new double[_channels];
        var blockPowers = new double[MaxScanGatingBlocks];
        int blockCount = 0;
        int blockFrameCount = 0;
        long totalFrames = 0;
        long maxFrames = (long)(_sampleRate * MaxScanDurationSeconds);
        int channels = _channels;

        int maxDecodedSamples = DecoderBufferFrames * channels;
        var filteredBuffer = new double[maxDecodedSamples];

        while (!ct.IsCancellationRequested && totalFrames < maxFrames)
        {
            var frame = await source.ReadFrameAsync(ct);
            if (frame == null) break;

            int decoded = decoder.Decode(frame.Value.Data.Span, decodeBuffer);
            if (decoded <= 0) continue;

            int framesToProcess = (int)Math.Min(decoded, maxFrames - totalFrames);
            int samplesToProcess = framesToProcess * channels;

            scanFilter.ProcessBlock(
                decodeBuffer.AsSpan(0, samplesToProcess),
                filteredBuffer.AsSpan(0, samplesToProcess));

            ref double filteredRef = ref MemoryMarshal.GetArrayDataReference(filteredBuffer);
            ref double sumSqRefLocal = ref MemoryMarshal.GetArrayDataReference(blockSumSq);

            for (int f = 0; f < framesToProcess; f++)
            {
                int offset = f * channels;
                for (int ch = 0; ch < channels; ch++)
                {
                    double val = Unsafe.Add(ref filteredRef, offset + ch);
                    Unsafe.Add(ref sumSqRefLocal, ch) += val * val;
                }

                if (++blockFrameCount >= _gatingBlockSizeFrames)
                {
                    double channelPowerSum = 0.0;
                    for (int ch = 0; ch < channels; ch++)
                        channelPowerSum += Unsafe.Add(ref sumSqRefLocal, ch) / blockFrameCount;

                    double blockLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(channelPowerSum, 1e-20));

                    if (blockLufs > AbsoluteGateThresholdLufs && blockCount < MaxScanGatingBlocks)
                        blockPowers[blockCount++] = channelPowerSum;

                    Array.Clear(blockSumSq, 0, channels);
                    blockFrameCount = 0;
                }
            }

            totalFrames += framesToProcess;
        }

        float rawGain = ComputeIntegratedGainFromBlocks(blockPowers, blockCount, _targetLufs, _maxGain);

#if DEBUG
        double scannedSeconds = totalFrames / (double)_sampleRate;
        Log.Debug($"[EbuR128] Pre-scan: gain={rawGain:F3}x " +
                  $"(scanned {scannedSeconds:F1}s, blocks={blockCount}, target={_targetLufs}LUFS)");
#endif

        return rawGain;
    }

    #endregion

    #region Internal DSP

    /// <summary>Завершает 400 мс gating block, применяет absolute gate (−70 LUFS).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinalizeGatingBlock()
    {
        int channels = _channels;
        double channelPowerSum = 0.0;
        for (int ch = 0; ch < channels; ch++)
            channelPowerSum += _blockChannelSumSq[ch] / _blockFrameCount;

        double blockLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(channelPowerSum, 1e-20));

        if (blockLufs > AbsoluteGateThresholdLufs && _gatingBlockCount < MaxGatingBlocks)
            _gatingBlockPowers[_gatingBlockCount++] = channelPowerSum;

        Array.Clear(_blockChannelSumSq, 0, channels);
        _blockFrameCount = 0;
    }

    /// <summary>
    /// Provisional gain с DownwardOnly constraint (для real-time playback).
    /// </summary>
    private float ComputeProvisionalGain()
    {
        bool hasCompleted = _gatingBlockCount > 0;
        bool hasPartial = _blockFrameCount > 0;

        if (!hasCompleted && !hasPartial)
            return _startingNormGain;

        float rawGain;

        if (hasPartial)
        {
            int channels = _channels;
            double partialPowerSum = 0.0;
            for (int ch = 0; ch < channels; ch++)
                partialPowerSum += _blockChannelSumSq[ch] / _blockFrameCount;

            double partialLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(partialPowerSum, 1e-20));

            double combinedPower;
            if (hasCompleted)
            {
                double completedSum = 0.0;
                for (int i = 0; i < _gatingBlockCount; i++)
                    completedSum += _gatingBlockPowers[i];

                double partialWeight = (double)_blockFrameCount / _gatingBlockSizeFrames;
                combinedPower = (completedSum + partialPowerSum * partialWeight)
                                / (_gatingBlockCount + partialWeight);
            }
            else
            {
                if (partialLufs <= AbsoluteGateThresholdLufs)
                    return _startingNormGain;
                combinedPower = partialPowerSum;
            }

            double provisionalLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(combinedPower, 1e-20));
            float gainDb = (float)(_targetLufs - provisionalLufs);
            float gain = MathF.Pow(10f, gainDb / 20f);
            rawGain = Math.Clamp(gain, MinNormalizationGain, _maxGain);
        }
        else
        {
            rawGain = ComputeIntegratedGainFromBlocks(
                _gatingBlockPowers, _gatingBlockCount, _targetLufs, _maxGain);
        }

        // Mode constraint — только для real-time output, не для persist.
        if (_mode == NormalizationMode.DownwardOnly)
            rawGain = MathF.Min(rawGain, 1.0f);

        return rawGain;
    }

    /// <summary>
    /// Пересчитывает gain из существующих gating blocks с новыми параметрами
    /// (targetLufs, mode) без потери накопленных данных анализа.
    /// </summary>
    /// <remarks>
    /// <para>Вызывается вместо <see cref="ExecuteReset"/> при изменении параметров
    /// нормализации во время воспроизведения. Существующие gating blocks содержат
    /// корректные power values и могут быть пересчитаны с новыми параметрами за O(n).</para>
    /// <para>Исключает 3-секундный период блуждающего provisional gain
    /// который возникал при полном reset в предыдущей версии.</para>
    /// </remarks>
    private void ExecuteRecalculate()
    {
        if (_gatingBlockCount == 0)
        {
            ExecuteReset();
            return;
        }

        if (!float.IsNaN(_lockedGain))
        {
            // Raw gain из блоков (без mode constraint)
            float rawGain = ComputeIntegratedGainFromBlocks(
                _gatingBlockPowers, _gatingBlockCount, _targetLufs, _maxGain);

            // Mode constraint для рабочего значения
            float constrained = _mode == NormalizationMode.DownwardOnly
                ? MathF.Min(rawGain, 1.0f)
                : rawGain;

            _lockedGain = Math.Clamp(constrained, MinNormalizationGain, _maxGain);
            _smoothedNormGain = _lockedGain;

            Log.Debug($"[EbuR128] Recalculated from {_gatingBlockCount} blocks: " +
                      $"gain={_lockedGain:F3}x (raw={rawGain:F3}x, target={_targetLufs}LUFS, mode={_mode})");
            return;
        }

        _smoothedNormGain = _startingNormGain;
        Log.Debug($"[EbuR128] Recalc: params updated, analysis continues " +
                  $"(blocks={_gatingBlockCount}, target={_targetLufs}LUFS, mode={_mode})");
    }

    /// <summary>
    /// Сбрасывает всё состояние анализа (вызывается строго из fill thread).
    /// Также сбрасывает лог-дедупликацию для корректного логирования нового трека.
    /// </summary>
    private void ExecuteReset()
    {
        _lockedGain = float.NaN;
        Array.Clear(_blockChannelSumSq, 0, _blockChannelSumSq.Length);
        _blockFrameCount = 0;
        _gatingBlockCount = 0;
        _normalizationProcessedFrames = 0;
        _kWeightFilter.Reset();
        _smoothedNormGain = _startingNormGain;

#if DEBUG
        // Сброс лог-дедупликации: следующий LockFromCachedGain для нового трека
        // гарантированно залогируется.
        _lastLoggedCacheGain = float.NaN;
        Log.Debug("[EbuR128] Analysis reset: ready for new track");
#endif
    }

    /// <summary>
    /// Вычисляет integrated LUFS gain из массива gating block powers (EBU R128).
    /// Возвращает RAW gain БЕЗ применения mode constraint.
    /// </summary>
    /// <remarks>
    /// <para><b>Почему без DownwardOnly constraint:</b> Этот gain персистируется в БД
    /// как <see cref="TrackInfo.CachedNormalizationGain"/>. Если применить DownwardOnly
    /// clamp (1.0) до персистенции, при переключении на Bidirectional потерянный
    /// raw gain (например 1.710) невозможно восстановить — тихий трек навсегда
    /// останется без буста.</para>
    /// <para>Mode constraint применяется на этапе ИСПОЛЬЗОВАНИЯ:
    /// <see cref="NormalizationGainResolver.ApplyConstraints"/>,
    /// <see cref="ComputeProvisionalGain"/>, <see cref="LockFromCachedGain"/>.</para>
    /// </remarks>
    internal static float ComputeIntegratedGainFromBlocks(
        double[] blockPowers, int blockCount, float targetLufs, float maxGain)
    {
        if (blockCount == 0)
            return 1.0f;

        double sumPower = 0.0;
        for (int i = 0; i < blockCount; i++)
            sumPower += blockPowers[i];

        double meanPower = sumPower / blockCount;
        double integratedLufs = LufsOffset + 10.0 * Math.Log10(Math.Max(meanPower, 1e-20));

        // Relative gating (−10 LU)
        double relThreshold = integratedLufs + RelativeGateOffsetLu;
        double relPowerThreshold = Math.Pow(10.0, (relThreshold - LufsOffset) / 10.0);

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

        // НЕ применяем DownwardOnly здесь — raw gain для персистенции.
        return Math.Clamp(gain, MinNormalizationGain, maxGain);
    }

    #endregion
}