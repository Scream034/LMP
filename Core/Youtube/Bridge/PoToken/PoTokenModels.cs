namespace LMP.Core.Youtube.Bridge.PoToken;

/// <summary>
/// Распарсенный ответ от WAA/Create (после дескрэмблинга).
/// Структура JSPB: [messageId, [[scriptUrl, scriptContent], interpreterHash], program, globalName, ...]
/// </summary>
public sealed record BotGuardChallenge
{
    /// <summary>URL bg.js скрипта (например //www.gstatic.com/bg/...).</summary>
    public required string ScriptUrl { get; init; }

    /// <summary>
    /// Хеш скрипта. Используется как ключ кеша bg.js —
    /// если хеш совпадает, скрипт не перекачивается.
    /// </summary>
    public required string InterpreterHash { get; init; }

    /// <summary>Байт-код задания. Меняется при каждом запросе.</summary>
    public required string Program { get; init; }

    /// <summary>
    /// Имя конструктора VM в globalThis.
    /// Например: "botguard", "bg", "_bg".
    /// </summary>
    public required string GlobalName { get; init; }

    /// <summary>
    /// Inline bg.js скрипт (когда YouTube отдаёт скрипт прямо в JSPB ответе, без отдельного URL).
    /// <c>null</c> если скрипт нужно скачивать по <see cref="ScriptUrl"/>.
    /// </summary>
    public string? InlineScript { get; init; }

    /// <summary>Есть ли готовый скрипт (inline или по URL).</summary>
    public bool HasScript => !string.IsNullOrEmpty(InlineScript) || !string.IsNullOrEmpty(ScriptUrl);
}

/// <summary>
/// Ответ от WAA/GenerateIT.
/// JSPB формат: [integrityToken, estimatedTtlSecs, mintRefreshThreshold, websafeFallbackToken?]
/// </summary>
public sealed record IntegrityTokenData
{
    public required string IntegrityToken { get; init; }

    /// <summary>TTL в секундах. Обычно 43200 (12 часов).</summary>
    public int EstimatedTtlSecs { get; init; }

    public int MintRefreshThreshold { get; init; }

    /// <summary>
    /// Флаг: токен получен из websafeFallbackToken ([3]) а не из integrityToken ([0]).
    /// Это нормально — YouTube иногда возвращает только fallback.
    /// </summary>
    public bool IsFallbackToken { get; init; }
}

/// <summary>Готовый PoToken с метаданными для кеширования.</summary>
public sealed record PoTokenResult
{
    public required string Token { get; init; }

    /// <summary>Идентификатор, с которым был заминчен токен (visitorId или videoId).</summary>
    public required string Identifier { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}

/// <summary>Сериализуемая запись PoToken для disk-кэша.</summary>
public sealed class PoTokenCacheEntry
{
    public string Token { get; set; } = "";
    public string Identifier { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CachedAt { get; set; }
}