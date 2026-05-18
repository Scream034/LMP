namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Режим нормализации громкости.
/// </summary>
public enum NormalizationMode
{
    /// <summary>Spotify-стиль: тихие треки усиливаются, громкие понижаются до таргета.</summary>
    Bidirectional,

    /// <summary>YouTube-стиль: только понижение громких треков (gain ≤ 1.0).</summary>
    DownwardOnly
}

/// <summary>
/// Иммутабельная конфигурация нормализации.
/// Заменяет россыпь параметров <c>(bool, float, float, NormalizationMode)</c>
/// в <see cref="AudioPipeline.SetNormalization"/> и <c>ConfigurePipelineBeforeStart</c>.
/// </summary>
/// <param name="Enabled">Включена ли нормализация.</param>
/// <param name="TargetLufs">Целевой уровень LUFS (по умолчанию −14, как Spotify/YouTube).</param>
/// <param name="MaxGain">Максимальный gain нормализации (защита от перегрузки).</param>
/// <param name="Mode">Режим: Bidirectional (Spotify) или DownwardOnly (YouTube).</param>
public readonly record struct NormalizationConfig(
    bool Enabled,
    float TargetLufs = -14f,
    float MaxGain = 3.0f,
    NormalizationMode Mode = NormalizationMode.Bidirectional)
{
    /// <summary>Конфигурация по умолчанию: нормализация отключена.</summary>
    public static readonly NormalizationConfig Disabled = new(false);
}