using System.Collections.Concurrent;
using System.Diagnostics;
using Jint;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Высокопроизводительный дешифратор SigCipher (подписей) YouTube на основе AST-анализа.
/// </summary>
public sealed partial class SigCipherDecryptor(PlayerContextManager playerManager) 
    : JsDecryptorBase<SigCipherDecryptor>(playerManager, G.FilePath.SigCipherCache, 500, 100)
{
    private readonly ConcurrentDictionary<string, byte> _decryptedTokens = new(StringComparer.Ordinal);

    private const string TestSignature = 
        "AHEqNM4wRQIgUw3FiHA8Pht_xgtH0N_C7fQwvOMGHPW9KCHzFbzj_uECIQDPrmvV4I7V_V-uKiksYsVh1xBFwp_vFpXjjLL7T4pBxg==";

    /// <summary>
    /// Выполняет дешифрацию подписи с использованием AST-солвера.
    /// </summary>
    public async ValueTask<string> DecipherAsync(string signature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signature)) return signature;
        
        if (_decryptedTokens.ContainsKey(signature))
        {
            Log.Debug($"[SigCipher] Idempotency bypass: '{signature}' is already deciphered.");
            return signature;
        }

        if (Cache.TryGet(signature, out var cached))
        {
            _decryptedTokens.TryAdd(cached, 0);
            return cached;
        }

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var jsResult = TryInvokeJs(signature, "Decipher");
        if (jsResult is not null)
        {
            _decryptedTokens.TryAdd(jsResult, 0);
            return jsResult;
        }

        return signature;
    }

    protected override void InitializeCore(PlayerContext context)
    {
        Log.Info("[SigCipher] Initializing via Unified AST Solver...");
        var sw = Stopwatch.StartNew();

        try
        {
            var preprocessedJs = YoutubeAstSolver.PreprocessPlayer(context.BaseJs);

            var engine = CreateFullEngine();
            engine.SetValue("__log", new Action<string>(msg => Log.Debug(msg)));
            engine.Execute(BrowserStubs);
            engine.Execute("var _result = {};");
            engine.Execute(preprocessedJs);

            engine.Execute("globalThis.__decryptorTransform = _result.sig;");

            var testOutput = engine.Invoke("__decryptorTransform", TestSignature).AsString();
            if (!string.IsNullOrEmpty(testOutput) && testOutput != TestSignature)
            {
                FullEngine = engine;
                FullFuncName = "__decryptorTransform";
                sw.Stop();
                Log.Info($"[SigCipher] AST-based Decryptor successfully initialized in {sw.ElapsedMilliseconds}ms!");
                return;
            }

            engine.Dispose();
            throw new InvalidOperationException("AST solver verification returned unmodified signature.");
        }
        catch (Exception ex)
        {
            Log.Error($"[SigCipher] AST-based Decryptor critical failure: {ex.Message}");
            throw;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}