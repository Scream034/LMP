namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Представляет декларативный шаблон для сопоставления структуры узлов AST Esprima.
/// </summary>
public sealed record MatchTemplate
{
    /// <summary>Ожидаемый тип узла AST.</summary>
    public Type? NodeType { get; init; }

    /// <summary>Ожидаемый оператор выражения (например, "=").</summary>
    public string? Operator { get; init; }

    /// <summary>Шаблон для левой части выражения.</summary>
    public MatchTemplate? Left { get; init; }

    /// <summary>Шаблон для правой части выражения.</summary>
    public MatchTemplate? Right { get; init; }

    /// <summary>Шаблон для объекта в MemberExpression.</summary>
    public MatchTemplate? Object { get; init; }

    /// <summary>Шаблон для свойства в MemberExpression.</summary>
    public MatchTemplate? Property { get; init; }

    /// <summary>Шаблон для выражения в ExpressionStatement.</summary>
    public MatchTemplate? Expression { get; init; }

    /// <summary>Шаблон для вызываемого объекта в CallExpression.</summary>
    public MatchTemplate? Callee { get; init; }

    /// <summary>Шаблон для идентификатора (Id) в объявлении.</summary>
    public MatchTemplate? Id { get; init; }

    /// <summary>Шаблон для инициализатора (Init) переменной.</summary>
    public MatchTemplate? Init { get; init; }

    /// <summary>Набор шаблонов, хотя бы один из которых должен совпасть (логическое ИЛИ).</summary>
    public MatchTemplate[]? Or { get; init; }

    /// <summary>Набор шаблонов, каждый из которых должен совпасть хотя бы с одним элементом коллекции (anykey).</summary>
    public MatchTemplate[]? AnyKey { get; init; }

    /// <summary>Ожидаемое значение литерала.</summary>
    public object? Value { get; init; }

    /// <summary>Флаг асинхронности функции.</summary>
    public bool? Async { get; init; }

    /// <summary>Флаг вычисляемого свойства в MemberExpression.</summary>
    public bool? Computed { get; init; }

    /// <summary>Флаг опциональности вызова или свойства.</summary>
    public bool? Optional { get; init; }

    /// <summary>Ожидаемое имя идентификатора.</summary>
    public string? Name { get; init; }

    /// <summary>Шаблон для списка деклараций (VariableDeclaration).</summary>
    public MatchTemplate? DeclarationsTemplate { get; init; }

    /// <summary>Шаблоны для списка аргументов в CallExpression.</summary>
    public MatchTemplate[]? Arguments { get; init; }

    /// <summary>Ожидаемое количество аргументов в CallExpression.</summary>
    public int? ExpectedArgumentsCount { get; init; }
}