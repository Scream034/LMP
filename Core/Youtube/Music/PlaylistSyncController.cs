// Core/Youtube/Music/PlaylistSyncController.cs

using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Music;

/// <summary>
/// Reads user playlists and playlist tracks for synchronization.
/// Uses WEB client for playlist listing, WEB_REMIX for track details.
/// </summary>
internal sealed class PlaylistSyncController(HttpClient http)
{
    private const string WebApiUrl = "https://www.youtube.com/youtubei/v1";
    private const string MusicApiUrl = "https://music.youtube.com/youtubei/v1";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");

    // WEB context bytes
    private static readonly byte[] Utf8Context = "context"u8.ToArray();
    private static readonly byte[] Utf8Client = "client"u8.ToArray();
    private static readonly byte[] Utf8ClientName = "clientName"u8.ToArray();
    private static readonly byte[] Utf8ClientVersion = "clientVersion"u8.ToArray();
    private static readonly byte[] Utf8Request = "request"u8.ToArray();
    private static readonly byte[] Utf8UseSsl = "useSsl"u8.ToArray();
    private static readonly byte[] Utf8Web = "WEB"u8.ToArray();
    private static readonly byte[] Utf8WebRemix = "WEB_REMIX"u8.ToArray();
    private static readonly byte[] Utf8Hl = "hl"u8.ToArray();
    private static readonly byte[] Utf8Gl = "gl"u8.ToArray();
    private static readonly byte[] Utf8User = "user"u8.ToArray();

    public string VisitorData { get; set; } = "";

    #region Context Writers

    /// <summary>
    /// Minimal WEB context for playlist listing.
    /// </summary>
    private static void WriteWebContext(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(Utf8Context);
        writer.WriteStartObject();

        writer.WritePropertyName(Utf8Client);
        writer.WriteStartObject();
        writer.WriteString(Utf8ClientName, Utf8Web);
        writer.WriteString(Utf8ClientVersion, YoutubeHttpHandler.WebClientVersion);
        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utf8Request);
        writer.WriteStartObject();
        writer.WriteBoolean(Utf8UseSsl, false);
        writer.WriteEndObject(); // request

        writer.WriteEndObject(); // context
    }

    /// <summary>
    /// WEB_REMIX context for playlist track fetching (musicResponsiveListItemRenderer).
    /// </summary>
    private void WriteMusicContext(Utf8JsonWriter writer)
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
            writer.WriteString("visitorData", VisitorData);

        writer.WriteEndObject(); // client

        writer.WritePropertyName(Utf8User);
        writer.WriteStartObject();
        writer.WriteEndObject(); // user

        writer.WriteEndObject(); // context
    }

    #endregion

    #region HTTP

    private static HttpContent CreateJsonContent(Action<Utf8JsonWriter> writeBody)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }

        var content = new ByteArrayContent(bufferWriter.WrittenSpan.ToArray());
        content.Headers.ContentType = JsonContentType;
        return content;
    }

    private async Task<JsonElement> PostWebAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WebApiUrl}/{endpoint}");
        request.Content = CreateJsonContent(writer =>
        {
            WriteWebContext(writer);
            writeBody(writer);
        });

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await Json.ParseAsync(stream, ct);
    }

    private async Task<JsonElement> PostMusicAsync(
        string endpoint,
        Action<Utf8JsonWriter> writeBody,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MusicApiUrl}/{endpoint}");

        if (!string.IsNullOrEmpty(VisitorData))
            request.Options.Set(YoutubeHttpHandler.VisitorDataKey, VisitorData);

        request.Content = CreateJsonContent(writer =>
        {
            WriteMusicContext(writer);
            writeBody(writer);
        });

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var root = await Json.ParseAsync(stream, ct);

        // Update visitor data from response
        var newVd = root.GetPropertyOrNull("responseContext")
            ?.GetPropertyOrNull("visitorData")
            ?.GetStringOrNull();
        if (!string.IsNullOrWhiteSpace(newVd) && newVd != VisitorData)
            VisitorData = newVd;

        return root;
    }

    #endregion

    #region User Playlists (WEB client)

    /// <summary>
    /// Gets all user playlists via WEB client with pagination.
    /// Uses FEplaylist_aggregation with "qAIC" filter (Music playlists).
    /// Parses lockupViewModel for compact data.
    /// </summary>
    public async IAsyncEnumerable<List<RemotePlaylistInfo>> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = await PostWebAsync("browse", writer =>
        {
            writer.WriteString("browseId", "FEplaylist_aggregation");
            writer.WriteString("params", "qAIC");
        }, ct);

        var batch = ParseLockupViewModels(root);
        if (batch.Count > 0)
            yield return batch;

        // Handle continuation
        var continuation = ExtractWebContinuationToken(root);
        int page = 0;

        while (!string.IsNullOrEmpty(continuation) && page < 50)
        {
            ct.ThrowIfCancellationRequested();

            var contRoot = await PostWebAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuation);
            }, ct);

            var contBatch = ParseLockupViewModelsFromContinuation(contRoot);
            if (contBatch.Count == 0) break;

            yield return contBatch;

            continuation = ExtractWebContinuationToken(contRoot);
            page++;
        }
    }

    private static List<RemotePlaylistInfo> ParseLockupViewModels(JsonElement root)
    {
        var results = new List<RemotePlaylistInfo>(32);

        // Navigate to items in the grid/list
        var lockups = new List<JsonElement>(32);
        root.EnumerateDescendantProperties("lockupViewModel", lockups);

        for (int i = 0; i < lockups.Count; i++)
        {
            var lockup = lockups[i];
            var info = ParseSingleLockup(lockup);
            if (info.HasValue)
                results.Add(info.Value);
        }

        return results;
    }

    private static List<RemotePlaylistInfo> ParseLockupViewModelsFromContinuation(JsonElement root)
    {
        var results = new List<RemotePlaylistInfo>(32);

        // Continuation: onResponseReceivedCommands[].appendContinuationItemsAction.continuationItems[]
        var commands = root.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands?.ValueKind != JsonValueKind.Array) return results;

        foreach (var cmd in commands.Value.EnumerateArray())
        {
            var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                ?.GetPropertyOrNull("continuationItems");
            if (items?.ValueKind != JsonValueKind.Array) continue;

            var lockups = new List<JsonElement>(32);
            items.Value.EnumerateDescendantProperties("lockupViewModel", lockups);

            for (int i = 0; i < lockups.Count; i++)
            {
                var info = ParseSingleLockup(lockups[i]);
                if (info.HasValue)
                    results.Add(info.Value);
            }
        }

        return results;
    }

    private static RemotePlaylistInfo? ParseSingleLockup(JsonElement lockup)
    {
        var contentId = lockup.GetPropertyOrNull("contentId")?.GetStringOrNull();
        if (string.IsNullOrEmpty(contentId)) return null;

        var title = lockup.GetPropertyOrNull("metadata")
            ?.GetPropertyOrNull("lockupMetadataViewModel")
            ?.GetPropertyOrNull("title")
            ?.GetPropertyOrNull("content")?.GetStringOrNull()
            ?? "";

        // Track count from thumbnail badge
        var badgeText = lockup.GetPropertyOrNull("contentImage")
            ?.GetPropertyOrNull("collectionThumbnailViewModel")
            ?.GetPropertyOrNull("primaryThumbnail")
            ?.GetPropertyOrNull("thumbnailViewModel")
            ?.GetPropertyOrNull("overlays")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("thumbnailOverlayBadgeViewModel")
            ?.GetPropertyOrNull("thumbnailBadges")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("thumbnailBadgeViewModel")
            ?.GetPropertyOrNull("text")?.GetStringOrNull();

        int trackCount = ParseTrackCount(badgeText);

        // Thumbnail URL
        string? thumbUrl = lockup.GetPropertyOrNull("contentImage")
            ?.GetPropertyOrNull("collectionThumbnailViewModel")
            ?.GetPropertyOrNull("primaryThumbnail")
            ?.GetPropertyOrNull("thumbnailViewModel")
            ?.GetPropertyOrNull("image")
            ?.GetPropertyOrNull("sources")
            ?.GetFirstArrayElementOrNull()
            ?.GetPropertyOrNull("url")?.GetStringOrNull();

        return new RemotePlaylistInfo(contentId, title, trackCount, thumbUrl);
    }

    private static string? ExtractWebContinuationToken(JsonElement root)
    {
        // Initial response: deep search for continuationCommand.token
        // Continuation response: onResponseReceivedCommands
        var commands = root.GetPropertyOrNull("onResponseReceivedCommands");
        if (commands?.ValueKind == JsonValueKind.Array)
        {
            foreach (var cmd in commands.Value.EnumerateArray())
            {
                var items = cmd.GetPropertyOrNull("appendContinuationItemsAction")
                    ?.GetPropertyOrNull("continuationItems");
                if (items?.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in items.Value.EnumerateArray())
                {
                    var token = item.GetPropertyOrNull("continuationItemRenderer")
                        ?.GetPropertyOrNull("continuationEndpoint")
                        ?.GetPropertyOrNull("continuationCommand")
                        ?.GetPropertyOrNull("token")?.GetStringOrNull();
                    if (!string.IsNullOrEmpty(token)) return token;
                }
            }
        }

        // Initial response: search in contents tree
        var found = root.FindFirstDescendantProperty("continuationCommand");
        return found?.GetPropertyOrNull("token")?.GetStringOrNull();
    }

    /// <summary>
    /// Parses track count from localized strings like "4 видео", "5,000 videos", "Нет видео".
    /// Handles all Unicode digit systems via char.IsDigit().
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ParseTrackCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int result = 0;
        bool foundDigit = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsDigit(c))
            {
                result = result * 10 + (c - '0');
                foundDigit = true;
            }
            else if (foundDigit && c != ',' && c != '.' && c != ' ' && c != '\u00A0')
            {
                // Stop at first non-digit, non-separator after digits started
                break;
            }
        }

        return result;
    }

    #endregion

    #region Playlist Tracks (WEB_REMIX client)

    /// <summary>
    /// Fetches all tracks from a playlist with setVideoId for sync/removal.
    /// Uses WEB_REMIX client (musicResponsiveListItemRenderer).
    /// </summary>
    public async Task<List<RemoteTrackInfo>> GetPlaylistTracksAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal)
            ? playlistId
            : "VL" + playlistId;

        var root = await PostMusicAsync("browse", writer =>
        {
            writer.WriteString("browseId", browseId);
        }, ct);

        var results = new List<RemoteTrackInfo>(64);
        ParsePlaylistTracks(root, results);

        // Handle continuation
        var continuation = ExtractMusicContinuationToken(root);
        int page = 0;

        while (!string.IsNullOrEmpty(continuation) && page < 50)
        {
            ct.ThrowIfCancellationRequested();

            var contRoot = await PostMusicAsync("browse", writer =>
            {
                writer.WriteString("continuation", continuation);
            }, ct);

            int prevCount = results.Count;
            ParsePlaylistTracks(contRoot, results);
            if (results.Count == prevCount) break;

            continuation = ExtractMusicContinuationToken(contRoot);
            page++;
        }

        Log.Debug($"[PlaylistSync] Fetched {results.Count} tracks for playlist {playlistId}");
        return results;
    }

    private static void ParsePlaylistTracks(JsonElement root, List<RemoteTrackInfo> results)
    {
        JsonElement? contentsArray = null;

        // Path 1: Initial browse response
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

                if (sectionContents?.ValueKind != JsonValueKind.Array) continue;

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
                if (contentsArray != null) break;
            }
        }

        // Path 2: Continuation response
        contentsArray ??= root.GetPropertyOrNull("continuationContents")
            ?.GetPropertyOrNull("musicPlaylistShelfContinuation")
            ?.GetPropertyOrNull("contents");

        if (contentsArray?.ValueKind != JsonValueKind.Array) return;

        foreach (var item in contentsArray.Value.EnumerateArray())
        {
            var renderer = item.GetPropertyOrNull("musicResponsiveListItemRenderer");
            if (renderer == null) continue;

            var playlistItemData = renderer.Value.GetPropertyOrNull("playlistItemData");
            if (playlistItemData == null) continue;

            var videoId = playlistItemData.Value.GetPropertyOrNull("videoId")?.GetStringOrNull();
            var setVideoId = playlistItemData.Value
                .GetPropertyOrNull("playlistSetVideoId")?.GetStringOrNull();

            if (!string.IsNullOrEmpty(videoId) && !string.IsNullOrEmpty(setVideoId))
            {
                results.Add(new RemoteTrackInfo(videoId, setVideoId, results.Count));
            }
        }
    }

    private static string? ExtractMusicContinuationToken(JsonElement root)
    {
        // Path 1: Initial response
        var tabs = root.GetPropertyOrNull("contents")
            ?.GetPropertyOrNull("singleColumnBrowseResultsRenderer")
            ?.GetPropertyOrNull("tabs");

        if (tabs?.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.Value.EnumerateArray())
            {
                var sections = tab.GetPropertyOrNull("tabRenderer")
                    ?.GetPropertyOrNull("content")
                    ?.GetPropertyOrNull("sectionListRenderer")
                    ?.GetPropertyOrNull("contents");

                if (sections?.ValueKind != JsonValueKind.Array) continue;

                foreach (var section in sections.Value.EnumerateArray())
                {
                    var contArray = section.GetPropertyOrNull("musicPlaylistShelfRenderer")
                        ?.GetPropertyOrNull("continuations");
                    if (contArray?.ValueKind != JsonValueKind.Array) continue;

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

    #endregion
}

/// <summary>
/// Compact playlist info from WEB client lockupViewModel.
/// </summary>
public readonly record struct RemotePlaylistInfo(
    string PlaylistId,
    string Title,
    int TrackCount,
    string? ThumbnailUrl);

/// <summary>
/// Track info with setVideoId for sync/removal operations.
/// </summary>
public readonly record struct RemoteTrackInfo(
    string VideoId,
    string SetVideoId,
    int Position);