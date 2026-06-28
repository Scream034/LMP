namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown when the requested video is unavailable.
/// </summary>
public sealed class VideoUnavailableException(string message) : VideoUnplayableException(message);
