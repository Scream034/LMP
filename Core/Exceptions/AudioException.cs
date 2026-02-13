namespace LMP.Core.Exceptions;

/// <summary>
/// Базовое исключение для аудио операций
/// </summary>
public class AudioException : Exception
{
    public AudioException(string message) : base(message) { }
    public AudioException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Ошибка декодирования аудио
/// </summary>
public class AudioDecoderException(string message, int errorCode = 0) : AudioException(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// Ошибка источника аудио
/// </summary>
public class AudioSourceException : AudioException
{
    public AudioSourceException(string message) : base(message) { }
    public AudioSourceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// URL истёк и требует обновления
/// </summary>
public class UrlExpiredException(string expiredUrl, string? trackId = null) : AudioSourceException($"URL expired: {expiredUrl[..Math.Min(50, expiredUrl.Length)]}...")
{
    public string? TrackId { get; } = trackId;
    public string ExpiredUrl { get; } = expiredUrl;
}

/// <summary>
/// Неподдерживаемый формат аудио
/// </summary>
public class UnsupportedFormatException(string format) : AudioException($"Unsupported audio format: {format}")
{
    public string Format { get; } = format;
}