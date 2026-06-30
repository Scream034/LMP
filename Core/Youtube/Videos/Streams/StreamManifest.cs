namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Describes media streams available for a YouTube video.
/// </summary>
public sealed class StreamManifest(IReadOnlyList<IStreamInfo> streams, float integratedLufs = float.NaN)
{
    /// <summary>
    /// Available streams.
    /// </summary>
    public IReadOnlyList<IStreamInfo> Streams { get; } = streams;

    /// <summary>
    /// Track-level integrated loudness in LUFS from YouTube <c>perceptualLoudnessDb</c>.
    /// <c>float.NaN</c> when absent.
    /// </summary>
    public float IntegratedLufs { get; } = integratedLufs;

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