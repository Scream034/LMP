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
    
    // Реестр уже расшифрованных токенов для предотвращения повторной расшифровки (Double-Decryption)
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
        
        // Если токен уже расшифрован на предыдущем этапе (например, библиотекой), возвращаем его as-is
        if (_decryptedTokens.ContainsKey(nToken))
        {
            Log.Debug($"[NToken] Idempotency bypass: '{nToken}' is already decrypted.");
            return nToken;
        }

        if (Cache.TryGet(nToken, out var cached))
        {
            _decryptedTokens.TryAdd(cached, 0); // Регистрируем результат в реестре
            return cached;
        }

        OnComplexDecryptionStarted?.Invoke(contextId);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = TryInvokeJs(nToken, "NToken");
        if (!string.IsNullOrEmpty(result))
        {
            _decryptedTokens.TryAdd(result, 0); // Регистрируем результат в реестре
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

        string preprocessedJs = "";
        try
        {
            preprocessedJs = YoutubeAstSolver.PreprocessPlayer(context.BaseJs);

            var engine = CreateFullEngine();
            engine.SetValue("__log", new Action<string>(msg => Log.Debug(msg)));
            engine.Execute(BrowserStubs);
            engine.Execute("var _result = {};");
            engine.Execute(preprocessedJs);

            engine.Execute("globalThis.__decryptorTransform = _result.n;");

            var testOutput = engine.Invoke("__decryptorTransform", TestToken).AsString();
            if (ValidateNTokenResult(testOutput, TestToken))
            {
                FullEngine = engine;
                FullFuncName = "__decryptorTransform";
                IsInitialized = true;
                sw.Stop();
                Log.Info($"[NToken] AST-based Decryptor successfully initialized in {sw.ElapsedMilliseconds}ms!");
                return;
            }

            engine.Dispose();
            throw new InvalidOperationException("AST solver verification returned invalid token structure.");
        }
        catch (Exception ex)
        {
            Log.Error($"[NToken] AST-based Decryptor critical failure: {ex.Message}");
            if (!string.IsNullOrEmpty(preprocessedJs) && ex.Message.Contains("<anonymous>:"))
            {
                try
                {
                    var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"<anonymous>:(\d+):(\d+)");
                    if (match.Success)
                    {
                        int lineNum = int.Parse(match.Groups[1].Value);
                        var lines = preprocessedJs.Split('\n');
                        int startLine = Math.Max(0, lineNum - 10);
                        int endLine = Math.Min(lines.Length - 1, lineNum + 10);
                        Log.Error("--- CODE CONTEXT OF FAILURE ---");
                        for (int li = startLine; li <= endLine; li++)
                        {
                            var prefix = (li + 1 == lineNum) ? ">>> " : "    ";
                            Log.Error($"{prefix}{li + 1}: {lines[li].TrimEnd()}");
                        }
                        Log.Error("--------------------------------");
                    }
                }
                catch { /* ignore diagnostics failure */ }
            }
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
}