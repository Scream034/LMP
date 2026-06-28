namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown when the requested playlist is unavailable.
/// </summary>
public sealed class PlaylistUnavailableException(string message) : YoutubeExplodeException(message);
