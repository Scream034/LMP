using System.Collections.Concurrent;
using LMP.Core.Youtube.Bridge.Common;

namespace LMP.Core.Youtube.Bridge.NToken;

/// <summary>
/// Дешифратор N-Token YouTube.
/// <para>
/// Тонкая обёртка над <see cref="JsDecryptionService"/>: добавляет
/// двухуровневый кэш (memory + disk), валидацию результата и idempotency-защиту.
/// Сам по себе не держит QuickJS runtime — всё делегируется shared service.
/// </para>
/// </summary>
public sealed class NTokenDecryptor : IYoutubeDecryptor, IDisposable
{
    private const int MaxNTokenLength = 60;
    private const int MinNTokenLength = 5;

    private readonly JsDecryptionService _jsService;
    private readonly DecryptorCache _cache;

    /// <summary>
    /// Idempotency guard: предотвращает повторную дешифрацию уже расшифрованного токена.
    /// Заполняется расшифрованными значениями (не оригиналами).
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _decryptedTokens =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Менеджер контекста плеера.
    /// Делегирует к <see cref="JsDecryptionService.PlayerManager"/> для обратной совместимости
    /// с <see cref="YoutubeProvider"/> и <see cref="VideoController"/>.
    /// </summary>
    public PlayerContextManager PlayerManager => _jsService.PlayerManager;

    /// <summary>
    /// Событие, сигнализирующее о начале комплексной (JS) дешифрации.
    /// Используется для UI-индикации и n-token warning.
    /// </summary>
    public event Action<string?>? OnComplexDecryptionStarted;

    /// <param name="jsService">Shared persistent QuickJS service.</param>
    /// <param name="cacheFilePath">Путь к файлу disk-кэша.</param>
    public NTokenDecryptor(JsDecryptionService jsService, string? cacheFilePath = null)
    {
        _jsService = jsService;
        _cache = new DecryptorCache(
            cacheFilePath ?? G.FilePath.NTokenCache,
            maxMemory: 2000,
            maxDisk: 500);
    }

    //  Public API 

    /// <summary>
    /// Расшифровывает N-Token YouTube.
    /// <para>
    /// Порядок поиска: idempotency guard → memory cache → disk cache → QuickJS.
    /// </para>
    /// </summary>
    /// <param name="nToken">Зашифрованный N-token из URL параметра <c>n</c>.</param>
    /// <param name="contextId">ID контекста для события <see cref="OnComplexDecryptionStarted"/>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Расшифрованный токен или исходный при ошибке.</returns>
    public async ValueTask<string> DecryptAsync(
        string nToken,
        string? contextId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nToken)) return nToken;

        // 1. Idempotency: токен уже расшифрован в этой сессии
        if (_decryptedTokens.ContainsKey(nToken))
        {
            Log.Debug($"[NToken] Idempotency bypass: '{Truncate(nToken)}' already decrypted");
            return nToken;
        }

        // 2. Memory + disk cache
        if (_cache.TryGet(nToken, out var cached))
        {
            _decryptedTokens.TryAdd(cached, 0);
            return cached;
        }

        // 3. JS дешифрация через shared persistent context
        OnComplexDecryptionStarted?.Invoke(contextId);

        await _jsService.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = await _jsService.CallAsync("n", nToken, ct).ConfigureAwait(false);

        if (ValidateResult(result, nToken))
        {
            _cache.Set(nToken, result!);
            _decryptedTokens.TryAdd(result!, 0);
            return result!;
        }

        Log.Warn($"[NToken] Validation failed for '{Truncate(nToken)}', returning original");
        return nToken;
    }

    /// <inheritdoc cref="DecryptAsync(string, string?, CancellationToken)"/>
    public ValueTask<string> DecryptAsync(string nToken, CancellationToken ct = default) =>
        DecryptAsync(nToken, contextId: null, ct);

    //  IYoutubeDecryptor 

    /// <inheritdoc/>
    public void InvalidateCache()
    {
        _decryptedTokens.Clear();
        _cache.Clear();
        Log.Info("[NToken] Cache invalidated");
    }

    /// <inheritdoc/>
    public Task FlushCacheAsync() => _cache.SaveAsync();

    //  Validation 

    private static bool ValidateResult(string? result, string input)
    {
        if (string.IsNullOrEmpty(result) || result == input) return false;
        if (result.Length < MinNTokenLength || result.Length > MaxNTokenLength) return false;
        if (result is "undefined" or "null" or "NaN" or "true" or "false") return false;

        foreach (char c in result)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        if (input.Contains(result, StringComparison.Ordinal) ||
            result.Contains(input, StringComparison.Ordinal))
            return false;

        if (HasLongSharedSubstring(input.AsSpan(), result.AsSpan(), minLength: 8))
            return false;

        return true;
    }

    private static bool HasLongSharedSubstring(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int minLength)
    {
        if (a.Length < minLength || b.Length < minLength) return false;

        for (int i = 0; i <= a.Length - minLength; i++)
        {
            if (b.IndexOf(a.Slice(i, minLength)) >= 0)
                return true;
        }

        return false;
    }

    private static string Truncate(string s, int len = 20) =>
        s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    //  Dispose 

    /// <inheritdoc/>
    public void Dispose()
    {
        _decryptedTokens.Clear();
        FlushCacheAsync().GetAwaiter().GetResult();
    }
}