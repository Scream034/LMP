namespace LMP.Core.Exceptions;

/// <summary>
/// Базовое исключение для аудио операций.
/// </summary>
public class AudioException : Exception
{
    public AudioException(string message) : base(message) { }
    public AudioException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// Бросается когда файл кэша удалён, усечён или содержит повреждённые чанки.
/// </summary>
/// <remarks>
/// <para><b>Recoverable vs Fatal:</b></para>
/// <para>Если <see cref="IsRecoverable"/> = true, <c>AudioEngine</c> выполняет
/// автоматический retry (не более <c>MaxAutoRetries</c>) с сохранением позиции.
/// При <c>IsRecoverable</c> = false или исчерпании retry — ошибка поднимается в UI.</para>
/// </remarks>
public sealed class CacheInvalidatedException : Exception
{
    /// <summary>ID трека, для которого произошла ошибка.</summary>
    public string? TrackId { get; init; }

    /// <summary>
    /// Индекс повреждённого чанка. Null если ошибка затрагивает весь файл
    /// (например <see cref="CacheInvalidationKind.FileDeleted"/>).
    /// </summary>
    public int? ChunkIndex { get; init; }

    /// <summary>Можно ли восстановиться без участия пользователя.</summary>
    public bool IsRecoverable { get; init; }

    /// <summary>Классификация причины инвалидации.</summary>
    public CacheInvalidationKind Kind { get; init; }

    /// <summary>
    /// Количество уже выполненных попыток auto-retry для этого трека.
    /// Инкрементируется в <c>AudioEngine</c> перед каждым повторным запуском.
    /// </summary>
    public int RetryCount { get; init; }

    public CacheInvalidatedException(string message) : base(message) { }

    public CacheInvalidatedException(string message, Exception? inner) : base(message, inner) { }

    public CacheInvalidatedException(
        string message,
        CacheInvalidationKind kind,
        bool isRecoverable,
        string? trackId = null,
        int? chunkIndex = null,
        int retryCount = 0,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        IsRecoverable = isRecoverable;
        TrackId = trackId;
        ChunkIndex = chunkIndex;
        RetryCount = retryCount;
    }
}

/// <summary>Классификация причины инвалидации кэша.</summary>
public enum CacheInvalidationKind
{
    /// <summary>Файл удалён с диска во время воспроизведения.</summary>
    FileDeleted,

    /// <summary>Файл усечён: объявленная длина больше реального размера.</summary>
    Truncated,

    /// <summary>Short read одного чанка: данные на диске неполные.</summary>
    ShortRead,

    /// <summary>Парсер контейнера выполнял resync и достиг EOF раньше времени.</summary>
    ParserResync,

    /// <summary>Неизвестная причина.</summary>
    Unknown
}

/// <summary>
/// Бросается когда аудиоустройство вывода недоступно.
/// </summary>
public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException(string message) : base(message) { }
    public AudioDeviceException(string message, Exception? inner) : base(message, inner) { }
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
///   <item>Превышен лимит последовательных HTTP 403 ответов (<see cref="AudioConstants.Max403BeforeGiveUp"/> в CachingStreamSource)</item>
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
/// <para><b>Действия оркестратора</b> зависят от <see cref="PlaybackErrorBehavior"/>:</para>
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