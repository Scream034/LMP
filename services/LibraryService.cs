using System.Text.Json;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.Services;

public class LibraryService
{
    private const string LibraryFileName = "library.json";
    private readonly string _libraryPath;
    private readonly string _appFolder;

    public LibraryData Data { get; private set; } = new();

    public event Action? OnDataChanged;

    public LibraryService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _appFolder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(_appFolder);
        _libraryPath = Path.Combine(_appFolder, LibraryFileName);

        Load();
    }

    public string AppFolder => _appFolder;

    public string DownloadPath
    {
        get => string.IsNullOrEmpty(Data.DownloadPath)
            ? Path.Combine(_appFolder, "Downloads")
            : Data.DownloadPath;
        set
        {
            Data.DownloadPath = value;
            Save();
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_libraryPath))
            {
                string json = File.ReadAllText(_libraryPath);
                Data = JsonSerializer.Deserialize<LibraryData>(json) ?? new LibraryData();
            }
        }
        catch
        {
            Data = new LibraryData();
        }

        EnsureLikedPlaylist();

        // Убеждаемся что папка загрузок существует
        Directory.CreateDirectory(DownloadPath);
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(Data, options);
            File.WriteAllText(_libraryPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save library: {ex.Message}");
        }
    }

    public void Reset()
    {
        Data = new LibraryData();
        EnsureLikedPlaylist();
        Save();
        OnDataChanged?.Invoke();
    }

    private void EnsureLikedPlaylist()
    {
        if (!Data.Playlists.ContainsKey("liked"))
        {
            Data.Playlists["liked"] = new Playlist
            {
                Id = "liked",
                Name = "Любимое",
                IsLocal = true
            };
        }
    }

    // --- Треки ---

    public void AddOrUpdateTrack(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Id))
            track.Id = GenerateTrackId(track);

        // Синхронизируем состояние лайка
        if (Data.Tracks.TryGetValue(track.Id, out var existing))
        {
            track.IsLiked = existing.IsLiked;
            track.IsDisliked = existing.IsDisliked;
            track.IsDownloaded = existing.IsDownloaded || track.IsDownloaded;
            track.LocalPath = existing.LocalPath ?? track.LocalPath;
            track.InPlaylists = existing.InPlaylists;
        }

        Data.Tracks[track.Id] = track;
        OnDataChanged?.Invoke();
        Save();
    }

    public TrackInfo? GetTrack(string id) =>
        Data.Tracks.TryGetValue(id, out var track) ? track : null;

    public bool HasTrack(string id) => Data.Tracks.ContainsKey(id);

    public void AddToRecentlyPlayed(TrackInfo track)
    {
        AddOrUpdateTrack(track);

        Data.RecentlyPlayedIds.Remove(track.Id);
        Data.RecentlyPlayedIds.Insert(0, track.Id);

        if (Data.RecentlyPlayedIds.Count > 100)
            Data.RecentlyPlayedIds.RemoveRange(100, Data.RecentlyPlayedIds.Count - 100);

        Save();
    }

    public List<TrackInfo> GetRecentlyPlayed(int count = 20)
    {
        return Data.RecentlyPlayedIds
            .Take(count)
            .Select(GetTrack)
            .Where(t => t != null)
            .Cast<TrackInfo>()
            .ToList();
    }

    public void ClearHistory()
    {
        Data.RecentlyPlayedIds.Clear();
        Save();
        OnDataChanged?.Invoke();
    }

    // --- Лайки ---

    public void ToggleLike(TrackInfo track)
    {
        AddOrUpdateTrack(track);

        track.IsLiked = !track.IsLiked;
        track.IsDisliked = false;

        var likedPlaylist = Data.Playlists["liked"];

        if (track.IsLiked)
        {
            if (!likedPlaylist.TrackIds.Contains(track.Id))
            {
                likedPlaylist.TrackIds.Insert(0, track.Id);
                likedPlaylist.UpdatedAt = DateTime.Now;
            }
            track.InPlaylists.Add("liked");
        }
        else
        {
            likedPlaylist.TrackIds.Remove(track.Id);
            likedPlaylist.UpdatedAt = DateTime.Now;
            track.InPlaylists.Remove("liked");
        }

        Data.Tracks[track.Id] = track;
        Save();
        OnDataChanged?.Invoke();
    }

    public void ToggleDislike(TrackInfo track)
    {
        AddOrUpdateTrack(track);

        track.IsDisliked = !track.IsDisliked;

        if (track.IsDisliked)
        {
            track.IsLiked = false;
            Data.Playlists["liked"].TrackIds.Remove(track.Id);
            track.InPlaylists.Remove("liked");
        }

        Data.Tracks[track.Id] = track;
        Save();
        OnDataChanged?.Invoke();
    }

    // --- Плейлисты ---

    public Playlist CreatePlaylist(string name)
    {
        var playlist = new Playlist
        {
            Name = name,
            IsLocal = true
        };
        Data.Playlists[playlist.Id] = playlist;
        Save();
        OnDataChanged?.Invoke();
        return playlist;
    }

    public void RenamePlaylist(string playlistId, string newName)
    {
        if (playlistId == "liked") return;

        if (Data.Playlists.TryGetValue(playlistId, out var playlist))
        {
            playlist.Name = newName;
            playlist.UpdatedAt = DateTime.Now;
            Save();
            OnDataChanged?.Invoke();
        }
    }

    public void DeletePlaylist(string playlistId)
    {
        if (playlistId == "liked") return;

        if (Data.Playlists.Remove(playlistId))
        {
            foreach (var track in Data.Tracks.Values)
                track.InPlaylists.Remove(playlistId);

            Save();
            OnDataChanged?.Invoke();
        }
    }

    public void AddTrackToPlaylist(TrackInfo track, string playlistId)
    {
        AddOrUpdateTrack(track);

        if (!Data.Playlists.TryGetValue(playlistId, out var playlist))
            return;

        if (!playlist.TrackIds.Contains(track.Id))
        {
            playlist.TrackIds.Add(track.Id);
            track.InPlaylists.Add(playlistId);
            playlist.UpdatedAt = DateTime.Now;

            Data.Tracks[track.Id] = track;
            Save();
            OnDataChanged?.Invoke();
        }
    }

    public void RemoveTrackFromPlaylist(TrackInfo track, string playlistId)
    {
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist))
            return;

        playlist.TrackIds.Remove(track.Id);
        track.InPlaylists.Remove(playlistId);
        playlist.UpdatedAt = DateTime.Now;

        if (Data.Tracks.ContainsKey(track.Id))
            Data.Tracks[track.Id] = track;

        Save();
        OnDataChanged?.Invoke();
    }

    public bool IsTrackInPlaylist(string trackId, string playlistId)
    {
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist))
            return false;

        return playlist.TrackIds.Contains(trackId);
    }

    public List<TrackInfo> GetPlaylistTracks(string playlistId)
    {
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist))
            return new List<TrackInfo>();

        return playlist.TrackIds
            .Select(GetTrack)
            .Where(t => t != null)
            .Cast<TrackInfo>()
            .ToList();
    }

    public IEnumerable<Playlist> GetAllPlaylists() => Data.Playlists.Values;

    public Playlist? GetPlaylist(string playlistId) =>
        Data.Playlists.TryGetValue(playlistId, out var playlist) ? playlist : null;

    public void MergeAccountPlaylists(IEnumerable<Playlist> accountPlaylists)
    {
        foreach (var playlist in accountPlaylists)
        {
            if (!Data.Playlists.ContainsKey(playlist.Id))
            {
                Data.Playlists[playlist.Id] = playlist;
            }
        }

        Save();
        OnDataChanged?.Invoke();
    }

    private static string GenerateTrackId(TrackInfo track)
    {
        if (!string.IsNullOrEmpty(track.Url))
        {
            try
            {
                var uri = new Uri(track.Url);
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var videoId = query["v"];
                if (!string.IsNullOrEmpty(videoId))
                    return $"yt_{videoId}";
            }
            catch { }
        }

        return $"local_{Guid.NewGuid():N}";
    }
}