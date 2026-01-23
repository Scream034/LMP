using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyLiteMusicPlayer.Core.Models;

namespace MyLiteMusicPlayer.Core.Services;

public partial class YoutubeUserDataService
{
    private readonly GoogleAuthService _auth;
    private readonly HttpClient _http;

    private const string BaseUrl = "https://www.googleapis.com/youtube/v3";

    public YoutubeUserDataService(GoogleAuthService auth)
    {
        _auth = auth;
        _http = new HttpClient();
    }

    private async Task<HttpClient> GetClientAsync()
    {
        var token = await _auth.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new UnauthorizedAccessException("User is not authenticated");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http;
    }

    // --- ЛАЙКИ И ТРЕКИ ---

    public async Task RateVideoAsync(string videoId, string rating)
    {
        var client = await GetClientAsync();
        var cleanId = videoId.Replace("yt_", "");
        var response = await client.PostAsync($"{BaseUrl}/videos/rate?id={cleanId}&rating={rating}", null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Получает список треков из плейлиста "Понравившиеся" (LL) С ДЛИТЕЛЬНОСТЬЮ.
    /// </summary>
    public async Task<List<TrackInfo>> GetLikedTracksAsync()
    {
        var client = await GetClientAsync();
        var result = new List<TrackInfo>();
        string? nextPageToken = null;
        int limit = 200;

        do
        {
            var url = $"{BaseUrl}/playlistItems?part=snippet,contentDetails&playlistId=LL&maxResults=50";
            if (nextPageToken != null) url += $"&pageToken={nextPageToken}";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) break;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Безопасное получение корневого элемента
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                var batchTracks = new List<TrackInfo>();
                var videoIds = new List<string>();

                foreach (var item in items.EnumerateArray())
                {
                    try
                    {
                        // Safe parsing logic to avoid KeyNotFoundException
                        if (!item.TryGetProperty("snippet", out var snippet)) continue;

                        // Check resourceId existence
                        if (!snippet.TryGetProperty("resourceId", out var resourceIdEl) ||
                            !resourceIdEl.TryGetProperty("videoId", out var videoIdEl))
                            continue;

                        var videoId = videoIdEl.GetString();
                        if (string.IsNullOrEmpty(videoId)) continue;

                        // Safe title
                        var title = snippet.TryGetProperty("title", out var titleEl) 
                            ? titleEl.GetString() ?? "Unknown" 
                            : "Unknown";
                        
                        // Filter out Deleted/Private videos
                        if (title == "Deleted video" || title == "Private video") continue;

                        // Safe author
                        string author = "Unknown";
                        if (snippet.TryGetProperty("videoOwnerChannelTitle", out var ownerEl))
                        {
                            author = ownerEl.GetString() ?? "Unknown";
                        }
                        else if (snippet.TryGetProperty("channelTitle", out var channelEl))
                        {
                            author = channelEl.GetString() ?? "Unknown";
                        }

                        string thumbUrl = ParseBestThumbnail(snippet);

                        var track = new TrackInfo
                        {
                            Id = $"yt_{videoId}",
                            Title = title,
                            Author = author,
                            ThumbnailUrl = thumbUrl,
                            IsLiked = true
                        };

                        batchTracks.Add(track);
                        videoIds.Add(videoId);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[Sync] Error parsing item: {ex.Message}");
                    }
                }

                // Загружаем Duration батчем
                if (videoIds.Count > 0)
                {
                    var durations = await FetchVideoDurationsAsync(client, videoIds);
                    
                    foreach (var track in batchTracks)
                    {
                        var cleanId = track.Id.Replace("yt_", "");
                        if (durations.TryGetValue(cleanId, out var duration))
                        {
                            track.Duration = duration;
                        }
                    }
                }

                result.AddRange(batchTracks);
            }

            // Безопасное получение токена следующей страницы
            nextPageToken = doc.RootElement.TryGetProperty("nextPageToken", out var token) 
                ? token.GetString() 
                : null;

        } while (nextPageToken != null && result.Count < limit);

        return result;
    }

    /// <summary>
    /// Батчевый запрос длительности видео (до 50 за раз)
    /// </summary>
    private async Task<Dictionary<string, TimeSpan>> FetchVideoDurationsAsync(
        HttpClient client, 
        IEnumerable<string> videoIds)
    {
        var result = new Dictionary<string, TimeSpan>();
        
        // YouTube API позволяет до 50 ID за запрос
        var idList = string.Join(",", videoIds.Take(50));
        if (string.IsNullOrEmpty(idList)) return result;

        try
        {
            var url = $"{BaseUrl}/videos?part=contentDetails&id={idList}";
            var response = await client.GetAsync(url);
            
            if (!response.IsSuccessStatusCode) return result;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    try
                    {
                        if (!item.TryGetProperty("id", out var idEl)) continue;
                        var id = idEl.GetString();

                        if (item.TryGetProperty("contentDetails", out var cd) &&
                            cd.TryGetProperty("duration", out var durEl))
                        {
                            var durationStr = durEl.GetString();
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(durationStr))
                            {
                                result[id] = ParseIso8601Duration(durationStr);
                            }
                        }
                    }
                    catch { /* skip */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to fetch durations: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Парсинг ISO 8601 Duration (PT4M13S -> TimeSpan)
    /// </summary>
    private static TimeSpan ParseIso8601Duration(string iso8601)
    {
        // Формат: PT#H#M#S или PT#M#S или PT#S
        var match = Iso8601Regex().Match(iso8601);
        if (!match.Success) return TimeSpan.Zero;

        int hours = match.Groups["hours"].Success ? int.Parse(match.Groups["hours"].Value) : 0;
        int minutes = match.Groups["minutes"].Success ? int.Parse(match.Groups["minutes"].Value) : 0;
        int seconds = match.Groups["seconds"].Success ? int.Parse(match.Groups["seconds"].Value) : 0;

        return new TimeSpan(hours, minutes, seconds);
    }

    [GeneratedRegex(@"PT((?<hours>\d+)H)?((?<minutes>\d+)M)?((?<seconds>\d+)S)?", RegexOptions.Compiled)]
    private static partial Regex Iso8601Regex();

    private static string ParseBestThumbnail(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbs)) return "";

        if (thumbs.TryGetProperty("maxres", out var max)) return max.GetProperty("url").GetString() ?? "";
        if (thumbs.TryGetProperty("high", out var high)) return high.GetProperty("url").GetString() ?? "";
        if (thumbs.TryGetProperty("medium", out var med)) return med.GetProperty("url").GetString() ?? "";
        if (thumbs.TryGetProperty("default", out var def)) return def.GetProperty("url").GetString() ?? "";

        return "";
    }

    // --- ПЛЕЙЛИСТЫ (без изменений, но добавим загрузку Duration) ---

    public async Task<string> CreatePlaylistAsync(string title, string description = "")
    {
        var client = await GetClientAsync();

        var body = new
        {
            snippet = new { title, description },
            status = new { privacyStatus = "private" }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{BaseUrl}/playlists?part=snippet,status", content);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(json);
        return node?["id"]?.GetValue<string>() ?? throw new Exception("Failed to get new playlist ID");
    }

    public async Task DeletePlaylistAsync(string youtubePlaylistId)
    {
        var client = await GetClientAsync();
        var response = await client.DeleteAsync($"{BaseUrl}/playlists?id={youtubePlaylistId}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> AddTrackToPlaylistAsync(string youtubePlaylistId, string videoId)
    {
        var client = await GetClientAsync();
        var cleanVideoId = videoId.Replace("yt_", "");

        var body = new
        {
            snippet = new
            {
                playlistId = youtubePlaylistId,
                resourceId = new { kind = "youtube#video", videoId = cleanVideoId }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{BaseUrl}/playlistItems?part=snippet", content);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(json);
        return node?["id"]?.GetValue<string>() ?? "";
    }

    public async Task<List<Playlist>> GetMyPlaylistsAsync()
    {
        var client = await GetClientAsync();
        var response = await client.GetAsync($"{BaseUrl}/playlists?part=snippet,contentDetails&mine=true&maxResults=50");

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync();
        var result = new List<Playlist>();

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("snippet", out var snippet)) continue;
                    
                    var title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? "Unknown" : "Unknown";
                    var channel = snippet.TryGetProperty("channelTitle", out var c) ? c.GetString() : "Unknown";
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() : "";

                    if (string.IsNullOrEmpty(id)) continue;

                    string thumbUrl = ParseBestThumbnail(snippet);

                    result.Add(new Playlist
                    {
                        YoutubeId = id,
                        Name = title,
                        Author = channel,
                        SyncMode = PlaylistSyncMode.TwoWaySync,
                        ThumbnailUrl = thumbUrl
                    });
                }
                catch { /* skip */ }
            }
        }
        return result;
    }
}

