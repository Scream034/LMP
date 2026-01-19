using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using MyLiteMusicPlayer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MyLiteMusicPlayer.Services;

public class YoutubeProvider
{
    private readonly YoutubeDL _ytdl;
    private readonly HttpClient _http;
    private readonly GoogleAuthService _auth;
    
    public bool IsReady { get; private set; }

    private static readonly Regex YoutubeVideoRegex = new(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex YoutubePlaylistRegex = new(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    public YoutubeProvider(GoogleAuthService auth)
    {
        _auth = auth;
        _http = new HttpClient();
        
        string downloadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiteMusicPlayer", "Downloads");
        
        _ytdl = new YoutubeDL
        {
            YoutubeDLPath = "yt-dlp.exe",
            FFmpegPath = "ffmpeg.exe",
            OutputFolder = downloadPath
        };
        
        Directory.CreateDirectory(_ytdl.OutputFolder);
    }

    public async Task InitializeAsync()
    {
        if (!File.Exists(_ytdl.YoutubeDLPath))
        {
            Console.WriteLine("Warning: yt-dlp.exe not found!");
        }
        IsReady = true;
        await Task.CompletedTask;
    }

    public QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return QueryType.None;
        
        query = query.Trim();
        
        if (YoutubePlaylistRegex.IsMatch(query))
            return QueryType.Playlist;
        
        if (YoutubeVideoRegex.IsMatch(query) || 
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return QueryType.DirectUrl;
        
        return QueryType.Search;
    }

    public string? ExtractVideoId(string url)
    {
        var match = YoutubeVideoRegex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady) return null;
        
        try
        {
            var res = await _ytdl.RunVideoDataFetch(url);
            if (!res.Success || res.Data == null) return null;
            
            return ConvertToTrackInfo(res.Data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetTrackByUrl error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 20)
    {
        if (!IsReady) return new List<TrackInfo>();
        
        try
        {
            string searchParam = $"ytsearch{maxResults}:{query}";
            
            var res = await _ytdl.RunVideoDataFetch(searchParam);
            if (!res.Success || res.Data == null) 
                return new List<TrackInfo>();

            var results = new List<TrackInfo>();
            
            if (res.Data.Entries != null)
            {
                foreach (var entry in res.Data.Entries)
                {
                    var track = ConvertToTrackInfo(entry);
                    if (track != null)
                        results.Add(track);
                }
            }
            else
            {
                var track = ConvertToTrackInfo(res.Data);
                if (track != null)
                    results.Add(track);
            }
            
            return results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search error: {ex.Message}");
            return new List<TrackInfo>();
        }
    }

    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;
        
        try
        {
            var res = await _ytdl.RunVideoDataFetch(url);
            if (!res.Success || res.Data == null) return null;

            string playlistName = res.Data.Title ?? "Unknown Playlist";
            var tracks = new List<TrackInfo>();

            if (res.Data.Entries != null)
            {
                foreach (var entry in res.Data.Entries)
                {
                    var track = ConvertToTrackInfo(entry);
                    if (track != null)
                        tracks.Add(track);
                }
            }

            return (playlistName, tracks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetPlaylist error: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url)) 
            return new List<TrackInfo>();
        
        try
        {
            string? videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) 
                return new List<TrackInfo>();
            
            string mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";
            
            var result = await GetPlaylistAsync(mixUrl);
            var tracks = result?.Tracks.Take(count).ToList() ?? new List<TrackInfo>();
            
            foreach (var track in tracks)
            {
                track.RadioSeedId = sourceTrack.Id;
            }
            
            return tracks;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetRadio error: {ex.Message}");
            return new List<TrackInfo>();
        }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        if (!IsReady) return new List<TrackInfo>();
        
        try
        {
            string trendingUrl = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            
            var result = await GetPlaylistAsync(trendingUrl);
            return result?.Tracks.Take(count).ToList() ?? new List<TrackInfo>();
        }
        catch
        {
            return await SearchAsync("top music 2024", count);
        }
    }

    public async Task<List<Playlist>> GetUserPlaylistsAsync()
    {
        var token = await _auth.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return new List<Playlist>();
        
        try
        {
            _http.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _http.GetAsync(
                "https://www.googleapis.com/youtube/v3/playlists?part=snippet,contentDetails&mine=true&maxResults=50");
            
            if (!response.IsSuccessStatusCode)
                return new List<Playlist>();
            
            string json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            
            var playlists = new List<Playlist>();
            
            if (data.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var snippet = item.GetProperty("snippet");
                    
                    playlists.Add(new Playlist
                    {
                        Id = $"yt_{item.GetProperty("id").GetString()}",
                        YoutubePlaylistId = item.GetProperty("id").GetString(),
                        Name = snippet.GetProperty("title").GetString() ?? "Unknown",
                        ThumbnailUrl = snippet.TryGetProperty("thumbnails", out var thumbs) &&
                                       thumbs.TryGetProperty("default", out var defThumb)
                            ? defThumb.GetProperty("url").GetString()
                            : null,
                        IsLocal = false,
                        IsFromAccount = true
                    });
                }
            }
            
            return playlists;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetUserPlaylists error: {ex.Message}");
            return new List<Playlist>();
        }
    }

    public async Task<List<TrackInfo>> GetPersonalRecommendationsAsync(int count = 20)
    {
        var token = await _auth.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return new List<TrackInfo>();
        
        try
        {
            _http.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", token);
            
            var response = await _http.GetAsync(
                "https://www.googleapis.com/youtube/v3/videos?part=snippet&myRating=like&maxResults=10");
            
            if (!response.IsSuccessStatusCode)
                return await GetTrendingAsync(count);
            
            string json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            
            var tracks = new List<TrackInfo>();
            
            if (data.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var videoId = item.GetProperty("id").GetString();
                    if (string.IsNullOrEmpty(videoId)) continue;
                    
                    var track = await GetTrackByUrlAsync($"https://youtube.com/watch?v={videoId}");
                    if (track != null)
                        tracks.Add(track);
                }
            }
            
            if (tracks.Count > 0 && tracks.Count < count)
            {
                var radioTracks = await GetRadioAsync(tracks[0], count - tracks.Count);
                tracks.AddRange(radioTracks);
            }
            
            return tracks.Take(count).ToList();
        }
        catch
        {
            return await GetTrendingAsync(count);
        }
    }

    public async Task<string?> DownloadTrackAsync(TrackInfo track, IProgress<float>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url)) return null;

        try
        {
            var progressHandler = progress != null 
                ? new Progress<DownloadProgress>(p => progress.Report((float)p.Progress)) 
                : null;

            var res = await _ytdl.RunAudioDownload(
                track.Url, 
                AudioConversionFormat.Mp3, 
                progress: progressHandler,
                ct: cancellationToken);
            
            return res.Success ? res.Data : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Download error: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> RefreshStreamUrlAsync(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Url)) return null;
        
        var freshTrack = await GetTrackByUrlAsync(track.Url);
        if (freshTrack != null)
        {
            track.StreamUrl = freshTrack.StreamUrl;
            return freshTrack.StreamUrl;
        }
        
        return null;
    }

    private TrackInfo? ConvertToTrackInfo(YoutubeDLSharp.Metadata.VideoData data)
    {
        if (data == null) return null;
        
        string bestStream = string.Empty;
        
        if (data.Formats != null)
        {
            var audioFormat = data.Formats
                .Where(f => f.AudioBitrate != null && f.AudioBitrate > 0)
                .OrderByDescending(f => f.AudioBitrate)
                .FirstOrDefault();
            
            bestStream = audioFormat?.Url ?? data.Url ?? string.Empty;
        }

        string videoId = data.ID ?? "";
        
        return new TrackInfo
        {
            Id = !string.IsNullOrEmpty(videoId) ? $"yt_{videoId}" : $"yt_{Guid.NewGuid():N}",
            Title = data.Title ?? "Unknown Title",
            Author = data.Uploader ?? data.Channel ?? "Unknown Artist",
            Url = data.WebpageUrl ?? $"https://youtube.com/watch?v={videoId}",
            StreamUrl = bestStream,
            Duration = data.Duration != null ? TimeSpan.FromSeconds((double)data.Duration) : TimeSpan.Zero,
            ThumbnailUrl = data.Thumbnail ?? string.Empty
        };
    }
}