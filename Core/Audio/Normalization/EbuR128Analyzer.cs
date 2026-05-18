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

    /// <summary>Коэффициент lerp-сглаживания: ~5 chunk'ов (250ms) для 98% конвергенции.</summary>
    private const float LerpFactor = 0.35f;

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

    #endregion

    #region Configuration

    private volatile bool _enabled;
    private float _targetLufs = -14f;
    private float _maxGain = DefaultMaxNormalizationGain;
    private NormalizationMode _mode = NormalizationMode.Bidirectional;

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
    /// Применяет конфигурацию нормализации.
    /// Сброс состояния запрашивается только при реальном изменении параметров.
    /// </summary>
    public void Configure(NormalizationConfig config)
    {
        float clampedMaxGain = Math.Max(1f, config.MaxGain);

        if (config.Enabled && !_enabled)
        {
            _targetLufs = config.TargetLufs;
            _maxGain = clampedMaxGain;
            _mode = config.Mode;
            Interlocked.Exchange(ref _pendingNormReset, 1);
            _enabled = true;
            Log.Debug($"[EbuR128] Normalization ON: target={config.TargetLufs}LUFS, " +
                      $"maxGain={clampedMaxGain:F1}x, mode={config.Mode}");
        }
        else if (!config.Enabled && _enabled)
        {
            _enabled = false;
            _lockedGain = float.NaN;
            _smoothedNormGain = 1.0f;
            Log.Debug("[EbuR128] Normalization OFF");
        }
        else if (config.Enabled)
        {
            bool changed =
                MathF.Abs(_targetLufs - config.TargetLufs) > 0.01f ||
                MathF.Abs(_maxGain - clampedMaxGain) > 0.01f ||
                _mode != config.Mode;

            _targetLufs = config.TargetLufs;
            _maxGain = clampedMaxGain;
            _mode = config.Mode;

            if (changed)
            {
                Interlocked.Exchange(ref _pendingNormReset, 1);
                Log.Debug($"[EbuR128] Params changed: target={config.TargetLufs}LUFS, " +
                          $"maxGain={clampedMaxGain:F1}x, mode={config.Mode}");
            }
        }
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
    /// Используется когда gain уже есть в БД или вычислен из YouTube metadata —
    /// повторная персистенция избыточна.
    /// </summary>
    /// <param name="gain">Linear gain из кэша.</param>
    public void LockFromCachedGain(float gain)
    {
        if (!_enabled) return;
        if (gain <= 0f || !float.IsFinite(gain)) return;

        if (_mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, _maxGain);

        Interlocked.Exchange(ref _pendingNormReset, 0);
        _lockedGain = gain;
        _smoothedNormGain = gain;

        Log.Debug($"[EbuR128] Gain from cache: {gain:F3}x (analysis skipped)");
    }

    /// <summary>
    /// Фиксирует gain из реального EBU R128 анализа (pre-scan или real-time).
    /// Единственная точка вызова <see cref="_onGainLocked"/> callback'а —
    /// gain будет персистирован в БД для ускорения следующего воспроизведения.
    /// </summary>
    public void LockGain(float gain)
    {
        if (_mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        gain = Math.Clamp(gain, MinNormalizationGain, _maxGain);

        Interlocked.Exchange(ref _pendingNormReset, 0);
        _lockedGain = gain;
        _smoothedNormGain = gain;

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
    /// Измеряет K-weighted LUFS и возвращает gain нормализации.
    /// НЕ модифицирует сэмплы — только анализирует raw сигнал.
    /// </summary>
    /// <remarks>
    /// <para><b>ВЫЗЫВАТЬ ТОЛЬКО ИЗ FILL THREAD.</b></para>
    /// <para><b>Zero-alloc.</b> Bounds elision через <see cref="Unsafe.Add{T}(ref T, int)"/>.</para>
    /// <para><b>Fast path:</b> gain зафиксирован → мгновенный return без фильтрации.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ProcessSamples(ReadOnlySpan<float> samples)
    {
        // Deferred reset: исполняется строго из fill thread
        if (Interlocked.Exchange(ref _pendingNormReset, 0) == 1)
            ExecuteReset();

        // Fast path: gain зафиксирован — основной режим (≥3 сек от начала трека)
        if (!float.IsNaN(_lockedGain))
        {
            _smoothedNormGain = _lockedGain;
            return _lockedGain;
        }

        // ──── Фаза анализа: K-weighted LUFS + provisional gain ────

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
                      $"blocks={_gatingBlockCount})");
            return _lockedGain;
        }

        _smoothedNormGain += (provisionalGain - _smoothedNormGain) * LerpFactor;
        return _smoothedNormGain;
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

        double scannedSeconds = totalFrames / (double)_sampleRate;
        Log.Debug($"[EbuR128] Pre-scan: gain={rawGain:F3}x " +
                  $"(scanned {scannedSeconds:F1}s, blocks={blockCount}, target={_targetLufs}LUFS)");

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
    /// Provisional gain включая данные текущего неполного gating block.
    /// Позволяет получить оценку с первого callback (~50ms).
    /// </summary>
    private float ComputeProvisionalGain()
    {
        bool hasCompleted = _gatingBlockCount > 0;
        bool hasPartial = _blockFrameCount > 0;

        if (!hasCompleted && !hasPartial)
            return _startingNormGain;

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
            return Math.Clamp(gain, MinNormalizationGain, _maxGain);
        }

        return ComputeIntegratedGainFromBlocks(
            _gatingBlockPowers, _gatingBlockCount, _targetLufs, _maxGain);
    }

    /// <summary>Сбрасывает всё состояние анализа (вызывается строго из fill thread).</summary>
    private void ExecuteReset()
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
    /// Вычисляет integrated LUFS gain из массива gating block powers (EBU R128).
    /// Статический метод, переиспользуемый pre-scan и real-time анализом.
    /// </summary>
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

        return Math.Clamp(gain, MinNormalizationGain, maxGain);
    }

    #endregion
}