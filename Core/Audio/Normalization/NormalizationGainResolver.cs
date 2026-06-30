namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Единственный источник истины для runtime-вычисления gain нормализации.
/// <para>Stateless, pure function. Gain вычисляется исключительно из <see cref="TrackInfo.IntegratedLufs"/>
/// и текущего <see cref="NormalizationConfig"/>.</para>
/// </summary>
public static class NormalizationGainResolver
{
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
        if (track == null || !config.Enabled || !track.HasIntegratedLufs)
            return float.NaN;

        return ComputeGainFromIntegratedLufs(track.IntegratedLufs, config);
    }

    /// <summary>
    /// Вычисляет линейный gain из canonical integrated LUFS.
    /// </summary>
    /// <param name="integratedLufs">Integrated loudness трека в LUFS.</param>
    /// <param name="config">Текущая конфигурация нормализации.</param>
    /// <returns>Линейный gain.</returns>
    public static float ComputeGainFromIntegratedLufs(float integratedLufs, NormalizationConfig config)
    {
        if (!config.Enabled || !float.IsFinite(integratedLufs))
            return float.NaN;

        float gainDb = config.TargetLufs - integratedLufs;
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