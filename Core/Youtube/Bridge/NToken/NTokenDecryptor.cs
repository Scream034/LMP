using System.Collections.Concurrent;
using Jint;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.SigCipher;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Высокопроизводительный дешифратор N-Token YouTube на основе AST-анализа.
/// </summary>
public sealed partial class NTokenDecryptor(PlayerContextManager playerManager)
    : JsDecryptorBase<NTokenDecryptor>(playerManager, G.FilePath.NTokenCache, 2000, 500)
{
    private const string TestToken = "Siib9I-K-KF0GqS-";
    private const int MaxNTokenLength = 60;

    private static readonly AsyncLocal<string?> CurrentDecryptionContext = new();
    
    private readonly ConcurrentDictionary<string, byte> _decryptedTokens = new(StringComparer.Ordinal);

    public event Action<string?>? OnComplexDecryptionStarted;

    public DecryptionContextCookie PushDecryptionContext(string? contextId)
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

    /// <summary>
    /// Выполняет идемпотентную дешифрацию N-Token.
    /// </summary>
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

        var result = TryInvokeJs(nToken, "NToken");
        if (!string.IsNullOrEmpty(result))
        {
            _decryptedTokens.TryAdd(result, 0);
            return result;
        }

        return nToken;
    }

    public ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default) =>
        DecryptAsync(nToken, contextId: null, ct);

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[NToken] Initializing via Unified AST Solver...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var preparedScript = context.GetOrPrepareScript(() => YoutubeAstSolver.PreprocessPlayer(context.BaseJs));

            using var testEngine = CreateFullEngine();
            testEngine.SetValue("__log", new Action<string>(msg => Log.Debug(msg)));
            testEngine.Execute(preparedScript);
            testEngine.Execute("globalThis.__decryptN = _result.n;");

            var testOutput = testEngine.Invoke("__decryptN", TestToken).AsString();
            if (ValidateNTokenResult(testOutput, TestToken))
            {
                SetupEnginePool(preparedScript, "__decryptN", "globalThis.__decryptN = _result.n;");
                IsInitialized = true;
                sw.Stop();
                Log.Info($"[NToken] AST-based Decryptor successfully initialized in {sw.ElapsedMilliseconds}ms!");
                return;
            }

            throw new InvalidOperationException("AST solver verification returned invalid token structure.");
        }
        catch (Exception ex)
        {
            Log.Error($"[NToken] AST-based Decryptor critical failure: {ex.Message}");
            throw;
        }
    }

    private static bool ValidateNTokenResult(string? result, string input)
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