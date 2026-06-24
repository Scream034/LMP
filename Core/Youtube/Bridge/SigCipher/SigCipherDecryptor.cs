using System.Collections.Concurrent;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.SigCipher;

/// <summary>
/// Дешифратор подписей (SigCipher) YouTube.
/// <para>
/// Тонкая обёртка над <see cref="JsDecryptionService"/>: добавляет
/// двухуровневый кэш (memory + disk) и idempotency-защиту.
/// Разделяет один persistent QuickJS context с <see cref="NToken.NTokenDecryptor"/>.
/// </para>
/// </summary>
public sealed class SigCipherDecryptor : IYoutubeDecryptor, IDisposable
{
    private readonly JsDecryptionService _jsService;
    private readonly DecryptorCache _cache;

    private readonly ConcurrentDictionary<string, byte> _decryptedTokens =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Менеджер контекста плеера.
    /// Делегирует к <see cref="JsDecryptionService.PlayerManager"/> для обратной совместимости
    /// с <see cref="VideoClient"/> и <see cref="StreamClient"/>.
    /// </summary>
    public PlayerContextManager PlayerManager => _jsService.PlayerManager;

    /// <param name="jsService">Shared persistent QuickJS service.</param>
    /// <param name="cacheFilePath">Путь к файлу disk-кэша.</param>
    public SigCipherDecryptor(JsDecryptionService jsService, string? cacheFilePath = null)
    {
        _jsService = jsService;
        _cache = new DecryptorCache(
            cacheFilePath ?? G.FilePath.SigCipherCache,
            maxMemory: 500,
            maxDisk: 100);
    }

    //  Public API 

    /// <summary>
    /// Расшифровывает подпись (signature) YouTube.
    /// </summary>
    /// <param name="signature">Зашифрованная подпись из <c>signatureCipher</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Расшифрованная подпись или исходная при ошибке.</returns>
    public async ValueTask<string> DecipherAsync(string signature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signature)) return signature;

        // 1. Idempotency
        if (_decryptedTokens.ContainsKey(signature))
        {
            Log.Debug($"[SigCipher] Idempotency bypass: already deciphered");
            return signature;
        }

        // 2. Cache
        if (_cache.TryGet(signature, out var cached))
        {
            _decryptedTokens.TryAdd(cached, 0);
            return cached;
        }

        // 3. JS дешифрация
        await _jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = await _jsService.CallAsync("sig", signature, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result) && result != signature)
        {
            _cache.Set(signature, result);
            _decryptedTokens.TryAdd(result, 0);
            return result;
        }

        return signature;
    }

    //  IYoutubeDecryptor 

    /// <inheritdoc/>
    public void InvalidateCache()
    {
        _decryptedTokens.Clear();
        _cache.Clear();
        Log.Info("[SigCipher] Cache invalidated");
    }

    /// <inheritdoc/>
    public Task FlushCacheAsync() => _cache.SaveAsync();

    //  Dispose 

    /// <inheritdoc/>
    public void Dispose()
    {
        _decryptedTokens.Clear();
        FlushCacheAsync().GetAwaiter().GetResult();
    }
}