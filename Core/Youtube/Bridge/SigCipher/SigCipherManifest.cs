using System.Text;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Манифест операций дешифровки подписи.
/// Иммутабельный, потокобезопасный.
/// </summary>
public sealed class SigCipherManifest
{
    /// <summary>Версия плеера (для кэширования)</summary>
    public string PlayerVersion { get; }
    
    /// <summary>Последовательность операций</summary>
    public IReadOnlyList<SigCipherOperation> Operations { get; }
    
    /// <summary>Время создания манифеста</summary>
    public DateTimeOffset CreatedAt { get; }
    
    /// <summary>Источник манифеста (для диагностики)</summary>
    public string Source { get; }

    public SigCipherManifest(
        string playerVersion,
        IEnumerable<SigCipherOperation> operations,
        string source = "extracted")
    {
        PlayerVersion = playerVersion;
        Operations = operations.ToArray();
        CreatedAt = DateTimeOffset.UtcNow;
        Source = source;
    }

    /// <summary>
    /// Применяет все операции к зашифрованной подписи.
    /// </summary>
    public string Decipher(string encryptedSignature)
    {
        if (string.IsNullOrEmpty(encryptedSignature))
            return encryptedSignature;

        // Работаем с char[] для производительности
        var chars = encryptedSignature.ToCharArray();
        int length = chars.Length;

        foreach (var op in Operations)
        {
            switch (op.Type)
            {
                case SigCipherOpType.Swap:
                    int swapPos = op.Parameter % length;
                    (chars[0], chars[swapPos]) = (chars[swapPos], chars[0]);
                    break;

                case SigCipherOpType.Reverse:
                    Array.Reverse(chars, 0, length);
                    break;

                case SigCipherOpType.Splice:
                    // Удаляем первые N символов — просто сдвигаем указатели
                    int removeCount = Math.Min(op.Parameter, length);
                    if (removeCount > 0 && removeCount < length)
                    {
                        // Создаём новый массив без первых N элементов
                        var newChars = new char[length - removeCount];
                        Array.Copy(chars, removeCount, newChars, 0, newChars.Length);
                        chars = newChars;
                        length = chars.Length;
                    }
                    break;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Сериализует манифест для кэширования.
    /// Формат: "version|op1,param1;op2,param2;..."
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder(128);
        sb.Append(PlayerVersion);
        sb.Append('|');
        
        for (int i = 0; i < Operations.Count; i++)
        {
            if (i > 0) sb.Append(';');
            var op = Operations[i];
            sb.Append((int)op.Type);
            sb.Append(',');
            sb.Append(op.Parameter);
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Десериализует манифест из строки.
    /// </summary>
    public static SigCipherManifest? Deserialize(string data)
    {
        try
        {
            var parts = data.Split('|', 2);
            if (parts.Length != 2) return null;

            var version = parts[0];
            var operations = new List<SigCipherOperation>();

            if (!string.IsNullOrEmpty(parts[1]))
            {
                foreach (var opStr in parts[1].Split(';'))
                {
                    var opParts = opStr.Split(',');
                    if (opParts.Length != 2) continue;

                    if (int.TryParse(opParts[0], out int typeInt) &&
                        int.TryParse(opParts[1], out int param))
                    {
                        operations.Add(new SigCipherOperation((SigCipherOpType)typeInt, param));
                    }
                }
            }

            return operations.Count > 0
                ? new SigCipherManifest(version, operations, "cached")
                : null;
        }
        catch
        {
            return null;
        }
    }

    public override string ToString() =>
        $"SigCipherManifest[{PlayerVersion}]: {string.Join(" → ", Operations)}";
}