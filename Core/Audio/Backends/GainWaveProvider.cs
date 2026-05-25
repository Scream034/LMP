using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// IWaveProvider-обёртка над <see cref="BufferedWaveProvider"/>, применяющая
/// volume gain к PCM-данным в момент чтения WaveOut (on-read path).
///
/// <para><b>Зачем нужен этот класс (архитектурное обоснование):</b></para>
/// <para>Стандартный подход — применять gain в AudioCallback при ЗАПИСИ в provider.
/// Задержка отклика = размер provider буфера (до 500ms): WaveOut дочитывает PCM
/// с устаревшим gain прежде чем дойдёт до нового.</para>
/// <para>GainWaveProvider применяет volumeGain при ЧТЕНИИ WaveOut из provider.
/// WaveOut вызывает <see cref="Read"/> многократно, запрашивая следующий буфер.
/// Gain применяется к тому, что WaveOut играет прямо сейчас →
/// задержка = один waveOut буфер (~100ms), а не весь provider (500ms).</para>
///
/// <para><b>Thread model:</b></para>
/// <list type="bullet">
///   <item><see cref="SetVolumeGain"/> — вызывается из command thread (AudioEngine).</item>
///   <item><see cref="Read"/> — вызывается из WaveOut playback thread.</item>
///   <item><see cref="_targetGain"/> — volatile float: единственная точка
///     cross-thread коммуникации. Все остальные поля — single writer (WaveOut thread).</item>
/// </list>
///
/// <para><b>Zero-alloc hot path.</b> Аппаратное SIMD-ускорение через <see cref="Vector{T}"/>.</para>
/// </summary>
public sealed class GainWaveProvider : IWaveProvider
{
    #region Constants

    /// <summary>
    /// Длина ramp в сэмплах (stereo) для сглаживания смены громкости.
    /// 2400 samples @ 48kHz stereo = 25ms — достаточно для маскировки
    /// щелчка при быстрой смене громкости ползунком.
    /// Короче чем GainCrossfader (300ms) — здесь нет normalization transitions,
    /// только user volume: пользователь ожидает быстрый отклик.
    /// </summary>
    private const int RampSamples = 2400;

    #endregion

    #region Fields

    /// <summary>
    /// Источник PCM данных (нормализованный, без volume gain).
    /// </summary>
    private readonly BufferedWaveProvider _source;

    /// <summary>
    /// Целевой volume gain, устанавливаемый из command thread.
    /// Volatile — гарантирует видимость в WaveOut thread без lock.
    /// </summary>
    private volatile float _targetGain = 1.0f;

    /// <summary>
    /// Текущее значение gain применяемое к последнему прочитанному сэмплу.
    /// Single writer: WaveOut thread. Не требует volatile.
    /// </summary>
    private float _currentGain = 1.0f;

    /// <summary>
    /// Начальное значение gain текущего ramp перехода.
    /// Single writer: WaveOut thread.
    /// </summary>
    private float _rampStartGain = 1.0f;

    /// <summary>
    /// Количество оставшихся сэмплов в текущем ramp переходе.
    /// 0 = ramp завершён, применяется константный <see cref="_currentGain"/>.
    /// Single writer: WaveOut thread.
    /// </summary>
    private int _rampRemaining;

    #endregion

    /// <summary>
    /// Создаёт GainWaveProvider, оборачивающий указанный source provider.
    /// </summary>
    /// <param name="source">
    /// Источник PCM данных. Должен быть IEEE Float 32-bit format
    /// (WaveFormat.CreateIeeeFloatWaveFormat) — байты reinterpret-cast'ятся в float.
    /// </param>
    public GainWaveProvider(BufferedWaveProvider source)
    {
        _source = source;
    }

    /// <inheritdoc/>
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    /// Устанавливает новый целевой volume gain.
    /// При значимом изменении инициирует 25ms ramp для устранения щелчков.
    /// </summary>
    /// <remarks>
    /// <para>Метод thread-safe: пишет только в volatile <see cref="_targetGain"/>.
    /// Ramp-параметры (<see cref="_rampStartGain"/>, <see cref="_rampRemaining"/>)
    /// устанавливаются при следующем вызове <see cref="Read"/> из WaveOut thread,
    /// где они являются single-writer. Это намеренный подход:
    /// максимум один chunk (~50ms) отыграет без ramp — приемлемо,
    /// зато исключается любая синхронизация на hot path.</para>
    /// </remarks>
    /// <param name="gain">Volume gain множитель [0, MaxVolumeGain].</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVolumeGain(float gain)
    {
        _targetGain = Math.Max(0f, gain);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>Вызывается из WaveOut playback thread.</b></para>
    /// </remarks>
    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        // Reinterpret byte region как float span — zero-copy, нет аллокаций.
        // Безопасно: provider использует IEEE Float 32-bit format.
        var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, read));
        int length = floats.Length;

        float target = _targetGain;

        // Инициализируем ramp если gain изменился с прошлого вызова Read.
        if (MathF.Abs(target - _currentGain) > 0.0005f && _rampRemaining == 0)
        {
            _rampStartGain = _currentGain;
            _rampRemaining = RampSamples;
        }

        ref float floatsRef = ref MemoryMarshal.GetReference(floats);

        if (_rampRemaining > 0)
        {
            // Ramp path: per-sample линейная интерполяция (активна только ~25мс после изменения громкости)
            for (int i = 0; i < length; i++)
            {
                if (_rampRemaining > 0)
                {
                    float t = 1f - (float)_rampRemaining / RampSamples;
                    _currentGain = _rampStartGain + (target - _rampStartGain) * t;
                    _rampRemaining--;
                }
                else
                {
                    _currentGain = target;
                }

                float sample = Unsafe.Add(ref floatsRef, i) * _currentGain;
                // Hard clamp: защита от clip при volumeGain > 1.0 (boost mode).
                Unsafe.Add(ref floatsRef, i) = sample < -1f ? -1f : sample > 1f ? 1f : sample;
            }
        }
        else if (MathF.Abs(_currentGain - 1.0f) > 0.0005f)
        {
            // Constant gain path: Аппаратное SIMD-ускорение (AVX/SSE/Neon)
            // Явное использование Vector<float> с Vector.Min и Vector.Max даёт 100% стабильную векторизацию
            // на уровне инструкций процессора (MAXPS/MINPS) без ветвления и промахов предсказателя!
            float g = _currentGain;
            int i = 0;
            int vectorSize = Vector<float>.Count;

            if (Vector.IsHardwareAccelerated && length >= vectorSize)
            {
                var gainVec = new Vector<float>(g);
                var minVec = new Vector<float>(-1.0f);
                var maxVec = new Vector<float>(1.0f);

                for (; i <= length - vectorSize; i += vectorSize)
                {
                    var vec = new Vector<float>(floats.Slice(i, vectorSize));
                    var amplified = vec * gainVec;
                    
                    // Branch-free Math.Clamp на регистрах процессора
                    var clamped = Vector.Max(minVec, Vector.Min(maxVec, amplified));
                    clamped.CopyTo(floats.Slice(i, vectorSize));
                }
            }

            // Дорабатываем остаток буфера (unaligned tail)
            for (; i < length; i++)
            {
                float sample = Unsafe.Add(ref floatsRef, i) * g;
                Unsafe.Add(ref floatsRef, i) = sample < -1f ? -1f : sample > 1f ? 1f : sample;
            }
        }
        // else: gain == 1.0 — пропускаем без обработки (0% CPU, данные идут транзитом)

        return read;
    }
}