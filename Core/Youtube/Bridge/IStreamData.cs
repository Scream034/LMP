namespace LMP.Core.Youtube.Bridge;

internal interface IStreamData
{
    int? Itag { get; }

    string? Url { get; }

    string? Signature { get; }

    string? SignatureParameter { get; }

    long? ContentLength { get; }

    long? Bitrate { get; }

    string? MimeType { get; }

    string? Container { get; }

    string? AudioCodec { get; }

    string? AudioLanguageCode { get; }

    string? AudioLanguageName { get; }

    bool? IsAudioLanguageDefault { get; }

    string? VideoCodec { get; }

    string? VideoQualityLabel { get; }

    int? VideoWidth { get; }

    int? VideoHeight { get; }

    int? VideoFramerate { get; }

    /// <summary>
    /// Content loudness relative to the YouTube reference level (-14 LUFS), in dB.
    /// Present only on audio streams; absent on video-only streams and some live formats.
    /// <c>float.NaN</c> when the field is missing in the InnerTube response.
    /// </summary>
    float LoudnessDb { get; }
}
