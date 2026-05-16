using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Коэффициенты biquad IIR фильтра (Direct Form II Transposed).
/// Нормализованы (a0 = 1, исключён из struct).
/// </summary>
/// <remarks>
/// Передаточная функция: H(z) = (b0 + b1*z⁻¹ + b2*z⁻²) / (1 + a1*z⁻¹ + a2*z⁻²)
/// </remarks>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

/// <summary>
/// Состояние biquad IIR фильтра (Direct Form II Transposed).
/// Zero-alloc value type — хранит только две переменные задержки.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BiquadState
{
    public double Z1;
    public double Z2;

    /// <summary>
    /// Обрабатывает один сэмпл через фильтр и возвращает результат.
    /// Direct Form II Transposed — лучшая числовая стабильность для float.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Process(double input, in BiquadCoefficients c)
    {
        double output = c.B0 * input + Z1;
        Z1 = c.B1 * input - c.A1 * output + Z2;
        Z2 = c.B2 * input - c.A2 * output;
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Z1 = 0;
        Z2 = 0;
    }
}

/// <summary>
/// K-weighting фильтр по стандарту ITU-R BS.1770-4.
///
/// <para><b>Архитектура:</b> Два каскадных biquad IIR фильтра на канал:</para>
/// <list type="bullet">
///   <item><b>Stage 1 — High shelf (+4 dB @ ~1500 Hz):</b> Модель акустического эффекта головы.
///     Повышает ВЧ для соответствия восприятию (free-field → diffuse-field).</item>
///   <item><b>Stage 2 — High pass (~38 Hz, 2nd order):</b> RLB-взвешивание.
///     Подавляет НЧ, к которым человеческое ухо менее чувствительно.</item>
/// </list>
///
/// <para><b>Коэффициенты:</b></para>
/// <para>Стандарт ITU-R BS.1770-4 определяет коэффициенты ТОЛЬКО для 48 kHz.
/// Для других sample rate коэффициенты вычисляются через Audio EQ Cookbook (RBJ)
/// с параметрами аналогового прототипа, дающими идентичную АЧХ.</para>
///
/// <para><b>Аналоговый прототип (обратная инженерия 48 kHz коэффициентов):</b></para>
/// <list type="bullet">
///   <item>Shelf: gain ≈ +4 dB, f₀ ≈ 1500 Hz, Q ≈ 0.7071</item>
///   <item>High-pass: f₀ ≈ 38.13 Hz, Q ≈ 0.5003</item>
/// </list>
///
/// <para><b>Потокобезопасность:</b> НЕ потокобезопасен — один экземпляр на поток.
/// Вызывается только из fill thread backend'а (single writer).</para>
///
/// <para><b>Zero-alloc:</b> Все массивы pre-allocated в конструкторе, hot path без аллокаций.</para>
/// </summary>
public sealed class KWeightingFilter
{
    /// <summary>Параметры аналогового прототипа shelving filter (обратная инженерия ITU-R BS.1770-4).</summary>
    private const double ShelfGainDb = 3.999843853973347;
    private const double ShelfFreqHz = 1500.0;
    private const double ShelfQ = 0.7071752369554196;

    /// <summary>Параметры аналогового прототипа high-pass filter (RLB weighting).</summary>
    private const double HighPassFreqHz = 38.13547087602444;
    private const double HighPassQ = 0.5003270373238773;

    private readonly BiquadCoefficients _shelf;
    private readonly BiquadCoefficients _highPass;
    private readonly BiquadState[] _shelfState;
    private readonly BiquadState[] _highPassState;
    private readonly int _channels;

    /// <summary>
    /// Создаёт K-weighting фильтр для указанного sample rate и количества каналов.
    /// Коэффициенты вычисляются через Audio EQ Cookbook (bilinear transform).
    /// </summary>
    public KWeightingFilter(int sampleRate, int channels)
    {
        _channels = channels;
        _shelf = ComputeHighShelfCoefficients(sampleRate, ShelfFreqHz, ShelfGainDb, ShelfQ);
        _highPass = ComputeHighPassCoefficients(sampleRate, HighPassFreqHz, HighPassQ);
        _shelfState = new BiquadState[channels];
        _highPassState = new BiquadState[channels];
    }

    /// <summary>
    /// Обрабатывает один сэмпл одного канала через оба каскада K-weighting.
    /// Порядок: shelf → high-pass (как в спецификации ITU-R BS.1770-4).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ProcessSample(int channel, float input)
    {
        double x = input;
        x = _shelfState[channel].Process(x, in _shelf);
        x = _highPassState[channel].Process(x, in _highPass);
        return x;
    }

    /// <summary>
    /// Обрабатывает блок interleaved сэмплов через K-weighting фильтр.
    /// Использует <c>ref</c>-доступ к state массивам для bounds elision —
    /// проверка диапазона выполняется один раз в начале, все последующие обращения
    /// к <see cref="_shelfState"/> и <see cref="_highPassState"/> без bounds check.
    /// </summary>
    /// <param name="input">Входные interleaved PCM сэмплы [L,R,L,R,…].</param>
    /// <param name="output">Выходной буфер для K-weighted сэмплов (того же размера).</param>
    /// <remarks>
    /// IIR фильтры имеют data dependency между последовательными сэмплами одного канала,
    /// поэтому SIMD внутри канала невозможен. Оптимизация достигается за счёт:
    /// <list type="bullet">
    ///   <item>Bounds elision через <see cref="Unsafe.Add{T}(ref T, int)"/></item>
    ///   <item>Кэширование коэффициентов в <c>in</c>-параметрах (register promotion)</item>
    ///   <item>Минимизации virtual dispatch — единый метод вместо N вызовов ProcessSample</item>
    /// </list>
    /// </remarks>
    public void ProcessBlock(ReadOnlySpan<float> input, Span<double> output)
    {
        int channels = _channels;
        if (channels == 0 || input.Length == 0) return;

        ref var shelfRef = ref MemoryMarshal.GetArrayDataReference(_shelfState);
        ref var hpRef = ref MemoryMarshal.GetArrayDataReference(_highPassState);

        // Локальные копии коэффициентов — помогает JIT поместить их в регистры
        var shelf = _shelf;
        var hp = _highPass;

        int frameCount = input.Length / channels;
        int idx = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < channels; ch++, idx++)
            {
                double x = input[idx];
                x = Unsafe.Add(ref shelfRef, ch).Process(x, in shelf);
                x = Unsafe.Add(ref hpRef, ch).Process(x, in hp);
                output[idx] = x;
            }
        }
    }

    /// <summary>
    /// Сбрасывает внутреннее состояние фильтров всех каналов.
    /// Вызывается при seek или смене трека для устранения артефактов от предыдущего сигнала.
    /// </summary>
    public void Reset()
    {
        for (int ch = 0; ch < _channels; ch++)
        {
            _shelfState[ch].Reset();
            _highPassState[ch].Reset();
        }
    }

    #region Coefficient Computation (Audio EQ Cookbook — Robert Bristow-Johnson)

    /// <summary>
    /// High shelf biquad коэффициенты (Audio EQ Cookbook formulas).
    /// H(s) = A * [ s² + (√A/Q)*s + A ] / [ A*s² + (√A/Q)*s + 1 ]
    /// </summary>
    private static BiquadCoefficients ComputeHighShelfCoefficients(
        int sampleRate, double freq, double gainDb, double q)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);
        double sqrtA = Math.Sqrt(a);
        double twoSqrtAAlpha = 2.0 * sqrtA * alpha;

        double a0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha;
        double a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0);
        double a2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha;
        double b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha);
        double b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0);
        double b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha);

        // Нормализация: делим все на a0
        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    /// <summary>
    /// High pass biquad коэффициенты (Audio EQ Cookbook formulas).
    /// H(s) = s² / [ s² + s/Q + 1 ]
    /// </summary>
    private static BiquadCoefficients ComputeHighPassCoefficients(
        int sampleRate, double freq, double q)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);

        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;
        double b0 = (1.0 + cosW0) / 2.0;
        double b1 = -(1.0 + cosW0);
        double b2 = (1.0 + cosW0) / 2.0;

        double invA0 = 1.0 / a0;
        return new BiquadCoefficients(b0 * invA0, b1 * invA0, b2 * invA0, a1 * invA0, a2 * invA0);
    }

    #endregion
}