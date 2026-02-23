using LMP.Core.Youtube.Utils;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Metadata associated with a media stream of a YouTube video.
/// </summary>
public interface IStreamInfo
{
    /// <summary>
    /// Stream Itag (Format ID).
    /// </summary>
    int Itag { get; }

    /// <summary>
    /// Stream URL. Fully decrypted and ready for playback.
    /// </summary>
    string Url { get; }

    /// <summary>
    /// Stream container.
    /// </summary>
    Container Container { get; }

    /// <summary>
    /// Stream size.
    /// </summary>
    FileSize Size { get; }

    /// <summary>
    /// Stream bitrate.
    /// </summary>
    Bitrate Bitrate { get; }
}

public static class StreamInfoExtensions
{
    extension<T>(T streamInfo) where T : IStreamInfo
    {
        public bool IsThrottled() =>
            !string.Equals(
                UrlEx.TryGetQueryParameterValue(streamInfo.Url, "ratebypass"),
                "yes",
                StringComparison.OrdinalIgnoreCase
            );
    }

    extension<T>(IEnumerable<T> streamInfos) where T : IStreamInfo
    {
        public T? TryGetWithHighestBitrate() => streamInfos.MaxBy(static s => s.Bitrate);

        public T GetWithHighestBitrate() =>
            streamInfos.TryGetWithHighestBitrate()
            ?? throw new InvalidOperationException("Input stream collection is empty.");
    }
}