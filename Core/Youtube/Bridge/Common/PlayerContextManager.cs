namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Единая точка управления версией плеера и base.js.
/// Singleton, потокобезопасный.
/// </summary>
/// <param name="http">HTTP-клиент для загрузки скриптов плеера.</param>
public class PlayerContextManager(HttpClient http)
{
    private readonly HttpClient _http = http;

    /// <summary>
    /// Семафор single-flight: гарантирует один активный <see cref="PlayerContext.DetectVersionAsync"/>
    /// при любом количестве параллельных вызовов <see cref="GetOrLoadAsync"/>.
    /// </summary>
    private readonly SemaphoreSlim _initSemaphore = new(1, 1);

    // volatile: атомарные read/write ссылок на x64 + memory fence — fast path без семафора.
    private volatile PlayerContext? _current;
    private volatile string? _cachedSignatureTimestamp;

    /// <summary>
    /// Возвращает актуальный контекст плеера.
    /// При отсутствии валидного in-memory контекста последовательно проверяет
    /// дисковый кэш и выполняет сетевую загрузку.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <exception cref="InvalidOperationException">
    /// Не удалось определить версию плеера или загрузить base.js.
    /// </exception>
    public virtual async Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        // Fast path: volatile read без захвата семафора
        var current = _current;
        if (current?.IsValid() == true) return current;

        await _initSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _current;
            if (current?.IsValid() == true) return current;

            var versionInfo = await PlayerContext.DetectVersionAsync(_http, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Failed to detect player version");

            var (version, urls) = versionInfo;

            // File.ReadAllText выполняется внутри семафора (не lock) — async-safe,
            // не блокирует другие потоки ожидающие семафор
            var cached = PlayerContext.LoadFromCache(version);
            if (cached is not null)
            {
                _current = cached;
                Log.Debug($"[PlayerContextManager] Loaded from cache: {version}");
                return cached;
            }

            foreach (var url in urls)
            {
                try
                {
                    Log.Debug($"[PlayerContextManager] Downloading: {url}");
                    var baseJs = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                    var newContext = new PlayerContext(version, baseJs);
                    await newContext.SaveCacheAsync().ConfigureAwait(false);
                    _current = newContext;
                    Log.Info($"[PlayerContextManager] Loaded fresh: {version} ({baseJs.Length / 1024}KB)");
                    return newContext;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Debug($"[PlayerContextManager] Download failed ({url}): {ex.Message}");
                }
            }

            throw new InvalidOperationException("Failed to download base.js from all candidate URLs");
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    // --- Section: STS Cache ---

    /// <summary>
    /// Возвращает закэшированный signatureTimestamp.
    /// Единый источник истины для всех экземпляров <see cref="VideoController"/>.
    /// </summary>
    public string? GetCachedSignatureTimestamp() => _cachedSignatureTimestamp;

    /// <summary>Записывает signatureTimestamp в кэш.</summary>
    /// <param name="sts">Значение STS или <c>null</c> для сброса.</param>
    public void SetCachedSignatureTimestamp(string? sts) => _cachedSignatureTimestamp = sts;

    /// <summary>
    /// Сбрасывает кэш signatureTimestamp.
    /// Вызывается через <see cref="VideoController.InvalidateSignatureTimestamp"/>
    /// при 403-recovery — гарантирует сброс для всех экземпляров <see cref="VideoController"/>.
    /// </summary>
    public void InvalidateSignatureTimestamp()
    {
        _cachedSignatureTimestamp = null;
        Log.Debug("[PlayerContextManager] SignatureTimestamp cache invalidated");
    }

    // --- Section: Invalidation ---

    /// <summary>
    /// Мягкая инвалидация: сбрасывает in-memory контекст, дисковый кэш сохраняется.
    /// </summary>
    public void InvalidateContext() => _current = null;

    /// <summary>
    /// Жёсткая инвалидация: сбрасывает in-memory контекст и физически удаляет дисковый кэш.
    /// </summary>
    public void Invalidate()
    {
        // Snapshot до обнуления: корректен при параллельной записи из GetOrLoadAsync
        var snapshot = _current;
        _current = null;
        if (snapshot != null)
            PlayerContext.ClearDiskCache(snapshot.Version);
    }
}