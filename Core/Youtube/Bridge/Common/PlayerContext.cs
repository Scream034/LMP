using System.Text.RegularExpressions;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Контекст версии плеера YouTube + закэшированный base.js.
/// Иммутабельный, потокобезопасный.
/// </summary>
public sealed partial class PlayerContext
{
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
            if (age.TotalDays > 7)
            {
                File.Delete(path);
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

    private static string GetCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_basejs.txt");

    private static void CleanupOldVersions()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.NTokenCache, "player_*_basejs.txt");
            var toDelete = files
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(3) // Keep last 3 versions
                .ToArray();

            foreach (var file in toDelete)
                file.Delete();
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
                $"https://www.youtube.com/s/player/{version}/player_es6.vflset/en_US/base.js",
                $"https://www.youtube.com/s/player/{version}/player_ias.vflset/en_US/base.js",
                $"https://www.youtube.com/s/player/{version}/player_ias.vflset/ru_RU/base.js",
            };

            return (version, urls);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex PlayerVersionRegex();
}