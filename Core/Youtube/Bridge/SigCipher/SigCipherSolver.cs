using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Математический решатель для дешифровки подписи YouTube.
/// Работает БЕЗ парсинга JavaScript — только анализ входа/выхода.
///
/// Стратегия: constraint propagation + forward simulation.
/// Для каждого swap'а симулируем оставшиеся операции и проверяем,
/// какие значения параметра дают правильный символ на ключевых позициях.
/// Это сужает пространство поиска с O(99^N) до O(K^N) где K ≈ 1-5.
/// </summary>
[Obsolete("Это будет вырезано в будущих обновлениях, не использовать!")]
public static class SigCipherSolver
{
    private static readonly OpKind[][] KnownPatterns =
    [
        // 4 операции (самые частые)
        [OpKind.Swap, OpKind.Reverse, OpKind.Swap, OpKind.Splice],
        [OpKind.Swap, OpKind.Swap, OpKind.Reverse, OpKind.Splice],
        [OpKind.Reverse, OpKind.Swap, OpKind.Swap, OpKind.Splice],

        // 3 операции
        [OpKind.Reverse, OpKind.Swap, OpKind.Splice],
        [OpKind.Swap, OpKind.Reverse, OpKind.Splice],
        [OpKind.Swap, OpKind.Splice],
        [OpKind.Reverse, OpKind.Splice],

        // 5 операций (редкие)
        [OpKind.Swap, OpKind.Swap, OpKind.Swap, OpKind.Splice],
        [OpKind.Swap, OpKind.Reverse, OpKind.Swap, OpKind.Swap, OpKind.Splice],
    ];

    private enum OpKind : byte { Swap, Reverse, Splice }

    // ═══════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    public static List<SigCipherOperation>? Solve(string encrypted, string decrypted)
    {
        if (string.IsNullOrEmpty(encrypted) || string.IsNullOrEmpty(decrypted))
            return null;

        int spliceAmount = encrypted.Length - decrypted.Length;
        if (spliceAmount is < 0 or > 3)
            return null;

        var sw = Stopwatch.StartNew();
        int totalAttempts = 0;

        // Splice candidates: если длина отличается — точное значение, иначе 1-3
        int[] spliceCandidates = spliceAmount > 0 ? [spliceAmount] : [1, 2, 3];

        foreach (var pattern in KnownPatterns)
        {
            foreach (int splice in spliceCandidates)
            {
                var ops = BuildOpsTemplate(pattern, splice);
                var verifyBuf = new char[encrypted.Length];

                if (SolveRecursive(pattern, ops, 0, encrypted, decrypted, verifyBuf, ref totalAttempts))
                {
                    sw.Stop();
                    Log.Info($"[SigSolver] Found in {totalAttempts} attempts, " +
                             $"{sw.ElapsedMilliseconds}ms: {FormatOps(ops)}");
                    return [.. ops];
                }
            }
        }

        sw.Stop();
        Log.Warn($"[SigSolver] No solution after {totalAttempts} attempts, " +
                 $"{sw.ElapsedMilliseconds}ms");
        return null;
    }

    public static List<SigCipherOperation>? SolveParallel(
        string encrypted, string decrypted, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(encrypted) || string.IsNullOrEmpty(decrypted))
            return null;

        int spliceAmount = encrypted.Length - decrypted.Length;
        if (spliceAmount is < 0 or > 3)
            return null;

        int[] spliceCandidates = spliceAmount > 0 ? [spliceAmount] : [1, 2, 3];

        // Build all (pattern, splice) combinations
        var tasks = new List<(OpKind[] Pattern, int Splice)>();
        foreach (var pattern in KnownPatterns)
            foreach (int splice in spliceCandidates)
                tasks.Add((pattern, splice));

        List<SigCipherOperation>? result = null;
        int found = 0;

        Parallel.ForEach(
            tasks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                CancellationToken = ct
            },
            (task, state) =>
            {
                if (Volatile.Read(ref found) == 1) { state.Stop(); return; }

                var ops = BuildOpsTemplate(task.Pattern, task.Splice);
                var verifyBuf = new char[encrypted.Length];
                int attempts = 0;

                if (SolveRecursive(task.Pattern, ops, 0, encrypted, decrypted, verifyBuf, ref attempts))
                {
                    if (Interlocked.Exchange(ref found, 1) == 0)
                    {
                        Interlocked.Exchange(ref result, [.. ops]);
                        state.Stop();
                    }
                }
            });

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // CORE SOLVER: Constraint-propagating recursive search
    // ═══════════════════════════════════════════════════════════════

    private static SigCipherOperation[] BuildOpsTemplate(OpKind[] pattern, int spliceParam)
    {
        var ops = new SigCipherOperation[pattern.Length];
        for (int i = 0; i < pattern.Length; i++)
        {
            ops[i] = pattern[i] switch
            {
                OpKind.Reverse => new SigCipherOperation(SigCipherOpType.Reverse, 0),
                OpKind.Splice => new SigCipherOperation(SigCipherOpType.Splice, spliceParam),
                _ => default // swap — will be filled during search
            };
        }
        return ops;
    }

    private static bool SolveRecursive(
        OpKind[] pattern, SigCipherOperation[] ops, int index,
        string encrypted, string decrypted,
        char[] verifyBuf, ref int attempts)
    {
        // Skip non-swap positions
        while (index < pattern.Length && pattern[index] != OpKind.Swap)
            index++;

        // All swaps filled — verify
        if (index >= pattern.Length)
        {
            attempts++;
            return VerifyInPlace(ops, encrypted, decrypted, verifyBuf);
        }

        // Get constrained swap candidates for this position
        var candidates = FindSwapCandidates(pattern, ops, index, encrypted, decrypted);

        foreach (int swapParam in candidates)
        {
            ops[index] = new SigCipherOperation(SigCipherOpType.Swap, swapParam);

            if (SolveRecursive(pattern, ops, index + 1,
                    encrypted, decrypted, verifyBuf, ref attempts))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds which swap parameters at position `swapIndex` could possibly
    /// produce the correct output character at a specific tracked position.
    /// </summary>
    private static List<int> FindSwapCandidates(
        OpKind[] pattern, SigCipherOperation[] ops, int swapIndex,
        string encrypted, string decrypted)
    {
        // Step 1: Simulate all operations BEFORE this swap
        var buf = new char[encrypted.Length];
        encrypted.CopyTo(0, buf, 0, encrypted.Length);
        int len = encrypted.Length;

        for (int i = 0; i < swapIndex; i++)
            ApplyOp(ops[i], buf, ref len);

        // Step 2: Track position 0 in decrypted backwards through
        //         all operations AFTER this swap (inclusive) to find
        //         what position in buf (after swap) maps to decrypted[0].

        // Compute lengths at each step
        var lengths = new int[pattern.Length + 1];
        lengths[swapIndex] = len;
        for (int i = swapIndex; i < pattern.Length; i++)
        {
            lengths[i + 1] = pattern[i] == OpKind.Splice
                ? lengths[i] - Math.Min(ops[i].Parameter, lengths[i])
                : lengths[i];
        }

        // Reverse-trace from final output position 0 back to just after swap
        int pos = 0;
        for (int i = pattern.Length - 1; i > swapIndex; i--)
        {
            int lenBefore = lengths[i];

            switch (pattern[i])
            {
                case OpKind.Reverse:
                    pos = lenBefore - 1 - pos;
                    break;

                case OpKind.Splice:
                    pos += ops[i].Parameter;
                    break;

                case OpKind.Swap:
                    int swapN = ops[i].Parameter % lenBefore;
                    if (pos == 0) pos = swapN;
                    else if (pos == swapN) pos = 0;
                    break;
            }
        }

        // `pos` is now the position in the array AFTER the current swap
        // that must contain the character that eventually becomes decrypted[0].
        char neededChar = decrypted[0];

        // Step 3: For swap(0, n), determine which values of n produce
        //         the needed character at position `pos`.
        var candidates = new List<int>(16);

        if (pos == 0)
        {
            // Need buf[n % len] == neededChar
            for (int n = 1; n < len; n++)
            {
                if (buf[n] == neededChar)
                {
                    candidates.Add(n);
                    for (int mult = 1; mult * len + n <= 99; mult++)
                        candidates.Add(mult * len + n);
                }
            }
        }
        else if (buf[0] == neededChar)
        {
            // Case 2: n % len == pos → buf_after_swap[pos] = buf[0] == neededChar
            for (int mult = 0; mult * len + pos <= 99; mult++)
            {
                int n = mult * len + pos;
                if (n > 0) candidates.Add(n);
            }

            if (pos < len && buf[pos] == neededChar)
                AddFullRange(candidates, len, pos);
        }
        else if (pos < len && buf[pos] == neededChar)
        {
            // Case 3: position `pos` is unchanged by swap as long as n%len != pos
            AddFullRange(candidates, len, pos);
        }

        if (candidates.Count == 0)
            AddFullRange(candidates, len, -1);

        candidates.Sort();
        return Deduplicate(candidates);
    }

    private static void AddFullRange(List<int> candidates, int len, int excludeModPos)
    {
        // YouTube hot range first: 40-74, then 1-39, then 75-99
        for (int i = 40; i <= 74; i++)
            if (excludeModPos < 0 || i % len != excludeModPos) candidates.Add(i);
        for (int i = 1; i <= 39; i++)
            if (excludeModPos < 0 || i % len != excludeModPos) candidates.Add(i);
        for (int i = 75; i <= 99; i++)
            if (excludeModPos < 0 || i % len != excludeModPos) candidates.Add(i);
    }

    private static List<int> Deduplicate(List<int> sorted)
    {
        if (sorted.Count <= 1) return sorted;
        var result = new List<int>(sorted.Count) { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] != sorted[i - 1])
                result.Add(sorted[i]);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // SIMULATION HELPERS
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyOp(SigCipherOperation op, char[] buf, ref int len)
    {
        switch (op.Type)
        {
            case SigCipherOpType.Swap:
                int swapPos = op.Parameter % len;
                (buf[0], buf[swapPos]) = (buf[swapPos], buf[0]);
                break;
            case SigCipherOpType.Reverse:
                Array.Reverse(buf, 0, len);
                break;
            case SigCipherOpType.Splice:
                int rm = Math.Min(op.Parameter, len);
                if (rm > 0 && rm < len)
                {
                    Array.Copy(buf, rm, buf, 0, len - rm);
                    len -= rm;
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VerifyInPlace(
        ReadOnlySpan<SigCipherOperation> ops,
        string input, string expected, char[] buffer)
    {
        input.CopyTo(0, buffer, 0, input.Length);
        int length = input.Length;

        foreach (var op in ops)
        {
            switch (op.Type)
            {
                case SigCipherOpType.Swap:
                    int swapPos = op.Parameter % length;
                    (buffer[0], buffer[swapPos]) = (buffer[swapPos], buffer[0]);
                    break;
                case SigCipherOpType.Reverse:
                    Array.Reverse(buffer, 0, length);
                    break;
                case SigCipherOpType.Splice:
                    int removeCount = Math.Min(op.Parameter, length);
                    if (removeCount > 0 && removeCount < length)
                    {
                        Array.Copy(buffer, removeCount, buffer, 0, length - removeCount);
                        length -= removeCount;
                    }
                    break;
            }
        }

        if (length != expected.Length) return false;
        return expected.AsSpan().SequenceEqual(buffer.AsSpan(0, length));
    }

    private static string FormatOps(SigCipherOperation[] ops) =>
        string.Join(" → ", ops.Select(static o => o.ToString()));
}