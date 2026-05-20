namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Единая точка управления версией плеера и base.js.
/// Singleton, потокобезопасный.
/// </summary>
/// <param name="http">HTTP клиент для загрузки скриптов.</param>
public class PlayerContextManager(HttpClient http)
{
    private readonly HttpClient _http = http;
    private readonly Lock _lock = new();
    private PlayerContext? _current;

    /// <summary>Получает актуальный контекст плеера (из кэша или скачивает).</summary>
    public virtual async Task<PlayerContext> GetOrLoadAsync(CancellationToken ct = default)
    {
        if (_current?.IsValid() == true) return _current;

        // Используем блокировку для предотвращения множественных загрузок
        lock (_lock)
        {
            if (_current?.IsValid() == true) return _current;
        }

        var versionInfo = await PlayerContext.DetectVersionAsync(_http, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Failed to detect player version");
        var (version, urls) = versionInfo;

        lock (_lock)
        {
            if (_current?.Version == version) return _current;

            var cached = PlayerContext.LoadFromCache(version);
            if (cached is not null)
            {
                _current = cached;
                Log.Debug($"[PlayerContext] Loaded from cache: {version}");
                return cached;
            }
        }

        foreach (var url in urls)
        {
            try
            {
                Log.Debug($"[PlayerContext] Downloading: {url}");
                var baseJs = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

                var newContext = new PlayerContext(version, baseJs);
                await newContext.SaveCacheAsync().ConfigureAwait(false);

                lock (_lock)
                {
                    _current = newContext;
                }

                Log.Info($"[PlayerContext] Loaded fresh: {version} ({baseJs.Length / 1024}KB)");
                return newContext;
            }
            catch (Exception ex)
            {
                Log.Debug($"[PlayerContext] Download failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException("Failed to download base.js");
    }

    /// <summary>Инвалидирует текущий контекст и физически стирает закэшированный скрипт с диска.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            if (_current != null)
            {
                PlayerContext.ClearDiskCache(_current.Version);
            }
            _current = null;
        }
    }
}