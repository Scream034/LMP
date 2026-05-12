using LMP.Core.Youtube.Videos.ClosedCaptions;

namespace LMP.Core.Youtube.Videos.Streams;

/// <summary>
/// Metadata associated with a media stream that contains audio.
/// </summary>
public interface IAudioStreamInfo : IStreamInfo
{
    /// <summary>
    /// Audio codec.
    /// </summary>
    string AudioCodec { get; }

    /// <summary>
    /// Audio language.
    /// </summary>
    /// <remarks>
    /// May be null if the audio stream does not contain language information.
    /// </remarks>
    Language? AudioLanguage { get; }

    /// <summary>
    /// Whether the audio stream's language corresponds to the default language of the video.
    /// </summary>
    /// <remarks>
    /// May be null if the audio stream does not contain language information.
    /// </remarks>
    bool? IsAudioLanguageDefault { get; }

    /// <summary>
    /// Indicates that the n-token in the URL was NOT successfully decrypted.
    /// URLs with encrypted n-tokens will receive HTTP 403 from YouTube.
    /// Used by upstream code to skip broken URLs and fall back to HLS immediately.
    /// </summary>
    public bool HasEncryptedNToken { get; }
}
