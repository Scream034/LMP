using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Единые helper-методы для отображения и нормализации аудиоформатов и кодеков.
/// </summary>
public static class AudioMediaTypeExtensions
{
    /// <summary>
    /// Возвращает каноническое container-имя для persistence и transport boundary.
    /// </summary>
    /// <param name="format">Аудиоформат.</param>
    /// <returns>Каноническое имя контейнера в lower-case.</returns>
    public static string ToContainerName(this AudioFormat format) => format switch
    {
        AudioFormat.WebM => "webm",
        AudioFormat.Mp4 => "mp4",
        AudioFormat.Ogg => "ogg",
        AudioFormat.Hls => "hls",
        _ => ""
    };

    /// <summary>
    /// Возвращает display-имя формата для UI.
    /// </summary>
    /// <param name="format">Аудиоформат.</param>
    /// <returns>Человекочитаемое имя формата.</returns>
    public static string ToDisplayName(this AudioFormat format) => format switch
    {
        AudioFormat.WebM => "WebM",
        AudioFormat.Mp4 => "MP4",
        AudioFormat.Ogg => "Ogg",
        AudioFormat.Hls => "HLS",
        _ => "Unknown"
    };

    /// <summary>
    /// Возвращает display-имя кодека для UI.
    /// </summary>
    /// <param name="codec">Аудиокодек.</param>
    /// <returns>Человекочитаемое имя кодека.</returns>
    public static string ToDisplayName(this AudioCodec codec) => codec switch
    {
        AudioCodec.Opus => "Opus",
        AudioCodec.Aac => "AAC",
        AudioCodec.Vorbis => "Vorbis",
        _ => "Unknown"
    };

    /// <summary>
    /// Нормализует строковое представление кодека в <see cref="AudioCodec"/>.
    /// </summary>
    /// <param name="value">Строковое значение кодека.</param>
    /// <param name="fallbackFormat">Формат для fallback-резолва кодека.</param>
    /// <returns>Нормализованный кодек.</returns>
    public static AudioCodec ToAudioCodec(this string? value, AudioFormat fallbackFormat = AudioFormat.Unknown)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AudioSourceFactory.GetCodecForFormat(fallbackFormat);

        if (value.Contains("opus", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.Opus;

        if (value.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.Vorbis;

        if (value.Contains("aac", StringComparison.OrdinalIgnoreCase)
            || value.Contains("mp4a", StringComparison.OrdinalIgnoreCase))
        {
            return AudioCodec.Aac;
        }

        return Enum.TryParse<AudioCodec>(value, true, out var codec)
            ? codec
            : AudioSourceFactory.GetCodecForFormat(fallbackFormat);
    }
}