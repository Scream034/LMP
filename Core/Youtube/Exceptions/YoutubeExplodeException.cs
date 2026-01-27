namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown within <see cref="Youtube" />.
/// </summary>
public class YoutubeExplodeException(string message) : Exception(message);
