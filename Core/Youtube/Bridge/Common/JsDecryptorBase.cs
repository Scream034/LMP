using System.Diagnostics;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Базовый класс для дешифраторов с общей логикой инициализации и валидации.
/// Обеспечивает асинхронную загрузку контекста плеера вне UI-потока.
/// </summary>
public abstract class JsDecryptorBase<T>(
    PlayerContextManager playerManager,
    string cacheFilePath,
    int maxMemory,
    int maxDisk) : IYoutubeDecryptor, IDisposable
{
    /// <summary>Менеджер контекста плеера.</summary>
    public PlayerContextManager PlayerManager { get; } = playerManager;

    protected readonly DecryptorCache Cache = new(cacheFilePath, maxMemory, maxDisk);

    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    protected string? CurrentPlayerVersion;
    internal volatile bool IsInitialized;

    protected string DecryptorName => typeof(T).Name;
    protected string DiagFolder => Cache.CacheFolder;

    protected abstract string FunctionName { get; }
    protected abstract string TestInput { get; }
    protected abstract bool ValidateResult(string? result, string input);

    /// <summary>
    /// Асинхронно гарантирует, что движок дешифрации инициализирован.
    /// Тяжелые задачи (парсинг AST) выполняются внутри на ThreadPool пуле.
    /// </summary>
    public async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (IsInitialized) return;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;

            var context = await PlayerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
            CurrentPlayerVersion = context.Version;
            await Cache.LoadAsync(context.Version).ConfigureAwait(false);

            try
            {
                // Выполняем инициализацию (парсинг AST) в фоновом пуле потоков, чтобы не фризить UI
                await Task.Run(() => InitializeCore(context), ct).ConfigureAwait(false);

                IsInitialized = true;
                context.ReleaseRawScripts();
            }
            catch (Exception ex)
            {
                Log.Error($"[{DecryptorName}] Initialization failed: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Общая логика инициализации (DRY): препроцессинг скрипта и тестовая валидация.
    /// </summary>
    private void InitializeCore(PlayerContext context)
    {
        Log.Info($"[{DecryptorName}] Initializing via Unified AST Solver + QuickJS...");
        var sw = Stopwatch.StartNew();

        string preprocessedJs = context.GetOrPrepareScript(() => YoutubeAstSolver.PreprocessPlayer(context.BaseJs));

        var testOutput = QuickJsDecryptor.Decrypt(preprocessedJs, FunctionName, TestInput);
        if (ValidateResult(testOutput, TestInput))
        {
            sw.Stop();
            Log.Info($"[{DecryptorName}] QuickJS-NG Decryptor successfully initialized in {sw.ElapsedMilliseconds}ms!");
            return;
        }

        throw new InvalidOperationException($"[{DecryptorName}] QuickJS AST solver verification failed on test token.");
    }

    /// <summary>
    /// Полностью асинхронный вызов нативного QuickJS-дешифратора.
    /// Исключает блокирующий GetAwaiter().GetResult().
    /// </summary>
    protected async ValueTask<string?> TryInvokeJsAsync(string input, string logPrefix, CancellationToken ct)
    {
        var context = await PlayerManager.GetOrLoadAsync(ct).ConfigureAwait(false);
        string? preprocessedJs = context.PreprocessedJs;

        if (string.IsNullOrEmpty(preprocessedJs))
        {
            preprocessedJs = await Task.Run(() => {
                var cached = PlayerContext.LoadFromCache(context.Version);
                return cached?.PreprocessedJs ?? YoutubeAstSolver.PreprocessPlayer(context.BaseJs);
            }, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(preprocessedJs))
        {
            Log.Error($"[{DecryptorName}] Cannot invoke JS: preprocessed script is unavailable.");
            return null;
        }

        var sw = Stopwatch.StartNew();
        var result = QuickJsDecryptor.Decrypt(preprocessedJs, FunctionName, input);
        sw.Stop();

        if (!string.IsNullOrEmpty(result) && result != input)
        {
            Cache.Set(input, result);
            Log.Debug($"[{DecryptorName}] {logPrefix} (QuickJS-NG in {sw.ElapsedTicks / 10000.0:F3}ms): {Truncate(input)} -> {Truncate(result)}");
            return result;
        }

        return null;
    }

    public void InvalidateValue(string value)
    {
        Cache.RemoveByValue(value);
        Log.Info($"[{DecryptorName}] Invalidated cache entry for decrypted value: {Truncate(value)}");
    }

    protected static string Truncate(string s, int len = 20) => s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    public void FlushCache() => Cache.SaveAsync().GetAwaiter().GetResult();

    public virtual void InvalidateCache()
    {
        Cache.Clear();
        IsInitialized = false;
        Log.Info($"[{DecryptorName}] Cache invalidated");
    }

    public virtual void Dispose()
    {
        FlushCache();
        _initSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}