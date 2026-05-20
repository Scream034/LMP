using System.Collections.Concurrent;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Высокопроизводительный дешифратор SigCipher (подписей) YouTube на основе QuickJS-NG.
/// Наследует общую логику инициализации из JsDecryptorBase.
/// </summary>
public sealed partial class SigCipherDecryptor(PlayerContextManager playerManager) 
    : JsDecryptorBase<SigCipherDecryptor>(playerManager, G.FilePath.SigCipherCache, 500, 100)
{
    private readonly ConcurrentDictionary<string, byte> _decryptedTokens = new(StringComparer.Ordinal);

    protected override string FunctionName => "sig";
    protected override string TestInput => "AHEqNM4wRQIgUw3FiHA8Pht_xgtH0N_C7fQwvOMGHPW9KCHzFbzj_uECIQDPrmvV4I7V_V-uKiksYsVh1xBFwp_vFpXjjLL7T4pBxg==";

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

        // Используем полностью асинхронный вызов для защиты UI-потока
        var jsResult = await TryInvokeJsAsync(signature, "Decipher", ct).ConfigureAwait(false);
        if (jsResult is not null)
        {
            _decryptedTokens.TryAdd(jsResult, 0);
            return jsResult;
        }

        return signature;
    }

    protected override bool ValidateResult(string? result, string input)
    {
        return !string.IsNullOrEmpty(result) && result != input;
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