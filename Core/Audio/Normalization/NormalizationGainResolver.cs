using LMP.Core.Models;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Единственный источник истины для резолюции gain нормализации трека
/// с учётом текущей конфигурации нормализации.
///
/// <para><b>Иерархия источников (приоритет по убыванию):</b></para>
/// <list type="number">
///   <item><b>EBU R128 gain</b> (<see cref="TrackInfo.CachedNormalizationGain"/>):
///     вычислен полным анализом для конкретного трека. Наиболее точный.</item>
///   <item><b>YouTube loudnessDb</b> (<see cref="TrackInfo.YoutubeIntegratedLoudnessDb"/>):
///     энкодирует интегральную громкость трека относительно -14 LUFS.
///     Конвертируется для ЛЮБОГО targetLufs и ЛЮБОГО режима через единую формулу.</item>
///   <item><b>float.NaN</b>: требуется EBU R128 анализ.</item>
/// </list>
///
/// <para><b>Математическое обоснование совместимости YouTube с Bidirectional:</b></para>
/// <para>YouTube определяет: <c>loudnessDb = trackLufs - YouTubeTarget = trackLufs + 14</c>.</para>
/// <para>Отсюда: <c>trackLufs = loudnessDb - 14</c>.</para>
/// <para>Gain для любого targetLufs:</para>
/// <code>gainDb = targetLufs - trackLufs = targetLufs - loudnessDb + 14</code>
/// <para>Примеры при target = -14 LUFS:</para>
/// <list type="bullet">
///   <item>Громкий трек: loudnessDb=+8.29 → gainDb = -14 - 8.29 + 14 = -8.29 dB → gain=0.385 (аттенуация) ✓</item>
///   <item>Тихий трек: loudnessDb=-4.66 → gainDb = -14 + 4.66 + 14 = +4.66 dB → gain=1.710 (буст) ✓</item>
/// </list>
/// <para>При target = -12 LUFS тихий трек: gainDb = -12 + 4.66 + 14 = +6.66 dB → gain=2.15 — корректно больше.</para>
///
/// <para><b>Stateless, pure function.</b></para>
/// </summary>
public static class NormalizationGainResolver
{
    /// <summary>
    /// Фиксированный reference level YouTube (дБ).
    /// YouTube измеряет: <c>loudnessDb = trackLufs − YouTubeReferenceLufs</c>.
    /// </summary>
    private const float YouTubeReferenceLufs = -14f;

    /// <summary>
    /// Резолвит линейный gain нормализации с учётом конфигурации.
    /// </summary>
    /// <param name="track">Информация о треке.</param>
    /// <param name="config">Текущая конфигурация нормализации.</param>
    /// <returns>
    /// Линейный gain если источник найден; <c>float.NaN</c> если требуется EBU R128 анализ.
    /// </returns>
    public static float Resolve(TrackInfo? track, NormalizationConfig config)
    {
        if (track == null || !config.Enabled) return float.NaN;

        // Приоритет 1: EBU R128 gain — точный анализ, универсален.
        if (track.HasCachedNormalizationGain)
            return ApplyConstraints(track.CachedNormalizationGain, config);

        // Приоритет 2: YouTube loudnessDb — конвертируем для текущего targetLufs и mode.
        if (track.HasYoutubeLoudnessDb)
            return ComputeGainFromYoutubeLoudness(track.YoutubeIntegratedLoudnessDb, config);

        return float.NaN;
    }

    /// <summary>
    /// Вычисляет линейный gain из YouTube loudnessDb для текущей конфигурации.
    /// </summary>
    /// <remarks>
    /// <para>YouTube определяет: <c>loudnessDb = trackLufs − (−14) = trackLufs + 14</c>.</para>
    /// <para>Поэтому: <c>trackLufs = loudnessDb − 14</c>.</para>
    /// <para>Gain для userTargetLufs:
    ///   <c>gainDb = userTargetLufs − trackLufs = userTargetLufs − loudnessDb + 14</c>.</para>
    /// <para>Верификация при target=−14:</para>
    /// <list type="bullet">
    ///   <item>loudnessDb=+8.29: gainDb = −14 − 8.29 + 14 = −8.29 dB → 0.385× (аттенуация) ✓</item>
    ///   <item>loudnessDb=−4.66: gainDb = −14 − (−4.66) + 14 = +4.66 dB → 1.710× (буст) ✓</item>
    /// </list>
    /// </remarks>
    private static float ComputeGainFromYoutubeLoudness(float loudnessDb, NormalizationConfig config)
    {
        // gainDb = targetLufs − trackLufs, где trackLufs = loudnessDb + YouTubeReferenceLufs
        float gainDb = config.TargetLufs - loudnessDb - YouTubeReferenceLufs;
        float gain = MathF.Pow(10f, gainDb / 20f);
        return ApplyConstraints(gain, config);
    }

    /// <summary>
    /// Применяет ограничения режима и clamp к computed gain.
    /// </summary>
    /// <param name="gain">Вычисленный линейный gain.</param>
    /// <param name="config">Конфигурация нормализации.</param>
    /// <returns>Gain с применёнными ограничениями.</returns>
    private static float ApplyConstraints(float gain, NormalizationConfig config)
    {
        if (config.Mode == NormalizationMode.DownwardOnly)
            gain = MathF.Min(gain, 1.0f);

        return Math.Clamp(gain, 0.1f, config.MaxGain);
    }
}