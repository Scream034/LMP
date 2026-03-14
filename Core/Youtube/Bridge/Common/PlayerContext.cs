using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Контекст версии плеера YouTube + закэшированный base.js.
/// Иммутабельный, потокобезопасный.
/// </summary>
public sealed partial class PlayerContext
{
    /// <summary>
    /// Максимальное количество закэшированных версий base.js.
    /// <para>
    /// Увеличено с 3 до 10, т.к. при тестировании совместимости
    /// с разными версиями плеера агрессивный cleanup удалял
    /// вручную подложенные файлы.
    /// </para>
    /// </summary>
    private const int MaxCachedVersions = 10;

    /// <summary>
    /// Максимальный возраст файла в днях, после которого он удаляется
    /// при cleanup (даже если лимит не превышен).
    /// </summary>
    private const int MaxAgeDays = 14;

    public string Version { get; }
    public string BaseJs { get; }
    public DateTimeOffset CachedAt { get; }

    public PlayerContext(string version, string baseJs)
    {
        Version = version;
        BaseJs = baseJs;
        CachedAt = DateTimeOffset.UtcNow;
    }

    public bool IsValid() =>
        (DateTimeOffset.UtcNow - CachedAt).TotalDays < 7;

    /// <summary>Сохраняет base.js в кэш.</summary>
    public async Task SaveCacheAsync()
    {
        try
        {
            var path = GetCachePath(Version);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, BaseJs);

            // Cleanup старых версий
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
                // НЕ удаляем здесь — пусть cleanup разберётся.
                // Просто не используем устаревший файл для production-контекста.
                // Но для тестов (FixedPlayerContextManager) файл всё ещё доступен напрямую.
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

    /// <summary>
    /// Загружает base.js из кэша БЕЗ проверки возраста.
    /// <para>
    /// Используется в тестах совместимости, где файлы base.js
    /// подкладываются вручную и могут быть сколь угодно старыми.
    /// В production-коде использовать <see cref="LoadFromCache"/>.
    /// </para>
    /// </summary>
    public static PlayerContext? LoadFromCacheNoExpiry(string version)
    {
        try
        {
            var path = GetCachePath(version);
            if (!File.Exists(path)) return null;

            var baseJs = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(baseJs))
            {
                Log.Debug($"[PlayerContext] Cache file empty for {version}");
                return null;
            }

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

    /// <summary>
    /// Удаляет старые закэшированные версии base.js.
    /// <para>
    /// ИСПРАВЛЕНИЕ: раньше оставлял только 3 файла, что приводило
    /// к удалению вручную подложенных тестовых файлов.
    /// Теперь:
    /// - Оставляет до <see cref="MaxCachedVersions"/> файлов
    /// - Удаляет только файлы старше <see cref="MaxAgeDays"/> дней
    ///   И превышающие лимит одновременно
    /// </para>
    /// </summary>
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
                .Skip(MaxCachedVersions) // Только файлы сверх лимита
                .Where(f => (now - f.LastWriteTimeUtc).TotalDays > MaxAgeDays) // И только устаревшие
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

    /// <summary>Определяет версию плеера и возможные URL base.js.</summary>
    public static async Task<(string Version, string[] Urls)?> DetectVersionAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        try
        {
            var iframeApi = await http.GetStringAsync(
                "https://www.youtube.com/iframe_api", ct);

            var match = PlayerVersionRegex().Match(iframeApi);
            if (!match.Success) return null;

            var version = match.Groups[1].Value;
            var urls = new[]
            {
                $"https://www.youtube.com/s/player/{version}/player_es6.vflset/ru_RU/base.js",
            };

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