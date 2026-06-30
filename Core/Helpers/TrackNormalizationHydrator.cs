using System.Runtime.CompilerServices;
using LMP.Core.Audio.Cache;

namespace LMP.Core.Helpers;

/// <summary>
/// Обеспечивает единую логику переноса метаданных нормализации звука из кэша в рантайм-модели треков.
/// </summary>
public static class TrackNormalizationHydrator
{
    /// <summary>
    /// Переносит значения нормализации и громкости из записи кэша в модель трека, если они ещё не установлены.
    /// </summary>
    /// <param name="track">Рантайм-модель трека.</param>
    /// <param name="entry">Запись кэша аудиофайла с сохранёнными метаданными.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HydrateNormalization(TrackInfo track, AudioCacheEntry entry)
    {
        if (!track.HasIntegratedLufs
            && entry.IntegratedLufs is float integratedLufs
            && float.IsFinite(integratedLufs))
        {
            track.SetIntegratedLufs(
                integratedLufs,
                (LMP.Core.Audio.Normalization.LoudnessSource)entry.IntegratedLufsSource);
        }
    }
}