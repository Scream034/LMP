namespace LMP.Core.Data.Entities;

public sealed class NotificationEntity
{
    public string Id { get; set; } = string.Empty;
    
    /// <summary>Ключ локализации заголовка.</summary>
    public string? TitleKey { get; set; }
    
    /// <summary>Готовый текст заголовка (fallback).</summary>
    public string? TitleRaw { get; set; }
    
    /// <summary>Ключ локализации сообщения.</summary>
    public string? MessageKey { get; set; }
    
    /// <summary>Готовый текст сообщения (fallback).</summary>
    public string? MessageRaw { get; set; }
    
    /// <summary>Аргументы сообщения (JSON array).</summary>
    public string? MessageArgsJson { get; set; }
    
    /// <summary>Ключ локализации рекомендации.</summary>
    public string? RecommendationKey { get; set; }
    
    public int Severity { get; set; }
    public bool IsRead { get; set; }
    public string? TrackId { get; set; }
    public string? TrackTitle { get; set; }
    public string? ExceptionDetails { get; set; }
    public string? AttemptsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}