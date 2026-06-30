namespace LMP.Core.Audio.Normalization;

/// <summary>
/// Источник измеренной integrated loudness трека.
/// </summary>
public enum LoudnessSource : byte
{
    /// <summary>Значение отсутствует.</summary>
    Unknown = 0,

    /// <summary>Значение получено из YouTube <c>perceptualLoudnessDb</c>.</summary>
    YoutubePerceptual = 1,

    /// <summary>Значение получено локальным EBU R128 pre-scan.</summary>
    EbuPreScan = 2
}