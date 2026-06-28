using LMP.Core.Youtube.Videos;

namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown when the requested video requires purchase.
/// </summary>
public sealed class VideoRequiresPurchaseException(string message, VideoId previewVideoId)
    : VideoUnplayableException(message)
{
    /// <summary>
    /// ID of a free preview video which is used as promotion for the original video.
    /// </summary>
    public VideoId PreviewVideoId { get; } = previewVideoId;
}
