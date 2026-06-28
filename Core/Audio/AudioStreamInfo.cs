namespace LMP.Core.Audio;

/// <summary>
/// Неизменяемая информация о текущем аудио потоке.
/// Единый источник правды о формате/кодеке/битрейте.
/// </summary>
public sealed record AudioStreamInfo
{
    public static readonly AudioStreamInfo Empty = new();

    public string TrackId { get; init; } = "";
    public string Container { get; init; } = "";  // WebM, Mp4, Ogg
    public string Codec { get; init; } = "";      // Opus, Aac
    public int Bitrate { get; init; }             // kbps
    public int SampleRate { get; init; }          // Hz
    public int Channels { get; init; }
    public long DurationMs { get; init; }
    public bool IsFromCache { get; init; }

    public bool IsValid => !string.IsNullOrEmpty(Codec) && Bitrate > 0;

    public string FormatDisplay => IsValid
        ? $"{Container}/{Codec}/{Bitrate:F0}kbps"
        : "Loading...";

    /// <summary>
    /// Создаёт <see cref="AudioStreamInfo"/> из <see cref="ResolvedStreamDescriptor"/>
    /// и runtime-параметров декодера.
    /// </summary>
    /// <param name="descriptor">Дескриптор resolved потока.</param>
    /// <param name="sampleRate">Sample rate из декодера.</param>
    /// <param name="channels">Количество каналов из декодера.</param>
    /// <param name="durationMs">Длительность из source.</param>
    /// <param name="isFromCache">Источник — полный дисковый кэш.</param>
    public static AudioStreamInfo FromDescriptor(
        ResolvedStreamDescriptor descriptor,
        int sampleRate,
        int channels,
        long durationMs,
        bool isFromCache)
    {
        return new AudioStreamInfo
        {
            TrackId = descriptor.TrackId,
            Container = descriptor.Format.ToString(),
            Codec = descriptor.Codec.ToString(),
            Bitrate = descriptor.BitrateKbps,
            SampleRate = sampleRate,
            Channels = channels,
            DurationMs = durationMs,
            IsFromCache = isFromCache
        };
    }
}