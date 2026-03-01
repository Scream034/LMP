using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Music;

internal class MusicController(HttpClient http)
{
    private const string ApiUrl = "https://music.youtube.com/youtubei/v1";

    // Кэшированные UTF-8 байты для статических ключей JSON
    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8VisitorData = "visitorData"u8.ToArray();
    private static readonly byte[] Utf8User = "user"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    public string VisitorData { get; set; } = "";

    /// <summary>
    /// Записывает context блок напрямую в writer — без промежуточного JsonDocument.
    /// </summary>
    private void WriteContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8WebRemix);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.MusicClientVersion);
        writer.WriteString(Utf8Hl, YoutubeHttpHandler.GetHl());
        writer.WriteString(Utf8Gl, YoutubeHttpHandler.GetGl());

        if (!string.IsNullOrEmpty(VisitorData))
            writer.WriteString(Utf8VisitorData, VisitorData);
        else
            writer.WriteNull(Utf8VisitorData);

        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utf8User);
        writer.WriteStartObject();
        writer.WriteEndObject(); // user

        writer.WriteEndObject(); // context
    }

    private void UpdateVisitorData(JsonElement root)
    {
        var newVisitorData = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();

        if (!string.IsNullOrWhiteSpace(newVisitorData) && newVisitorData != VisitorData)
        {
            VisitorData = newVisitorData;
        }
    }

    private void AttachVisitorDataToRequest(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(VisitorData))
        {
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, VisitorData);
        }
    }

    /// <summary>
    /// Создает HttpContent с использованием ArrayPool.
    /// Пишет JSON напрямую в pooled буфер, без двойного копирования.
    /// </summary>
    private HttpContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            WriteContext(writer);
            writeBody(writer);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(bufferWriter.WrittenSpan.ToArray());
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    /// <summary>
    /// Общий метод POST-запроса к YouTube Music API.
    /// Убирает дублирование request creation + response parsing.
    /// </summary>
    private async Task<JsonElement> PostAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");
        AttachVisitorDataToRequest(request);
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var jsonDoc = await Json.ParseAsync(stream, cancellationToken);
        UpdateVisitorData(jsonDoc);

        return jsonDoc;
    }

    /// <summary>
    /// POST-запрос без необходимости читать тело ответа (fire-and-forget с проверкой статуса).
    /// </summary>
    private async Task PostFireAndForgetAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl}/{endpoint}");
        AttachVisitorDataToRequest(request);
        request.Content = CreateJsonContent(writeBody);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Best-effort: пытаемся обновить visitorData из ответа
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            UpdateVisitorData(await Json.ParseAsync(stream, cancellationToken));
        }
        catch { /* best effort */ }
    }

    #region Browse

    public async ValueTask<MusicBrowseResponse> GetBrowseAsync(
        string? browseId = null,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        var jsonDoc = await PostAsync("browse", writer =>
        {
            if (!string.IsNullOrEmpty(continuation))
                writer.WriteString("continuation", continuation);
            else if (!string.IsNullOrEmpty(browseId))
                writer.WriteString("browseId", browseId);
        }, cancellationToken);

        return new MusicBrowseResponse(jsonDoc);
    }

    #endregion

    #region Like

    public async Task SendLikeActionAsync(
        string endpoint, string videoId, CancellationToken cancellationToken)
    {
        await PostFireAndForgetAsync(endpoint, writer =>
        {
            writer.WritePropertyName("target");
            writer.WriteStartObject();
            writer.WriteString("videoId", videoId);
            writer.WriteEndObject();
        }, cancellationToken);
    }

    #endregion

    #region Playlist CRUD

    public async Task<string> CreatePlaylistAsync(
        string title,
        string description,
        List<string>? videoIds,
        CancellationToken cancellationToken)
    {
        var jsonDoc = await PostAsync("playlist/create", writer =>
        {
            writer.WriteString("title", title);
            writer.WriteString("description", description);

            if (videoIds is { Count: > 0 })
            {
                writer.WriteStartArray("videoIds");
                foreach (var id in videoIds)
                    writer.WriteStringValue(id);
                writer.WriteEndArray();
            }
        }, cancellationToken);

        return jsonDoc.GetPropertyOrNull("playlistId")?.GetStringOrNull()
            ?? throw new YoutubeExplodeException("Failed to create playlist: no playlistId in response.");
    }

    /// <summary>
    /// Переименовывает плейлист в YouTube Music.
    /// Использует browse/edit_playlist с ACTION_SET_PLAYLIST_NAME.
    /// </summary>
    public async Task RenamePlaylistAsync(
        string playlistId, string newTitle, CancellationToken cancellationToken)
    {
        var jsonDoc = await PostAsync("browse/edit_playlist", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

            writer.WriteStartArray("actions");

            writer.WriteStartObject();
            writer.WriteString("action", "ACTION_SET_PLAYLIST_NAME");
            writer.WriteString("playlistName", newTitle);
            writer.WriteEndObject();

            writer.WriteEndArray();
        }, cancellationToken);

        // Проверяем что YouTube не вернул ошибку
        var status = jsonDoc.GetPropertyOrNull("status")?.GetStringOrNull();
        if (status != null &&
            !status.Equals("STATUS_SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            throw new YoutubeExplodeException(
                $"Failed to rename playlist: status={status}");
        }
    }

    /// <summary>
    /// Обновляет описание плейлиста в YouTube Music.
    /// </summary>
    public async Task SetPlaylistDescriptionAsync(
        string playlistId, string description, CancellationToken cancellationToken)
    {
        var jsonDoc = await PostAsync("browse/edit_playlist", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

            writer.WriteStartArray("actions");

            writer.WriteStartObject();
            writer.WriteString("action", "ACTION_SET_PLAYLIST_DESCRIPTION");
            writer.WriteString("playlistDescription", description);
            writer.WriteEndObject();

            writer.WriteEndArray();
        }, cancellationToken);

        var status = jsonDoc.GetPropertyOrNull("status")?.GetStringOrNull();
        if (status != null &&
            !status.Equals("STATUS_SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            throw new YoutubeExplodeException(
                $"Failed to update playlist description: status={status}");
        }
    }

    /// <summary>
    /// Удаляет плейлист из YouTube Music аккаунта.
    /// </summary>
    public async Task DeletePlaylistAsync(
        string playlistId, CancellationToken cancellationToken)
    {
        await PostFireAndForgetAsync("playlist/delete", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));
        }, cancellationToken);
    }

    /// <summary>
    /// Добавляет видео в плейлист. Возвращает setVideoId из ответа (если доступен).
    /// </summary>
    public async Task<string?> AddPlaylistItemAsync(
        string playlistId,
        string videoId,
        CancellationToken cancellationToken)
    {
        var jsonDoc = await PostAsync("browse/edit_playlist", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", "ACTION_ADD_VIDEO");
            writer.WriteString("addedVideoId", videoId);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }, cancellationToken);

        // Try to extract setVideoId from response
        // Response path: playlistEditResults[0].playlistEditVideoAddedResultData.setVideoId
        try
        {
            var editResults = jsonDoc.GetPropertyOrNull("playlistEditResults");
            if (editResults?.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in editResults.Value.EnumerateArray())
                {
                    var setVideoId = result
                        .GetPropertyOrNull("playlistEditVideoAddedResultData")
                        ?.GetPropertyOrNull("setVideoId")
                        ?.GetStringOrNull();

                    if (!string.IsNullOrEmpty(setVideoId))
                    {
                        Log.Debug($"[MusicController] Got setVideoId={setVideoId} for video={videoId}");
                        return setVideoId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[MusicController] Could not parse setVideoId from add response: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Удаляет видео из плейлиста по setVideoId.
    /// YouTube API требует setVideoId (уникальный ID элемента в плейлисте),
    /// а не videoId для удаления.
    /// </summary>
    public async Task RemovePlaylistItemAsync(
        string playlistId,
        string videoId,
        string setVideoId,
        CancellationToken cancellationToken)
    {
        await PostFireAndForgetAsync("browse/edit_playlist", writer =>
        {
            writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", "ACTION_REMOVE_VIDEO");
            writer.WriteString("removedVideoId", videoId);
            writer.WriteString("setVideoId", setVideoId);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Fetches playlist content with setVideoId for each track.
    /// Uses browse endpoint with browseId="VL"+playlistId.
    /// </summary>
    public async Task<List<PlaylistVideoData>> GetPlaylistVideosAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal)
            ? playlistId
            : "VL" + playlistId;

        var jsonDoc = await PostAsync("browse", writer =>
        {
            writer.WriteString("browseId", browseId);
        }, cancellationToken);

        var results = new List<PlaylistVideoData>();
        ParsePlaylistVideos(jsonDoc, results);

        // Handle continuation for large playlists
        var continuation = ExtractContinuationToken(jsonDoc);
        int page = 0;
        while (!string.IsNullOrEmpty(continuation) && page < 50)
        {
            var contDoc = await PostAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuation);
            }, cancellationToken);

            var prevCount = results.Count;
            ParsePlaylistVideos(contDoc, results);
            if (results.Count == prevCount) break;

            continuation = ExtractContinuationToken(contDoc);
            page++;
        }

        Log.Debug($"[MusicController] Fetched {results.Count} videos with setVideoIds for playlist {playlistId}");
        return results;
    }

    /// <summary>
    /// Parses playlist video items from browse response JSON.
    /// Extracts videoId and setVideoId from musicResponsiveListItemRenderer.
    /// </summary>
    private static void ParsePlaylistVideos(JsonElement root, List<PlaylistVideoData> results)
    {
        // Path 1: Initial browse response
        // contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content
        //   .sectionListRenderer.contents[0].musicPlaylistShelfRenderer.contents[]
        //     .musicResponsiveListItemRenderer.playlistItemData { videoId, playlistSetVideoId }

        // Path 2: Continuation response
        // continuationContents.musicPlaylistShelfContinuation.contents[]

        JsonElement? contentsArray = null;

        // Try initial response path
        var tabs = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("singleColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs?.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.Value.EnumerateArray())
            {
                var sectionContents = tab.GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content")
                    ?.GetPropertyOrNull("sectionListRenderer")
                    ?.GetPropertyOrNull("contents");

                if (sectionContents?.ValueKind == JsonValueKind.Array)
                {
                    foreach (var section in sectionContents.Value.EnumerateArray())
                    {
                        var shelfContents = section.GetPropertyOrNull("musicPlaylistShelfRenderer")
                            ?.GetPropertyOrNull("contents");

                        if (shelfContents?.ValueKind == JsonValueKind.Array)
                        {
                            contentsArray = shelfContents;
                            break;
                        }
                    }
                }
                if (contentsArray != null) break;
            }
        }

        // Try continuation response path
        if (contentsArray == null)
        {
            contentsArray = root.GetPropertyOrNull("continuationContents")
                ?.GetPropertyOrNull("musicPlaylistShelfContinuation")
                ?.GetPropertyOrNull("contents");
        }

        if (contentsArray?.ValueKind != JsonValueKind.Array) return;

        foreach (var item in contentsArray.Value.EnumerateArray())
        {
            var renderer = item.GetPropertyOrNull("musicResponsiveListItemRenderer");
            if (renderer == null) continue;

            var playlistItemData = renderer.Value.GetPropertyOrNull("playlistItemData");
            if (playlistItemData == null) continue;

            var videoId = playlistItemData.Value.GetPropertyOrNull("videoId")?.GetStringOrNull();
            var setVideoId = playlistItemData.Value.GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull();

            if (!string.IsNullOrEmpty(videoId) && !string.IsNullOrEmpty(setVideoId))
            {
                results.Add(new PlaylistVideoData(videoId, setVideoId));
            }
        }
    }

    /// <summary>
    /// Extracts continuation token from browse response.
    /// </summary>
    private static string? ExtractContinuationToken(JsonElement root)
    {
        // Path 1: Initial response continuations
        var continuations = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("singleColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (continuations?.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in continuations.Value.EnumerateArray())
            {
                var sections = tab.GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content")
                    ?.GetPropertyOrNull("sectionListRenderer")
                    ?.GetPropertyOrNull("contents");

                if (sections?.ValueKind != JsonValueKind.Array) continue;

                foreach (var section in sections.Value.EnumerateArray())
                {
                    var shelf = section.GetPropertyOrNull("musicPlaylistShelfRenderer");
                    var contArray = shelf?.GetPropertyOrNull("continuations");
                    if (contArray?.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cont in contArray.Value.EnumerateArray())
                        {
                            var token = cont.GetPropertyOrNull("nextContinuationData")
                                ?.GetPropertyOrNull("continuation")
                                ?.GetStringOrNull();
                            if (!string.IsNullOrEmpty(token)) return token;
                        }
                    }
                }
            }
        }

        // Path 2: Continuation response
        var contCont = root.GetPropertyOrNull("continuationContents")
            ?.GetPropertyOrNull("musicPlaylistShelfContinuation")
            ?.GetPropertyOrNull("continuations");

        if (contCont?.ValueKind == JsonValueKind.Array)
        {
            foreach (var cont in contCont.Value.EnumerateArray())
            {
                var token = cont.GetPropertyOrNull("nextContinuationData")
                    ?.GetPropertyOrNull("continuation")
                    ?.GetStringOrNull();
                if (!string.IsNullOrEmpty(token)) return token;
            }
        }

        return null;
    }

    /// <summary>
    /// Legacy method — kept for backward compatibility. Use AddPlaylistItemAsync instead.
    /// </summary>
    public async Task EditPlaylistItemAsync(
        string playlistId,
        string videoId,
        string action,
        CancellationToken cancellationToken)
    {
        if (action == "ACTION_ADD_VIDEO")
        {
            await AddPlaylistItemAsync(playlistId, videoId, cancellationToken);
        }
        else
        {
            // For removal without setVideoId — log warning
            Log.Warn($"[MusicController] EditPlaylistItemAsync called with {action} without setVideoId. Use RemovePlaylistItemAsync instead.");
            await PostFireAndForgetAsync("browse/edit_playlist", writer =>
            {
                writer.WriteString("playlistId", SanitizePlaylistId(playlistId));

                writer.WriteStartArray("actions");
                writer.WriteStartObject();
                writer.WriteString("action", action);
                writer.WriteString("addedVideoId", videoId);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Убирает "VL" префикс из playlistId если он есть.
    /// YouTube API иногда возвращает ID с "VL" prefix, но ожидает без него.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string SanitizePlaylistId(string playlistId)
    {
        var span = playlistId.AsSpan();
        if (span.StartsWith("VL") && span.Length > 2)
            return playlistId[2..];
        return playlistId;
    }

    #endregion

    #region Account

    public async Task<JsonElement> GetAccountMenuAsync(
        CancellationToken cancellationToken = default)
    {
        return await PostAsync("account/account_menu", _ => { }, cancellationToken);
    }

    #endregion
}

/// <summary>
/// Data for a video item in a YouTube Music playlist.
/// Contains both videoId and setVideoId (unique playlist item identifier).
/// </summary>
public readonly record struct PlaylistVideoData(string VideoId, string SetVideoId);