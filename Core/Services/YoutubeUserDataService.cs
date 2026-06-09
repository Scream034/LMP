using LMP.Core.Models;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Helpers.Extensions;
using LMP.Core.Youtube.Exceptions;

namespace LMP.Core.Services;

/// <summary>
/// Сервис для работы с данными пользователя YouTube Music.
/// Делегирует сетевые операции в YoutubeProvider, добавляя бизнес-логику.
/// </summary>
public partial class YoutubeUserDataService
{
    private readonly Lazy<YoutubeProvider> _youtubeLazy;
    private readonly CookieAuthService _auth;

    private YoutubeProvider Provider => _youtubeLazy.Value;

    /// <summary>
    /// Инициализирует новый экземпляр службы с ленивой зависимостью на провайдер.
    /// </summary>
    public YoutubeUserDataService(
        Lazy<YoutubeProvider> youtubeLazy, // Разрывает циклическую зависимость в DI
        CookieAuthService auth)
    {
        _youtubeLazy = youtubeLazy;
        _auth = auth;
    }

    #region Liked Tracks

    /// <summary>
    /// Получает лайкнутые треки в зависимости от режима синхронизации.
    /// </summary>
    /// <remarks>
    /// <para><b>MusicOnly:</b> Один запрос VLLM через WEB_REMIX — возвращает только музыку.
    /// Ранее дополнительно загружался плейлист LL (все лайки YouTube) для поиска "пропущенных"
    /// треков, что генерировало 10+ лишних HTTP-запросов. Убрано: YTM API — исчерпывающий
    /// источник музыкальных лайков, дублирование через LL не даёт значимого выигрыша.</para>
    ///
    /// <para><b>AllVideos:</b> Загружает LL целиком — все лайкнутые видео включая немузыкальные.</para>
    /// </remarks>
    public async Task<List<TrackInfo>> GetLikedTracksAsync(
        LikeSyncMode mode = LikeSyncMode.MusicOnly)
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            List<TrackInfo> likedTracks;

            switch (mode)
            {
                case LikeSyncMode.MusicOnly:
                    Log.Info("[Sync] Fetching Music Likes (LM) from YouTube Music...");
                    try
                    {
                        likedTracks = await Provider.GetClient().Music.GetLikedTracksAsync();
                        Log.Info($"[Sync] Got {likedTracks.Count} music likes from LM.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Sync] Music API failed, falling back to LL: {ex.Message}");
                        var allLikes = await GetAllLikedVideosAsync();
                        likedTracks = allLikes.FindAll(t => t.IsMusic);
                    }
                    break;

                case LikeSyncMode.AllVideos:
                    Log.Info("[Sync] Fetching ALL Liked Videos (LL)...");
                    likedTracks = await GetAllLikedVideosAsync();
                    break;

                case LikeSyncMode.LocalOnly:
                    Log.Info("[Sync] LocalOnly mode - no cloud sync.");
                    return [];

                default:
                    return [];
            }

            for (int i = 0; i < likedTracks.Count; i++)
            {
                var track = likedTracks[i];
                track.IsLiked = true;
                if (!track.Id.StartsWith("yt_", StringComparison.Ordinal))
                    track.Id = "yt_" + track.Id;
            }

            Log.Info($"[Sync] Total liked tracks: {likedTracks.Count}");
            return likedTracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to fetch liked tracks: {ex.Message}");
            return [];
        }
    }

    private async Task<List<TrackInfo>> GetAllLikedVideosAsync()
    {
        return await Provider.GetClient().Playlists
            .GetVideosAsync(new PlaylistId("LL"))
            .TakeAsync(1000)
            .ToListAsync();
    }

    #endregion

    #region Rating

    public async Task RateVideoAsync(string videoId, string rating)
    {
        await Provider.LikeTrackAsync(videoId, rating == "like");
    }

    #endregion

    #region Playlist Operations

    public async Task<string> CreatePlaylistAsync(string title, string description = "")
    {
        // Description is no longer supported via WEB client mutations.
        // Title-only creation via PlaylistMutationController.
        return await Provider.CreatePlaylistAsync(title)
            ?? throw new InvalidOperationException("YouTube API returned null playlist ID.");
    }

    public async Task DeletePlaylistAsync(string youtubePlaylistId)
    {
        await Provider.DeletePlaylistAsync(youtubePlaylistId);
    }

    public async Task AddTrackToPlaylistAsync(string youtubePlaylistId, string videoId)
    {
        await Provider.AddToPlaylistAsync(youtubePlaylistId, videoId);
    }

    #endregion

    #region Account

    /// <summary>
    /// Получает список всех каналов (бренд-аккаунтов), привязанных к текущим кукам.
    /// </summary>
    /// <exception cref="LoginRequiredException">Выбрасывается при невалидности или истечении сессии кук для данного эндпоинта.</exception>
    public async Task<List<YoutubeAccountItem>> GetAvailableAccountsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            var json = await Provider.GetClient().Music.GetAccountSwitcherAsync();
            var results = new List<YoutubeAccountItem>();

            var dataNode = json.GetPropertyOrNull("data") ?? json;
            var actions = dataNode.GetPropertyOrNull("actions")?.EnumerateArrayOrNull()?.FirstOrDefault();
            if (actions == null) return results;

            var menuObj = actions.Value.GetPropertyOrNull("getMultiPageMenuAction")?.GetPropertyOrNull("menu")
                          ?? actions.Value.GetPropertyOrNull("openPopupAction")?.GetPropertyOrNull("popup");

            var sections = menuObj?.GetPropertyOrNull("multiPageMenuRenderer")
                ?.GetPropertyOrNull("sections")
                ?.EnumerateArrayOrNull();

            if (sections == null) return results;

            foreach (var section in sections.Value)
            {
                var items = section.GetPropertyOrNull("accountSectionListRenderer")
                    ?.GetPropertyOrNull("contents")
                    ?.EnumerateArrayOrNull();

                if (items == null) continue;

                foreach (var itemWrap in items.Value)
                {
                    var accountItemSection = itemWrap.GetPropertyOrNull("accountItemSectionRenderer");
                    var subItems = accountItemSection?.GetPropertyOrNull("contents")?.EnumerateArrayOrNull();

                    if (subItems != null)
                    {
                        foreach (var subItemWrap in subItems.Value)
                        {
                            var accountItem = subItemWrap.GetPropertyOrNull("accountItem");
                            if (accountItem == null) continue;

                            ProcessAccountItem(accountItem.Value, results);
                        }
                    }
                    else
                    {
                        var accountItem = itemWrap.GetPropertyOrNull("accountItem");
                        if (accountItem == null) continue;

                        ProcessAccountItem(accountItem.Value, results);
                    }
                }
            }

            return results;
        }
        catch (LoginRequiredException ex) when (ex.Reason == LoginRequiredReason.SessionExpired)
        {
            Log.Warn($"[UserDataService] YouTube session expired during account listing. Propagating to UI to prompt for cookie update.");

            // Позволяем плееру продолжить работать в режиме "деградировавшей сессии",
            // пробрасывая исключение в UI для принятия решения пользователем.
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to parse accounts: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Разбирает элемент аккаунта из InnerTube JSON структуры, вычленяя PageId и AuthUser.
    /// </summary>
    private static void ProcessAccountItem(System.Text.Json.JsonElement accountItem, List<YoutubeAccountItem> results)
    {
        var name = accountItem.GetPropertyOrNull("accountName")
            ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("text")?.GetStringOrNull()
            ?? accountItem.GetPropertyOrNull("accountName")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? "Unknown";

        var email = accountItem.GetPropertyOrNull("accountByline")
            ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("text")?.GetStringOrNull()
            ?? accountItem.GetPropertyOrNull("accountByline")?.GetPropertyOrNull("simpleText")?.GetStringOrNull()
            ?? "";

        var avatar = accountItem.GetPropertyOrNull("accountPhoto")
            ?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.LastOrDefault()
            .GetPropertyOrNull("url")?.GetStringOrNull() ?? "";

        var isSelected = accountItem.GetPropertyOrNull("isSelected")?.GetBoolean() ?? false;

        string pageId = "";
        string gaiaId = "";
        string handle = "";
        string authUser = "0";

        // Извлекаем channelHandle (@тег канала)
        var parsedHandle = accountItem.GetPropertyOrNull("channelHandle")
            ?.GetPropertyOrNull("runs")?.EnumerateArrayOrNull()?.FirstOrDefault()
            .GetPropertyOrNull("text")?.GetStringOrNull();
        if (!string.IsNullOrEmpty(parsedHandle))
        {
            handle = parsedHandle;
        }

        var tokens = accountItem.GetPropertyOrNull("serviceEndpoint")
            ?.GetPropertyOrNull("selectActiveIdentityEndpoint")
            ?.GetPropertyOrNull("supportedTokens")
            ?.EnumerateArrayOrNull();

        if (tokens != null)
        {
            foreach (var token in tokens.Value)
            {
                var pId = token.GetPropertyOrNull("pageIdToken")?.GetPropertyOrNull("pageId")?.GetStringOrNull();
                if (!string.IsNullOrEmpty(pId))
                {
                    pageId = pId;
                }

                var stateToken = token.GetPropertyOrNull("accountStateToken");
                if (stateToken != null)
                {
                    var gId = stateToken.Value.GetPropertyOrNull("obfuscatedGaiaId")?.GetStringOrNull();
                    if (!string.IsNullOrEmpty(gId))
                    {
                        gaiaId = gId;
                    }
                }

                var signinToken = token.GetPropertyOrNull("accountSigninToken");
                if (signinToken != null)
                {
                    var signinUrl = signinToken.Value.GetPropertyOrNull("signinUrl")?.GetStringOrNull();
                    if (!string.IsNullOrEmpty(signinUrl))
                    {
                        var urlSpan = signinUrl.AsSpan();
                        int targetIdx = urlSpan.IndexOf("authuser=");
                        if (targetIdx >= 0)
                        {
                            var remaining = urlSpan[(targetIdx + 9)..];
                            int ampIdx = remaining.IndexOf('&');
                            var valSpan = ampIdx >= 0 ? remaining[..ampIdx] : remaining;
                            authUser = valSpan.ToString();
                        }
                    }
                }
            }
        }

        results.Add(new YoutubeAccountItem
        {
            Index = results.Count + 1,
            Name = name,
            Email = email,
            AvatarUrl = avatar,
            PageId = pageId,
            GaiaId = gaiaId,
            Handle = handle,
            AuthUser = authUser,
            IsSelected = isSelected
        });
    }

    public async Task<(string Name, string Email, string AvatarUrl)> GetAccountInfoAsync()
    {
        if (!_auth.IsAuthenticated) return ("Guest", "", "");

        try
        {
            var json = await Provider.GetClient().Music.GetAccountMenuAsync();

            var header = json.GetPropertyOrNull("actions")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("openPopupAction")
                ?.GetPropertyOrNull("popup")
                ?.GetPropertyOrNull("multiPageMenuRenderer")
                ?.GetPropertyOrNull("header")
                ?.GetPropertyOrNull("activeAccountHeaderRenderer");

            if (header == null) return ("User", "", "");

            var name = header.Value.GetPropertyOrNull("accountName")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("text")
                ?.GetStringOrNull() ?? "User";

            var email = header.Value.GetPropertyOrNull("email")
                ?.GetPropertyOrNull("runs")
                ?.EnumerateArrayOrNull()
                ?.FirstOrDefault()
                .GetPropertyOrNull("text")
                ?.GetStringOrNull() ?? "";

            var avatar = header.Value.GetPropertyOrNull("accountPhoto")
                ?.GetPropertyOrNull("thumbnails")
                ?.EnumerateArrayOrNull()
                ?.LastOrDefault()
                .GetPropertyOrNull("url")
                ?.GetStringOrNull() ?? "";

            return (name, email, avatar);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to get account menu: {ex.Message}");
            return ("User", "", "");
        }
    }

    #endregion

    #region Library

    public async Task<List<Playlist>> GetMyPlaylistsAsync()
    {
        if (!_auth.IsAuthenticated) return [];

        try
        {
            return await Provider.GetClient().Music.GetLibraryPlaylistsAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Failed to get user playlists: {ex.Message}");
            return [];
        }
    }

    #endregion
}