namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Exception thrown when YouTube denies a request because the client has exceeded rate limit.
/// </summary>
public sealed class RequestLimitExceededException(string message) : YoutubeExplodeException(message);
