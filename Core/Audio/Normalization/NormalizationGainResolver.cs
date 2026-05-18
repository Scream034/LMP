using LMP.Core.Models;

namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Единственный источник истины для определения gain нормализации трека.
///
/// <para><b>Stateless, pure function.</b> Не мутирует TrackInfo, не знает про Pipeline.</para>
///
/// <para><b>Логика:</b> Есть <see cref="TrackInfo.CachedNormalizationGain"/> → использовать.
/// Нет → анализ (pre-scan или real-time).</para>
///
/// <para><b>Откуда берётся CachedNormalizationGain:</b></para>
/// <list type="bullet">
///   <item>YouTube loudnessDb → <c>10^(-dB/20)</c> → persist при первом стриминге</item>
///   <item>EBU R128 pre-scan → persist через OnGainLocked callback</item>
///   <item>EBU R128 real-time (~3 сек) → persist через OnGainLocked callback</item>
/// </list>
/// <para>Все три пути сводятся к одному полю — нет приоритетов, нет дублирования.</para>
/// </summary>
public static class NormalizationGainResolver
{
    /// <summary>
    /// Резолвит gain нормализации из кэша трека.
    /// </summary>
    /// <returns>
    /// Cached gain если доступен, иначе <c>float.NaN</c> (нужен EBU R128 анализ).
    /// </returns>
    public static float Resolve(TrackInfo? track) =>
        track?.HasCachedNormalizationGain == true
            ? track.CachedNormalizationGain
            : float.NaN;
}