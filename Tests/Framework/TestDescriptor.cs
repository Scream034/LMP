using System.Reflection;

namespace LMP.Tests.Framework;

/// <summary>
/// Дескриптор обнаруженного теста. Содержит метаданные и MethodInfo для вызова.
/// Immutable record — создаётся один раз при discovery.
/// </summary>
public sealed record TestDescriptor
{
    /// <summary>Уникальный идентификатор: "ClassName.MethodName".</summary>
    public required string Id { get; init; }

    /// <summary>Человекочитаемое имя для UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Категория (Unit / Integration / Benchmark).</summary>
    public required TestCategory Category { get; init; }

    /// <summary>
    /// Тематическая группа: "NToken", "SigCipher", "Pipeline", "Cache", "Solver".
    /// Используется для быстрой фильтрации по подсистеме.
    /// </summary>
    public required string Group { get; init; }

    /// <summary>Имя класса-контейнера (для группировки).</summary>
    public required string ClassName { get; init; }

    /// <summary>Порядок сортировки внутри категории.</summary>
    public required int Order { get; init; }

    /// <summary>Требуется ли сеть.</summary>
    public required bool RequiresNetwork { get; init; }

    /// <summary>Таймаут в секундах (0 = без ограничения).</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>
    /// Требует ли метод IServiceProvider в параметрах.
    /// </summary>
    public required bool RequiresServices { get; init; }

    /// <summary>MethodInfo для вызова через рефлексию.</summary>
    public required MethodInfo Method { get; init; }
}

/// <summary>Состояние выполнения теста.</summary>
public enum TestRunState
{
    /// <summary>Тест ещё не запускался.</summary>
    NotRun,

    /// <summary>Тест выполняется прямо сейчас.</summary>
    Running,

    /// <summary>Тест пройден успешно.</summary>
    Passed,

    /// <summary>Тест провален.</summary>
    Failed,

    /// <summary>Тест пропущен.</summary>
    Skipped,
}

/// <summary>
/// Результат выполнения одного теста.
/// </summary>
public sealed record TestResult
{
    /// <summary>Идентификатор теста.</summary>
    public required string TestId { get; init; }

    /// <summary>Итоговое состояние.</summary>
    public required TestRunState State { get; init; }

    /// <summary>Время выполнения.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Сообщение об ошибке (null если пройден).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Форматированное время для UI.</summary>
    public string DurationFormatted => Duration.TotalMilliseconds < 1000
        ? $"{Duration.TotalMilliseconds:F0}ms"
        : $"{Duration.TotalSeconds:F1}s";
}