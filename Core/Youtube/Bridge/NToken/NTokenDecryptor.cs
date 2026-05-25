using System.Collections.Concurrent;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Высокопроизводительный дешифратор N-Token YouTube на основе QuickJS-NG.
/// Наследует общую логику из JsDecryptorBase для устранения дублирования.
/// </summary>
public sealed partial class NTokenDecryptor(PlayerContextManager playerManager)
    : JsDecryptorBase<NTokenDecryptor>(playerManager, G.FilePath.NTokenCache, 2000, 500)
{
    private const int MaxNTokenLength = 60;

    private static readonly AsyncLocal<string?> CurrentDecryptionContext = new();
    private readonly ConcurrentDictionary<string, byte> _decryptedTokens = new(StringComparer.Ordinal);

    public event Action<string?>? OnComplexDecryptionStarted;

    protected override string FunctionName => "n";
    protected override string TestInput => "Siib9I-K-KF0GqS-";

    public static DecryptionContextCookie PushDecryptionContext(string? contextId)
    {
        var previousContext = CurrentDecryptionContext.Value;
        CurrentDecryptionContext.Value = contextId;
        return new DecryptionContextCookie(previousContext);
    }

    public readonly record struct DecryptionContextCookie(string? PreviousContext) : IDisposable
    {
        public void Dispose()
        {
            CurrentDecryptionContext.Value = PreviousContext;
        }
    }

    public async ValueTask<string> DecryptAsync(string nToken, string? contextId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;

        if (_decryptedTokens.ContainsKey(nToken))
        {
            Log.Debug($"[NToken] Idempotency bypass: '{nToken}' is already decrypted.");
            return nToken;
        }

        if (Cache.TryGet(nToken, out var cached))
        {
            _decryptedTokens.TryAdd(cached, 0);
            return cached;
        }

        OnComplexDecryptionStarted?.Invoke(contextId);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        // Используем полностью асинхронный вызов для защиты UI-потока
        var result = await TryInvokeJsAsync(nToken, "NToken", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result))
        {
            _decryptedTokens.TryAdd(result, 0);
            return result;
        }

        return nToken;
    }

    public ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default) =>
        DecryptAsync(nToken, contextId: null, ct);

    protected override bool ValidateResult(string? result, string input)
    {
        if (string.IsNullOrEmpty(result) || result == input) return false;
        if (result.Length < 5 || result.Length > MaxNTokenLength) return false;
        if (result is "undefined" or "null" or "NaN" or "true" or "false") return false;

        foreach (char c in result)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        if (input.Contains(result, StringComparison.Ordinal) || result.Contains(input, StringComparison.Ordinal))
            return false;

        if (HasLongSharedSubstring(input.AsSpan(), result.AsSpan(), 8))
            return false;

        return true;
    }

    private static bool HasLongSharedSubstring(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int minLength)
    {
        if (a.Length < minLength || b.Length < minLength) return false;
        for (int i = 0; i <= a.Length - minLength; i++)
        {
            var sub = a.Slice(i, minLength);
            if (b.IndexOf(sub) >= 0) return true;
        }
        return false;
    }

    public override void InvalidateCache()
    {
        _decryptedTokens.Clear();
        base.InvalidateCache();
    }

    public override void Dispose()
    {
        _decryptedTokens.Clear();
        base.Dispose();
    }
}