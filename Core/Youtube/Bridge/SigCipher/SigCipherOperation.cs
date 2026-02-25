namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Тип операции над массивом символов подписи.
/// </summary>
public enum SigCipherOpType
{
    /// <summary>swap(0, n) — меняет arr[0] с arr[n % length]</summary>
    Swap,
    
    /// <summary>reverse() — переворачивает массив</summary>
    Reverse,
    
    /// <summary>splice(0, n) — удаляет первые n элементов</summary>
    Splice
}

/// <summary>
/// Одна операция дешифровки подписи.
/// </summary>
public readonly record struct SigCipherOperation(SigCipherOpType Type, int Parameter)
{
    public override string ToString() => Type switch
    {
        SigCipherOpType.Swap => $"swap(0, {Parameter})",
        SigCipherOpType.Reverse => "reverse()",
        SigCipherOpType.Splice => $"splice(0, {Parameter})",
        _ => $"unknown({Parameter})"
    };
}