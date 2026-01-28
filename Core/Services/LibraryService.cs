using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

public class LibraryService : IDisposable
{
    public const string LikedPlaylistId = "liked";

    private readonly Subject<Unit> _saveSignal = new();
    private readonly IDisposable _saveSubscription;

    public LibraryData Data { get; private set; } = new();

    // --- Fake Account кэш (в памяти, не сохраняется) ---
    private string? _fakeAccountName;
    private string? _fakeAccountAvatarUrl;

    public event Action? OnDataChanged;
    public event Action? OnFakeAccountChanged;
    public event Action<TrackInfo>? OnTrackUpdated;

    public LibraryService()
    {
        LocalizationService.Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        _saveSubscription = _saveSignal
            .Throttle(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Subscribe(async _ => await SaveInternalAsync());

        Load();
    }

    public string DownloadPath
    {
        get => string.IsNullOrEmpty(Data.DownloadPath)
            ? G.Folder.Downloads
            : Data.DownloadPath;
        set
        {
            Data.DownloadPath = value;
            Save();
        }
    }

    // --- Fake Account API ---

    public bool HasFakeAccount => !string.IsNullOrEmpty(Data.FakeAccountChannelUrl);
    public string? FakeAccountUrl => Data.FakeAccountChannelUrl;
    public string? FakeAccountName => _fakeAccountName;
    public string? FakeAccountAvatarUrl => _fakeAccountAvatarUrl;

    /// <summary>
    /// Устанавливает Fake Account (только URL сохраняется, остальное в кэше)
    /// </summary>
    public void SetFakeAccount(string channelUrl, string name, string avatarUrl)
    {
        Data.FakeAccountChannelUrl = channelUrl;
        _fakeAccountName = name;
        _fakeAccountAvatarUrl = avatarUrl;
        Save();
        OnFakeAccountChanged?.Invoke();
        OnDataChanged?.Invoke();
    }

    /// <summary>
    /// Обновляет кэш Fake Account (без сохранения URL)
    /// </summary>
    public void UpdateFakeAccountCache(string name, string avatarUrl)
    {
        _fakeAccountName = name;
        _fakeAccountAvatarUrl = avatarUrl;
        OnFakeAccountChanged?.Invoke();
    }

    /// <summary>
    /// Очищает Fake Account
    /// </summary>
    public void ClearFakeAccount()
    {
        Data.FakeAccountChannelUrl = null;
        _fakeAccountName = null;
        _fakeAccountAvatarUrl = null;
        Save();
        OnFakeAccountChanged?.Invoke();
        OnDataChanged?.Invoke();
    }

    // --- Загрузка/Сохранение ---

    public void Load()
    {
        try
        {
            if (File.Exists(G.File.Library))
            {
                using var fs = new FileStream(G.File.Library, FileMode.Open, FileAccess.Read, FileShare.Read);
                Data = JsonSerializer.Deserialize<LibraryData>(fs) ?? new LibraryData();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load library: {ex.Message}");
            Data = new LibraryData();
        }
        EnsureLikedPlaylist();
    }

    public void Save()
    {
        _saveSignal.OnNext(Unit.Default);
    }

    private async Task SaveInternalAsync()
    {
        try
        {
            var tempFile = G.File.Library + ".tmp";
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(fs, Data, G.Json.Beautiful);
            }
            File.Move(tempFile, G.File.Library, true);
        }
        catch (Exception ex)
        {
            Log.Error($"[LibraryService] Async save failed: {ex.Message}");
        }
    }

    public void Reset()
    {
        Data = new LibraryData();
        _fakeAccountName = null;
        _fakeAccountAvatarUrl = null;
        EnsureLikedPlaylist();
        Save();
        OnDataChanged?.Invoke();
    }

    private void OnLanguageChanged()
    {
        OnDataChanged?.Invoke();
    }

    private void EnsureLikedPlaylist()
    {
        if (!Data.Playlists.TryGetValue(LikedPlaylistId, out Playlist? value))
        {
            Data.Playlists[LikedPlaylistId] = new Playlist
            {
                Id = LikedPlaylistId,
                Name = LocalizationService.Instance["Playlist_Liked"],
                SyncMode = PlaylistSyncMode.LocalOnly,
                ThumbnailUrl = null
            };
        }
        else
        {
            value.Name = LocalizationService.Instance["Playlist_Liked"];
        }
    }

    public Playlist GetLikedPlaylist()
    {
        EnsureLikedPlaylist();
        return Data.Playlists[LikedPlaylistId];
    }

    public static bool IsSystemPlaylist(string playlistId)
    {
        return playlistId == LikedPlaylistId;
    }

    public void AddOrUpdateTrack(TrackInfo track)
    {
        if (string.IsNullOrEmpty(track.Id))
            track.Id = GenerateTrackId(track);

        if (Data.Tracks.TryGetValue(track.Id, out var existing))
        {
            track.IsLiked = existing.IsLiked;
            track.IsDisliked = existing.IsDisliked;
            track.IsDownloaded = existing.IsDownloaded || track.IsDownloaded;
            track.LocalPath = existing.LocalPath ?? track.LocalPath;
            track.InPlaylists = existing.InPlaylists;
        }
        Data.Tracks[track.Id] = track;
        Save();
    }

    public void AddOrUpdatePlaylist(Playlist playlist)
    {
        if (playlist.Id == LikedPlaylistId)
        {
            if (Data.Playlists.TryGetValue(LikedPlaylistId, out var existing))
            {
                existing.TrackIds = playlist.TrackIds;
                existing.UpdatedAt = DateTime.Now;
            }
            Save();
            OnDataChanged?.Invoke();
            return;
        }

        if (Data.Playlists.TryGetValue(playlist.Id, out var existingPlaylist))
        {
            existingPlaylist.Name = playlist.Name;
            existingPlaylist.ThumbnailUrl = playlist.ThumbnailUrl;
            existingPlaylist.TrackIds = playlist.TrackIds;
            existingPlaylist.YoutubeId = playlist.YoutubeId;
            existingPlaylist.SyncMode = playlist.SyncMode;
            existingPlaylist.UpdatedAt = DateTime.Now;
        }
        else
        {
            Data.Playlists[playlist.Id] = playlist;
        }
        Save();
        OnDataChanged?.Invoke();
    }

    public bool MergePlaylists(string sourceId, string targetId)
    {
        if (!Data.Playlists.TryGetValue(sourceId, out var sourcePlaylist) ||
            !Data.Playlists.TryGetValue(targetId, out var targetPlaylist))
        {
            return false;
        }

        if (!targetPlaylist.IsLocal) return false;

        var targetTrackIds = new HashSet<string>(targetPlaylist.TrackIds);
        int newTracksCount = 0;

        foreach (var trackId in sourcePlaylist.TrackIds)
        {
            if (targetTrackIds.Add(trackId))
            {
                newTracksCount++;
                if (Data.Tracks.TryGetValue(trackId, out var track))
                {
                    track.InPlaylists.Add(targetId);
                }
            }
        }

        if (newTracksCount > 0)
        {
            targetPlaylist.TrackIds = [.. targetTrackIds];
            targetPlaylist.UpdatedAt = DateTime.Now;
            Save();
            OnDataChanged?.Invoke();
        }

        return true;
    }

    public TrackInfo? GetTrack(string id) => Data.Tracks.TryGetValue(id, out var track) ? track : null;
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
        return [.. Data.RecentlyPlayedIds
            .Take(count)
            .Select(GetTrack)
            .Where(static t => t != null)
            .Cast<TrackInfo>()];
    }

    public void ClearHistory()
    {
        Data.RecentlyPlayedIds.Clear();
        Save();
        OnDataChanged?.Invoke();
    }

    public void ToggleLike(TrackInfo track)
    {
        AddOrUpdateTrack(track);
        track.IsLiked = !track.IsLiked;
        track.IsDisliked = false;

        var likedPlaylist = Data.Playlists[LikedPlaylistId];
        if (track.IsLiked)
        {
            if (!likedPlaylist.TrackIds.Contains(track.Id))
            {
                likedPlaylist.TrackIds.Insert(0, track.Id);
                likedPlaylist.UpdatedAt = DateTime.Now;
            }
            track.InPlaylists.Add(LikedPlaylistId);
        }
        else
        {
            likedPlaylist.TrackIds.Remove(track.Id);
            likedPlaylist.UpdatedAt = DateTime.Now;
            track.InPlaylists.Remove(LikedPlaylistId);
        }

        Data.Tracks[track.Id] = track;
        Save();
        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(track);
    }

    public void ToggleDislike(TrackInfo track)
    {
        AddOrUpdateTrack(track);
        track.IsDisliked = !track.IsDisliked;
        if (track.IsDisliked)
        {
            track.IsLiked = false;
            Data.Playlists[LikedPlaylistId].TrackIds.Remove(track.Id);
            track.InPlaylists.Remove(LikedPlaylistId);
        }
        Data.Tracks[track.Id] = track;
        Save();
        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(track);
    }

    public Playlist CreatePlaylist(string name)
    {
        var playlist = new Playlist { Name = name, SyncMode = PlaylistSyncMode.LocalOnly };
        Data.Playlists[playlist.Id] = playlist;
        Save();
        OnDataChanged?.Invoke();
        return playlist;
    }

    public void RemovePlaylist(string playlistId)
    {
        if (IsSystemPlaylist(playlistId)) return;
        if (Data.Playlists.Remove(playlistId))
        {
            Save();
            OnDataChanged?.Invoke();
        }
    }

    public void RenamePlaylist(string playlistId, string newName)
    {
        if (IsSystemPlaylist(playlistId)) return;
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
        if (IsSystemPlaylist(playlistId)) return;
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
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist)) return;
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
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist)) return;
        playlist.TrackIds.Remove(track.Id);
        track.InPlaylists.Remove(playlistId);
        playlist.UpdatedAt = DateTime.Now;
        if (Data.Tracks.ContainsKey(track.Id)) Data.Tracks[track.Id] = track;
        Save();
        OnDataChanged?.Invoke();
    }

    public void MoveTrackInPlaylist(string playlistId, int oldIndex, int newIndex)
    {
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist)) return;

        if (oldIndex < 0 || oldIndex >= playlist.TrackIds.Count ||
            newIndex < 0 || newIndex >= playlist.TrackIds.Count ||
            oldIndex == newIndex) return;

        var trackId = playlist.TrackIds[oldIndex];
        playlist.TrackIds.RemoveAt(oldIndex);
        playlist.TrackIds.Insert(newIndex, trackId);

        playlist.UpdatedAt = DateTime.Now;
        Save();
        OnDataChanged?.Invoke();
    }

    public bool IsTrackInPlaylist(string trackId, string playlistId)
    {
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist)) return false;
        return playlist.TrackIds.Contains(trackId);
    }

    public List<TrackInfo> GetPlaylistTracks(string playlistId)
    {
        if (!Data.Playlists.TryGetValue(playlistId, out var playlist)) return [];
        return [.. playlist.TrackIds.Select(GetTrack).Where(static t => t != null).Cast<TrackInfo>()];
    }

    public IEnumerable<Playlist> GetAllPlaylists() => Data.Playlists.Values;
    public Playlist? GetPlaylist(string playlistId) => Data.Playlists.TryGetValue(playlistId, out var playlist) ? playlist : null;

    public void MergeAccountPlaylists(IEnumerable<Playlist> accountPlaylists)
    {
        foreach (var playlist in accountPlaylists)
            if (!Data.Playlists.ContainsKey(playlist.Id)) Data.Playlists[playlist.Id] = playlist;
        Save();
        OnDataChanged?.Invoke();
    }

    private static string GenerateTrackId(TrackInfo track)
    {
        if (!string.IsNullOrEmpty(track.Url))
        {
            try
            {
                var videoId = Youtube.Videos.VideoId.TryParse(track.Url);
                if (videoId.HasValue)
                {
                    return $"yt_{videoId.Value.Value}";
                }
            }
            catch { }
        }
        return $"local_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        _saveSubscription.Dispose();
        _saveSignal.Dispose();
        try
        {
            var options = G.Json.Beautiful;
            string json = JsonSerializer.Serialize(Data, options);
            File.WriteAllText(G.File.Library, json);
        }
        catch { }
        GC.SuppressFinalize(this);
    }
}
