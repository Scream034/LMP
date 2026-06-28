using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Высокопроизводительные утилиты для обработки идентификаторов YouTube и метаданных медиа-потоков.
/// Минимизируют аллокации памяти в куче за счет эффективной работы со Span.
/// </summary>
public static class YoutubeIdHelper
{
    /// <summary>
    /// Быстро извлекает чистый YouTube ID без префиксов "yt_" или "yt_pl_" в виде Span без аллокаций.
    /// </summary>
    /// <param name="id">Исходный идентификатор.</param>
    /// <returns>Очищенный сегмент символов.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> ExtractRawIdSpan(ReadOnlySpan<char> id)
    {
        var trimmed = id.Trim();
        if (trimmed.StartsWith("yt_pl_"))
            return trimmed[6..];
        if (trimmed.StartsWith("yt_"))
            return trimmed[3..];
        return trimmed;
    }

    /// <summary>
    /// Извлекает чистый YouTube ID как строку. 
    /// Если префиксы отсутствуют, возвращает исходную строку без выделения новой памяти.
    /// </summary>
    /// <param name="id">Исходный идентификатор.</param>
    /// <returns>Очищенная строка идентификатора.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ExtractRawId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;

        var span = id.AsSpan().Trim();
        if (span.StartsWith("yt_pl_"))
            return new string(span[6..]);
        if (span.StartsWith("yt_"))
            return new string(span[3..]);

        return id;
    }

    /// <summary>
    /// Безопасно сопоставляет строковое представление контейнера с перечислением <see cref="AudioFormat"/>.
    /// </summary>
    /// <param name="container">Имя контейнера.</param>
    /// <returns>Соответствующий формат аудио.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioFormat MapContainerToFormat(string? container)
    {
        if (string.IsNullOrWhiteSpace(container)) 
            return AudioFormat.Unknown;

        var span = container.AsSpan().Trim();

        if (span.Equals("webm", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.WebM;
        if (span.Equals("mp4", StringComparison.OrdinalIgnoreCase) || span.Equals("m4a", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp4;
        if (span.Equals("ogg", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Ogg;
        if (span.Equals("m3u8", StringComparison.OrdinalIgnoreCase) || span.Equals("hls", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Hls;

        return Enum.TryParse<AudioFormat>(container, true, out var format) ? format : AudioFormat.Unknown;
    }
}