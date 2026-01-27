namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Describes media streams available for a YouTube video.
/// </summary>
public class StreamManifest(IReadOnlyList<IStreamInfo> streams)
{
    /// <summary>
    /// Available streams.
    /// </summary>
    public IReadOnlyList<IStreamInfo> Streams { get; } = streams;

    /// <summary>
    /// Gets streams that contain audio (i.e. muxed and audio-only streams).
    /// </summary>
    public IEnumerable<IAudioStreamInfo> GetAudioStreams() => Streams.OfType<IAudioStreamInfo>();

    /// <summary>
    /// Gets audio-only streams.
    /// </summary>
    public IEnumerable<AudioOnlyStreamInfo> GetAudioOnlyStreams() =>
        GetAudioStreams().OfType<AudioOnlyStreamInfo>();
}
