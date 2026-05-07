using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Контекст версии плеера YouTube + закэшированный base.js.
/// Иммутабельный, потокобезопасный.
/// </summary>
public sealed partial class PlayerContext
{
    private const int MaxCachedVersions = 10;
    private const int MaxAgeDays = 14;

    /// <summary>Версия плеера (например, 6c5cb4f4).</summary>
    public string Version { get; }

    /// <summary>Полный исходный код base.js.</summary>
    public string BaseJs { get; }

    /// <summary>Время кэширования контекста.</summary>
    public DateTimeOffset CachedAt { get; }

    /// <summary>Создает новый контекст плеера.</summary>
    public PlayerContext(string version, string baseJs)
    {
        Version = version;
        BaseJs = baseJs;
        CachedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Проверяет, является ли кэш актуальным.</summary>
    public bool IsValid() => (DateTimeOffset.UtcNow - CachedAt).TotalDays < 7;

    /// <summary>Сохраняет base.js в файловый кэш.</summary>
    public async Task SaveCacheAsync()
    {
        try
        {
            var path = GetCachePath(Version);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            
            await File.WriteAllTextAsync(path, BaseJs).ConfigureAwait(false);
            CleanupOldVersions();
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache save failed: {ex.Message}");
        }
    }

    /// <summary>Загружает base.js из кэша.</summary>
    public static PlayerContext? LoadFromCache(string version)
    {
        try
        {
            var path = GetCachePath(version);
            if (!File.Exists(path)) return null;

            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age.TotalDays > MaxAgeDays)
            {
                Log.Debug($"[PlayerContext] Cache expired for {version} ({age.TotalDays:F0}d old)");
                return null;
            }

            var baseJs = File.ReadAllText(path);
            return new PlayerContext(version, baseJs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Загружает base.js из кэша БЕЗ проверки возраста (для тестов).</summary>
    public static PlayerContext? LoadFromCacheNoExpiry(string version)
    {
        try
        {
            var path = GetCachePath(version);
            if (!File.Exists(path)) return null;

            var baseJs = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(baseJs)) return null;

            return new PlayerContext(version, baseJs);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache load failed for {version}: {ex.Message}");
            return null;
        }
    }

    private static string GetCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_basejs.txt");

    private static void CleanupOldVersions()
    {
        try
        {
            var dir = G.Folder.NTokenCache;
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir, "player_*_basejs.txt");
            if (files.Length <= MaxCachedVersions) return;

            var now = DateTime.UtcNow;
            var candidates = files
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxCachedVersions)
                .Where(f => (now - f.LastWriteTimeUtc).TotalDays > MaxAgeDays)
                .ToArray();

            foreach (var file in candidates)
            {
                try
                {
                    file.Delete();
                    Log.Debug($"[PlayerContext] Cleaned up old cache: {file.Name}");
                }
                catch { /* ignore locked files */ }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Определяет версию плеера через iframe_api.</summary>
    public static async Task<(string Version, string[] Urls)?> DetectVersionAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        try
        {
            var iframeApi = await http.GetStringAsync("https://www.youtube.com/iframe_api", ct).ConfigureAwait(false);
            var match = PlayerVersionRegex().Match(iframeApi);
            if (!match.Success) return null;

            var version = match.Groups[1].Value;
            string[] urls = [$"https://www.youtube.com/s/player/{version}/player_es6.vflset/ru_RU/base.js"];

            return (version, urls);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerContext] Version detection failed: {ex.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex PlayerVersionRegex();
}