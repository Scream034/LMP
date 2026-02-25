namespace LMP.Core.Exceptions;

/// <summary>
/// Базовое исключение для аудио операций.
/// </summary>
public class AudioException : Exception
{
    public AudioException(string message) : base(message) { }
    public AudioException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Ошибка декодирования аудио.
/// </summary>
public class AudioDecoderException(string message, int errorCode = 0) : AudioException(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// Ошибка источника аудио.
/// </summary>
public class AudioSourceException : AudioException
{
    public AudioSourceException(string message) : base(message) { }
    public AudioSourceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// URL истёк и требует обновления.
/// </summary>
public class UrlExpiredException(string expiredUrl, string? trackId = null) 
    : AudioSourceException($"URL expired: {expiredUrl[..Math.Min(50, expiredUrl.Length)]}...")
{
    public string? TrackId { get; } = trackId;
    public string ExpiredUrl { get; } = expiredUrl;
}

/// <summary>
/// Неподдерживаемый формат аудио.
/// </summary>
public class UnsupportedFormatException(string format) 
    : AudioException($"Unsupported audio format: {format}")
{
    public string Format { get; } = format;
}

/// <summary>
/// Фатальная ошибка загрузки чанков — стрим безвозвратно недоступен.
/// </summary>
/// <remarks>
/// <para><b>Когда выбрасывается:</b></para>
/// <list type="bullet">
///   <item>Превышен лимит последовательных HTTP 403 ответов (<see cref="Audio.AudioConstants.Max403BeforeGiveUp"/> в CachingStreamSource)</item>
///   <item>YouTube вернул UMP формат вместо raw audio</item>
///   <item>Все retry-попытки исчерпаны без успеха</item>
/// </list>
/// 
/// <para><b>Обработка:</b></para>
/// <para>Исключение пробрасывается через:</para>
/// <code>
/// CachingStreamSource.EnsureChunkAsync()
///   → AudioPipeline.DecoderLoopAsync() [onError callback]
///     → AudioPlayer.HandleError()
///       → AudioPlayer.Events.ErrorOccurred
///         → AudioEngine.OnErrorOccurred
///           → PlaybackErrorOrchestrator
/// </code>
/// 
/// <para><b>Действия оркестратора</b> зависят от <see cref="Models.PlaybackErrorBehavior"/>:</para>
/// <list type="bullet">
///   <item>Dialog — пауза, модальный диалог</item>
///   <item>ToastAndSkip — toast + авто-skip</item>
///   <item>Ignore — только skip</item>
/// </list>
/// </remarks>
public class ChunkDownloadFatalException : AudioSourceException
{
    /// <summary>
    /// Индекс чанка, на котором произошла ошибка.
    /// </summary>
    public int ChunkIndex { get; }

    /// <summary>
    /// Количество последовательных неудачных попыток.
    /// </summary>
    public int ConsecutiveFailures { get; }

    /// <summary>
    /// Причина ошибки.
    /// </summary>
    public ChunkDownloadFailureReason Reason { get; }

    /// <summary>
    /// ID трека (для диагностики).
    /// </summary>
    public string? TrackId { get; }

    /// <summary>
    /// HTTP статус код последнего неудачного запроса (если применимо).
    /// </summary>
    public int? HttpStatusCode { get; }

    public ChunkDownloadFatalException(
        string message,
        int chunkIndex,
        int consecutiveFailures,
        ChunkDownloadFailureReason reason,
        string? trackId = null,
        int? httpStatusCode = null)
        : base(message)
    {
        ChunkIndex = chunkIndex;
        ConsecutiveFailures = consecutiveFailures;
        Reason = reason;
        TrackId = trackId;
        HttpStatusCode = httpStatusCode;
    }

    public ChunkDownloadFatalException(
        string message,
        int chunkIndex,
        int consecutiveFailures,
        ChunkDownloadFailureReason reason,
        string? trackId,
        int? httpStatusCode,
        Exception innerException)
        : base(message, innerException)
    {
        ChunkIndex = chunkIndex;
        ConsecutiveFailures = consecutiveFailures;
        Reason = reason;
        TrackId = trackId;
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>
    /// Возвращает ключ локализации для пользовательского сообщения.
    /// </summary>
    public string GetLocalizationKey() => Reason switch
    {
        ChunkDownloadFailureReason.Forbidden403 => "Error_Stream_Forbidden",
        ChunkDownloadFailureReason.UmpFormat => "Error_Stream_UmpFormat",
        ChunkDownloadFailureReason.MaxRetriesExceeded => "Error_Stream_MaxRetries",
        ChunkDownloadFailureReason.NetworkError => "Error_Stream_Network",
        _ => "Error_Stream_Unknown"
    };
}

/// <summary>
/// Причина фатальной ошибки загрузки чанков.
/// </summary>
public enum ChunkDownloadFailureReason
{
    /// <summary>Превышен лимит HTTP 403 Forbidden ответов.</summary>
    Forbidden403,

    /// <summary>YouTube вернул UMP (encrypted) формат вместо raw audio.</summary>
    UmpFormat,

    /// <summary>Превышен лимит retry-попыток.</summary>
    MaxRetriesExceeded,

    /// <summary>Сетевая ошибка (timeout, connection reset).</summary>
    NetworkError,

    /// <summary>Неизвестная ошибка.</summary>
    Unknown
}