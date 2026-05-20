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

    private readonly Lock _prepLock = new();
    private Jint.Prepared<Acornima.Ast.Script>? _preparedScript;

    /// <summary>Версия плеера (например, 6c5cb4f4).</summary>
    public string Version { get; }

    /// <summary>Полный исходный код base.js. Может быть очищен после подготовки скрипта.</summary>
    public string BaseJs { get; private set; }

    /// <summary>Препроцессированный и оптимизированный код JS. Может быть очищен после подготовки скрипта.</summary>
    public string? PreprocessedJs { get; private set; }

    /// <summary>Временная метка подписи (sts).</summary>
    public string? Sts { get; private set; }

    /// <summary>Время кэширования контекста.</summary>
    public DateTimeOffset CachedAt { get; }

    /// <summary>Создает новый контекст плеера.</summary>
    public PlayerContext(string version, string baseJs, string? preprocessedJs = null, string? sts = null)
    {
        Version = version;
        BaseJs = baseJs;
        PreprocessedJs = preprocessedJs;
        Sts = sts ?? (string.IsNullOrEmpty(baseJs) ? null : YoutubeAstSolver.ExtractSts(baseJs));
        CachedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Получает или создает pre-compiled скрипт Jint в рамках жизненного цикла контекста плеера.
    /// Исключает дублирование парсинга и компиляции между SigCipher и NToken.
    /// </summary>
    public Jint.Prepared<Acornima.Ast.Script> GetOrPrepareScript(Func<string> preprocessor)
    {
        if (_preparedScript.HasValue) return _preparedScript.Value;

        lock (_prepLock)
        {
            if (_preparedScript.HasValue) return _preparedScript.Value;

            var js = PreprocessedJs;
            if (string.IsNullOrEmpty(js))
            {
                js = preprocessor();
                PreprocessedJs = js;
                _ = SavePreprocessedCacheAsync();
            }

            _preparedScript = Jint.Engine.PrepareScript(js);
            return _preparedScript.Value;
        }
    }

    /// <summary>
    /// Освобождает исходные строки скрипта из оперативной памяти для предотвращения фрагментации кучи больших объектов (LOH).
    /// Вызывается дешифраторами после завершения инициализации компилированных ресурсов Jint.
    /// </summary>
    public void ReleaseRawScripts()
    {
        lock (_prepLock)
        {
            if (_preparedScript.HasValue)
            {
                BaseJs = string.Empty;
                PreprocessedJs = null;
                Log.Debug($"[PlayerContext] Discarded raw JS source strings from LOH memory for player version: {Version}");
            }
        }
    }   

    /// <summary>Проверяет, является ли кэш актуальным (не более 6 часов для соответствия циклу ротации YouTube).</summary>
    public bool IsValid() => (DateTimeOffset.UtcNow - CachedAt).TotalHours < 6;

    /// <summary>Сохраняет препроцессированный JS на диск.</summary>
    public async Task SavePreprocessedCacheAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(PreprocessedJs)) return;

            var prepPath = GetPreprocessedCachePath(Version);
            var stsPath = GetStsCachePath(Version);

            var dir = Path.GetDirectoryName(prepPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(prepPath, PreprocessedJs).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(Sts))
            {
                await File.WriteAllTextAsync(stsPath, Sts).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Preprocessed cache save failed: {ex.Message}");
        }
    }

    /// <summary>Сохраняет base.js в файловый кэш.</summary>
    public async Task SaveCacheAsync()
    {
        try
        {
            var path = GetCachePath(Version);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);

            if (!string.IsNullOrEmpty(BaseJs))
            {
                await File.WriteAllTextAsync(path, BaseJs).ConfigureAwait(false);
            }

            await SavePreprocessedCacheAsync().ConfigureAwait(false);
            CleanupOldVersions();
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache save failed: {ex.Message}");
        }
    }

    /// <summary>Загружает base.js или его препроцессированную версию из кэша.</summary>
    public static PlayerContext? LoadFromCache(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var baseJsPath = GetCachePath(version);

            if (File.Exists(prepPath) && File.Exists(stsPath))
            {
                var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(prepPath);
                if (age.TotalDays <= MaxAgeDays)
                {
                    var preprocessedJs = File.ReadAllText(prepPath);
                    var sts = File.ReadAllText(stsPath).Trim();

                    Log.Debug($"[PlayerContext] Loaded preprocessed cache for {version} ({preprocessedJs.Length / 1024}KB, sts={sts})");
                    return new PlayerContext(version, string.Empty, preprocessedJs, sts);
                }
            }

            if (!File.Exists(baseJsPath)) return null;

            var baseAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(baseJsPath);
            if (baseAge.TotalDays > MaxAgeDays)
            {
                Log.Debug($"[PlayerContext] Cache expired for {version} ({baseAge.TotalDays:F0}d old)");
                return null;
            }

            var baseJs = File.ReadAllText(baseJsPath);
            return new PlayerContext(version, baseJs);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Загружает base.js из кэша БЕЗ проверки возраста (для тестов).</summary>
    public static PlayerContext? LoadFromCacheNoExpiry(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var baseJsPath = GetCachePath(version);

            if (File.Exists(prepPath) && File.Exists(stsPath))
            {
                var preprocessedJs = File.ReadAllText(prepPath);
                var sts = File.ReadAllText(stsPath).Trim();

                if (!string.IsNullOrWhiteSpace(preprocessedJs))
                {
                    return new PlayerContext(version, string.Empty, preprocessedJs, sts);
                }
            }

            if (!File.Exists(baseJsPath)) return null;

            var baseJs = File.ReadAllText(baseJsPath);
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

    private static string GetPreprocessedCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_preprocessed.js");

    private static string GetStsCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_sts.txt");

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
                    var name = file.Name;
                    var versionMatch = Regex.Match(name, @"player_([a-fA-F0-9]+)_basejs\.txt");
                    if (versionMatch.Success)
                    {
                        var version = versionMatch.Groups[1].Value;
                        var prepPath = GetPreprocessedCachePath(version);
                        var stsPath = GetStsCachePath(version);

                        if (File.Exists(prepPath)) File.Delete(prepPath);
                        if (File.Exists(stsPath)) File.Delete(stsPath);
                    }

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