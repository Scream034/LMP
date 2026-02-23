namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Единая точка управления версией плеера и base.js.
/// Singleton, потокобезопасный.
/// </summary>
public sealed class PlayerContextManager
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PlayerContext? _current;
    
    public PlayerContextManager(HttpClient http)
    {
        _http = http;
    }
    
    /// <summary>Получает актуальный контекст плеера (из кэша или скачивает).</summary>
    public async Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        // Fast path
        if (_current?.IsValid() == true)
            return _current;
        
        await _lock.WaitAsync(ct);
        try
        {
            // Double-check
            if (_current?.IsValid() == true)
                return _current;
            
            // 1. Определяем версию
            var versionInfo = await PlayerContext.DetectVersionAsync(_http, ct);
            if (versionInfo is null)
                throw new InvalidOperationException("Failed to detect player version");
            
            var (version, urls) = versionInfo.Value;
            
            // 2. Проверяем кэш
            if (_current?.Version == version)
                return _current;
            
            var cached = PlayerContext.LoadFromCache(version);
            if (cached is not null)
            {
                _current = cached;
                Log.Debug($"[PlayerContext] Loaded from cache: {version}");
                return cached;
            }
            
            // 3. Скачиваем
            foreach (var url in urls)
            {
                try
                {
                    Log.Debug($"[PlayerContext] Downloading: {url}");
                    var baseJs = await _http.GetStringAsync(url, ct);
                    
                    _current = new PlayerContext(version, baseJs);
                    await _current.SaveCacheAsync();
                    
                    Log.Info($"[PlayerContext] Loaded fresh: {version} ({baseJs.Length / 1024}KB)");
                    return _current;
                }
                catch (Exception ex)
                {
                    Log.Debug($"[PlayerContext] Download failed: {ex.Message}");
                }
            }
            
            throw new InvalidOperationException("Failed to download base.js");
        }
        finally
        {
            _lock.Release();
        }
    }
    
    /// <summary>Инвалидирует текущий контекст.</summary>
    public void Invalidate()
    {
        _current = null;
    }
}