using System.Text.RegularExpressions;
using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Youtube.Videos.Streams;

internal partial class StreamController(HttpClient http) : VideoController(http)
{
    // ═══ CACHED PLAYER SOURCE ═══
    private static PlayerSource? s_cachedPlayerSource;
    private static string? s_cachedPlayerVersion;
    private static readonly SemaphoreSlim s_playerSourceLock = new(1, 1);

    /// <summary>
    /// Получает PlayerSource с кэшированием. Скачивает base.js только при первом вызове
    /// или после явной инвалидации через <see cref="InvalidatePlayerSourceCache"/>.
    /// Thread-safe: параллельные вызовы ждут один результат.
    /// </summary>
    public async ValueTask<PlayerSource> GetPlayerSourceAsync(
        CancellationToken cancellationToken = default)
    {
        // Fast path: уже закэшировано
        if (s_cachedPlayerSource is not null)
            return s_cachedPlayerSource;

        await s_playerSourceLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check после взятия лока
            if (s_cachedPlayerSource is not null)
                return s_cachedPlayerSource;

            var version = await DetectPlayerVersionAsync(cancellationToken);

            Log.Info($"[StreamController] Loading player source: {version}");

            var playerJs = await Http.GetStringAsync(
                $"https://www.youtube.com/s/player/{version}/player_ias.vflset/en_US/base.js",
                cancellationToken
            );

            var source = PlayerSource.Parse(playerJs);

            s_cachedPlayerVersion = version;
            s_cachedPlayerSource = source;

            Log.Info($"[StreamController] Player source cached: version={version}, " +
                     $"size={playerJs.Length / 1024}KB");

            return source;
        }
        catch (Exception ex)
        {
            Log.Error($"[StreamController] Failed to load player source: {ex.Message}");
            throw;
        }
        finally
        {
            s_playerSourceLock.Release();
        }
    }

    /// <summary>
    /// Только версия плеера (из iframe_api, ~2KB вместо ~2.5MB base.js).
    /// Кэшируется вместе с PlayerSource.
    /// </summary>
    public async ValueTask<string> GetPlayerVersionAsync(CancellationToken ct = default)
    {
        if (s_cachedPlayerVersion is not null)
            return s_cachedPlayerVersion;

        // Если версия ещё не известна, просто получим PlayerSource целиком
        // (она всё равно понадобится для sig/n-token)
        await GetPlayerSourceAsync(ct);
        return s_cachedPlayerVersion!;
    }

    /// <summary>
    /// Инвалидация кэша. Вызывать при ошибках расшифровки
    /// (YouTube обновил base.js, наш кэш протух).
    /// </summary>
    public static void InvalidatePlayerSourceCache()
    {
        s_cachedPlayerSource = null;
        s_cachedPlayerVersion = null;
        Log.Info("[StreamController] Player source cache invalidated");
    }

    /// <summary>
    /// Детектит версию плеера из iframe_api (~2KB запрос).
    /// </summary>
    private async Task<string> DetectPlayerVersionAsync(CancellationToken ct)
    {
        try
        {
            var iframe = await Http.GetStringAsync(
                "https://www.youtube.com/iframe_api", ct);

            var version = PlayerRegex().Match(iframe).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(version))
                throw new YoutubeExplodeException("Failed to extract player version from iframe_api");

            return version;
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"[StreamController] iframe_api fetch failed: {ex.Message}");
            throw new YoutubeExplodeException($"Failed to detect player version: {ex.Message}");
        }
    }

    public async ValueTask<DashManifest> GetDashManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
        => DashManifest.Parse(await Http.GetStringAsync(url, cancellationToken));

    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex PlayerRegex();
}