using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Неизменяемая информация о текущем аудио потоке.
/// Единый источник правды о формате/кодеке/битрейте.
/// </summary>
public sealed record AudioStreamInfo
{
    /// <summary>Пустой экземпляр для отсутствующего потока.</summary>
    public static readonly AudioStreamInfo Empty = new();

    /// <summary>Идентификатор трека.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>Типизированный формат контейнера.</summary>
    public AudioFormat Format { get; init; }

    /// <summary>Типизированный аудиокодек.</summary>
    public AudioCodec CodecType { get; init; }

    /// <summary>Битрейт потока в kbps.</summary>
    public int Bitrate { get; init; }

    /// <summary>Частота дискретизации в Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Количество каналов.</summary>
    public int Channels { get; init; }

    /// <summary>Длительность потока в миллисекундах.</summary>
    public long DurationMs { get; init; }

    /// <summary><c>true</c>, если поток воспроизводится из локального кэша.</summary>
    public bool IsFromCache { get; init; }

    /// <summary>Display-имя контейнера для UI.</summary>
    public string Container => Format.ToDisplayName();

    /// <summary>Display-имя кодека для UI.</summary>
    public string Codec => CodecType.ToDisplayName();

    /// <summary><c>true</c>, если stream-info содержит валидные данные.</summary>
    public bool IsValid => CodecType != AudioCodec.Unknown && Bitrate > 0;

    /// <summary>Готовая строка для UI-отображения формата потока.</summary>
    public string FormatDisplay => IsValid
        ? $"{Format.ToDisplayName()}/{CodecType.ToDisplayName()}/{Bitrate:F0}kbps"
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
    /// <returns>Типизированная информация о потоке.</returns>
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
            Format = descriptor.Format,
            CodecType = descriptor.Codec,
            Bitrate = descriptor.BitrateKbps,
            SampleRate = sampleRate,
            Channels = channels,
            DurationMs = durationMs,
            IsFromCache = isFromCache
        };
    }
}