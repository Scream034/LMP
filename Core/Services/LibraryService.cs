using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using LMP.Core.Data;
using LMP.Core.Data.Entities;
using LMP.Core.Data.Repositories;
using LMP.Core.Models;
using LMP.Core.Youtube.Utils;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace LMP.Core.Services;

/// <summary>
/// Main library service with SQLite persistence.
/// Uses repositories for data access and TrackRegistry as L1 cache.
/// </summary>
public sealed class LibraryService : IAsyncDisposable
{
    public const string LikedPlaylistId = "liked";

    private readonly TrackRegistry _registry;
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly ISettingsRepository _settings;
    private readonly IDbContextFactory<LibraryDbContext> _dbFactory;

    private readonly Subject<Unit> _saveSettingsSignal = new();
    private readonly IDisposable _saveSubscription;

    public AppSettings Settings { get; private set; } = new();

    public event Action? OnDataChanged;
    public event Action<TrackInfo>? OnTrackUpdated;

    /// <summary>
    /// Вызывается при добавлении или обновлении конкретного плейлиста.
    /// </summary>
    public event Action<Playlist>? OnPlaylistChanged;

    /// <summary>
    /// Вызывается при удалении плейлиста.
    /// </summary>
    public event Action<string>? OnPlaylistRemoved;

    public LibraryService(
        TrackRegistry registry,
        ITrackRepository tracks,
        IPlaylistRepository playlists,
        ISettingsRepository settings,
        IDbContextFactory<LibraryDbContext> dbFactory)
    {
        _registry = registry;
        _tracks = tracks;
        _playlists = playlists;
        _settings = settings;
        _dbFactory = dbFactory;

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // Throttled settings save
        _saveSubscription = _saveSettingsSignal
            .Throttle(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Subscribe(async _ =>
            {
                try { await _settings.SetAsync("AppSettings", Settings); }
                catch (Exception ex) { Log.Error($"[LibraryService] Settings save failed: {ex.Message}"); }
            });
    }

    #region Initialization

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Settings = await _settings.GetOrDefaultAsync("AppSettings", new AppSettings(), ct);
        YoutubeClientUtils.CurrentProfile = Settings.YoutubeClient;

        var jsonPath = G.FilePath.Library;
        if (File.Exists(jsonPath))
        {
            await MigrateFromJsonAsync(jsonPath, ct);
        }

        await _registry.HydrateAsync(ct);
        _registry.SubscribeToCacheEvents();

        await EnsureLikedPlaylistAsync(ct);

        sw.Stop();
        Log.Info($"[LibraryService] Initialized in {sw.ElapsedMilliseconds}ms");
    }

    private async Task MigrateFromJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            Log.Info("[Migration] Starting JSON -> SQLite migration...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var json = await File.ReadAllTextAsync(path, ct);
            var legacy = JsonSerializer.Deserialize<LegacyLibraryData>(json);
            if (legacy == null)
            {
                Log.Warn("[Migration] Could not deserialize legacy data");
                return;
            }

            var migratedTrackIds = new HashSet<string>();

            if (legacy.Tracks?.Count > 0)
            {
                int migrated = 0;
                int failed = 0;

                foreach (var track in legacy.Tracks.Values)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(track.Id)) continue;
                        await _tracks.UpsertAsync(track, ct);
                        migratedTrackIds.Add(track.Id);
                        migrated++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warn($"[Migration] Failed to migrate track {track.Id}: {ex.Message}");
                    }
                }
                Log.Info($"[Migration] Migrated {migrated} tracks ({failed} failed)");
            }

            if (legacy.Playlists?.Count > 0)
            {
                int playlistsMigrated = 0;
                int totalTracksAdded = 0;
                int totalTracksMissing = 0;

                foreach (var legacyPl in legacy.Playlists.Values)
                {
                    try
                    {
                        var playlist = legacyPl.ToPlaylist();
                        await _playlists.UpsertAsync(playlist, ct);
                        playlistsMigrated++;

                        var validTrackIds = legacyPl.TrackIds
                            .Where(migratedTrackIds.Contains)
                            .ToList();

                        var missingCount = legacyPl.TrackIds.Count - validTrackIds.Count;
                        if (missingCount > 0)
                        {
                            totalTracksMissing += missingCount;
                        }

                        var added = await _playlists.AddTracksAsync(playlist.Id, validTrackIds, ct);
                        totalTracksAdded += added;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Migration] Failed to migrate playlist {legacyPl.Name}: {ex.Message}");
                    }
                }

                Log.Info($"[Migration] Migrated {playlistsMigrated} playlists, added {totalTracksAdded} track links");
            }

            if (legacy.RecentlyPlayedIds?.Count > 0)
            {
                int historyAdded = 0;
                foreach (var id in legacy.RecentlyPlayedIds.AsEnumerable().Reverse().Take(100))
                {
                    try
                    {
                        if (migratedTrackIds.Contains(id))
                        {
                            await _tracks.AddToHistoryAsync(id, ct);
                            historyAdded++;
                        }
                    }
                    catch { }
                }
                Log.Info($"[Migration] Added {historyAdded} history entries");
            }

            Settings = MapLegacySettings(legacy);
            await _settings.SetAsync("AppSettings", Settings, ct);

            var backup = path + $".migrated.{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, backup);

            sw.Stop();
            Log.Info($"[Migration] Complete in {sw.ElapsedMilliseconds}ms. Backup: {backup}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Migration] Failed: {ex.Message}");
        }
    }

    private static AppSettings MapLegacySettings(LegacyLibraryData d) => new()
    {
        Volume = d.Volume,
        LastVolume = d.LastVolume,
        ShuffleEnabled = d.ShuffleEnabled,
        RepeatMode = d.RepeatMode,
        MaxVolumeLimit = d.MaxVolumeLimit,
        TargetGainDb = d.TargetGainDb,
        QualityPreference = d.QualityPreference,
        RememberTrackFormat = d.RememberTrackFormat,
        InternetProfile = d.InternetProfile,
        LanguageCode = d.LanguageCode,
        DownloadPath = d.DownloadPath,
        DiscordRpcEnabled = d.DiscordRpcEnabled,
        AutoPlayOnUrlPaste = d.AutoPlayOnUrlPaste,
        LoadBatchSize = d.LoadBatchSize,
        SearchBatchSize = d.SearchBatchSize,
        EnableSearchCache = d.EnableSearchCache,
        SearchCacheTtlMinutes = d.SearchCacheTtlMinutes,
        EnableSmoothLoading = d.EnableSmoothLoading,
        PlaylistHeaderHeight = d.PlaylistHeaderHeight,
        LastSearchQuery = d.LastSearchQuery,
        SearchHistory = d.SearchHistory ?? []
    };

    #endregion

    #region Tracks

    public async Task AddOrUpdateTrackAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, ct);
        var canonical = _registry.RegisterOrUpdate(track);
        await _tracks.UpsertAsync(canonical, ct);
        OnTrackUpdated?.Invoke(canonical);
    }

    public TrackInfo? GetTrack(string id) => _registry.TryGet(id);

    public async Task<TrackInfo?> GetTrackAsync(string id, CancellationToken ct = default)
    {
        return await _registry.GetOrLoadAsync(id, ct);
    }

    public bool HasTrack(string id) => _registry.TryGet(id) != null;

    public async Task<List<TrackInfo>> SearchTracksAsync(
     string query, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.SearchAsync(query, limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, ct);

        foreach (var t in tracks)
        {
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<TimeSpan> GetPlaylistTotalDurationAsync(string playlistId, CancellationToken ct = default)
    {
        var totalTicks = await _playlists.GetTotalDurationTicksAsync(playlistId, ct);
        return TimeSpan.FromTicks(totalTicks);
    }

    // ═══ ОДИН ЗАПРОС НА ВСЮ БАЗУ ═══
    public async Task<long> GetTotalLibraryDurationAsync(CancellationToken ct = default)
    {
        return await _playlists.GetTotalLibraryDurationAsync(ct);
    }

    public async Task<List<TrackInfo>> GetAllTracksAsync(
        int limit = 10000,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await _tracks.GetAllAsync(limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, ct);

        foreach (var t in tracks)
        {
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<List<TrackInfo>> GetLocalTracksAsync(
        int limit = 1000,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLocalTracksAsync(limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, ct);

        foreach (var t in tracks)
        {
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<int> GetTrackCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountAsync(ct);
    }

    public async Task<int> GetLocalTrackCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountLocalAsync(ct);
    }

    public async Task<List<TrackInfo>> SearchLocalTracksAsync(
     string query,
     int limit = 100,
     CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetLocalTracksAsync(limit, 0, ct);

        var allLocal = await _tracks.GetLocalTracksAsync(limit * 2, 0, ct);

        var filtered = allLocal
            .Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        if (filtered.Count == 0) return filtered;

        var trackIds = filtered.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, ct);

        foreach (var t in filtered)
        {
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return filtered;
    }

    #endregion

    #region History

    public async Task AddToRecentlyPlayedAsync(TrackInfo track, CancellationToken ct = default)
    {
        await AddOrUpdateTrackAsync(track, ct);
        await _tracks.AddToHistoryAsync(track.Id, ct);
    }

    public async Task<List<TrackInfo>> GetRecentlyPlayedAsync(int count = 20, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetRecentlyPlayedAsync(count, ct);
        foreach (var t in tracks) _registry.RegisterOrUpdate(t);
        return tracks;
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await _tracks.ClearHistoryAsync(ct);
        OnDataChanged?.Invoke();
    }

    #endregion

    #region Likes

    public async Task SetLikeStateAsync(TrackInfo track, bool isLiked, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, ct);
        var canonical = _registry.RegisterOrUpdate(track);

        if (canonical.IsLiked == isLiked)
        {
            Log.Debug($"[LibraryService] Like state already {isLiked} for {track.Id}");
            return;
        }

        canonical.IsLiked = isLiked;
        if (isLiked) canonical.IsDisliked = false;

        // Гарантируем наличие трека в БД перед созданием связи
        await _tracks.UpsertAsync(canonical, ct);

        if (isLiked)
        {
            await _playlists.AddTrackAsync(LikedPlaylistId, canonical.Id, 0, ct);
            canonical.InPlaylists.Add(LikedPlaylistId);
        }
        else
        {
            await _playlists.RemoveTrackAsync(LikedPlaylistId, canonical.Id, ct);
            canonical.InPlaylists.Remove(LikedPlaylistId);
        }

        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
    }

    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, ct);
        var canonical = _registry.RegisterOrUpdate(track);
        await SetLikeStateAsync(track, !canonical.IsLiked, ct);
    }

    public async Task ToggleDislikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        var canonical = _registry.RegisterOrUpdate(track);
        canonical.IsDisliked = !canonical.IsDisliked;

        if (canonical.IsDisliked)
        {
            canonical.IsLiked = false;
            await _playlists.RemoveTrackAsync(LikedPlaylistId, canonical.Id, ct);
            canonical.InPlaylists.Remove(LikedPlaylistId);
        }

        await _tracks.UpsertAsync(canonical, ct);
        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
    }

    public async Task<List<TrackInfo>> GetLikedTracksAsync(
     int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLikedAsync(limit, offset, ct);
        if (tracks.Count == 0) return tracks;

        var trackIds = tracks.Select(t => t.Id).ToList();
        var playlistsMap = await _playlists.GetPlaylistsForTracksAsync(trackIds, ct);

        foreach (var t in tracks)
        {
            t.InPlaylists = playlistsMap.TryGetValue(t.Id, out var pls) ? pls : [];
            _registry.RegisterOrUpdate(t);
        }

        return tracks;
    }

    public async Task<int> GetLikedCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountLikedAsync(ct);
    }

    #endregion

    #region Playlists

    public async Task<string?> GetSetVideoIdAsync(
        string playlistId, string trackId, CancellationToken ct = default)
    {
        return await _playlists.GetSetVideoIdAsync(playlistId, trackId, ct);
    }

    public async Task UpdateSetVideoIdAsync(
        string playlistId, string trackId, string setVideoId, CancellationToken ct = default)
    {
        await _playlists.UpdateSetVideoIdAsync(playlistId, trackId, setVideoId, ct);
    }

    public async Task UpdateSetVideoIdsAsync(
        string playlistId,
        IReadOnlyList<(string TrackId, string SetVideoId)> mappings,
        CancellationToken ct = default)
    {
        await _playlists.UpdateSetVideoIdsAsync(playlistId, mappings, ct);
    }

    public async Task<List<string>> GetPlaylistTrackIdsAsync(string playlistId, CancellationToken ct = default)
    {
        return await _playlists.GetTrackIdsAsync(playlistId, ct);
    }

    private async Task EnsureLikedPlaylistAsync(CancellationToken ct = default)
    {
        var liked = await _playlists.GetByIdAsync(LikedPlaylistId, ct);
        if (liked == null)
        {
            await _playlists.UpsertAsync(new Playlist
            {
                Id = LikedPlaylistId,
                Name = LocalizationService.Instance["Playlist_Liked"],
                SyncMode = PlaylistSyncMode.LocalOnly
            }, ct);
        }
    }

    public async Task<(Playlist Playlist, int TrackCount)?> GetPlaylistWithCountAsync(
        string playlistId, CancellationToken ct = default)
    {
        var playlist = await GetPlaylistAsync(playlistId, ct);
        if (playlist == null) return null;

        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, ct);
        return (playlist, trackIds.Count);
    }

    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllPlaylistsWithCountsAsync(CancellationToken ct = default)
    {
        var results = await _playlists.GetAllWithCountsAsync(ct);

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Playlist.Id == LikedPlaylistId)
            {
                var pl = results[i].Playlist;
                pl.Name = LocalizationService.Instance["Playlist_Liked"];
                results[i] = (pl, results[i].TrackCount);
            }
        }

        return results;
    }

    public async Task<Playlist?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        var pl = await _playlists.GetByIdAsync(id, ct);
        if (pl != null && id == LikedPlaylistId)
        {
            pl.Name = LocalizationService.Instance["Playlist_Liked"];
        }
        return pl;
    }

    public async Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
    {
        await EnsureLikedPlaylistAsync(ct);
        return (await _playlists.GetByIdAsync(LikedPlaylistId, ct))!;
    }

    public async Task<List<Playlist>> GetAllPlaylistsAsync(CancellationToken ct = default)
    {
        var all = await _playlists.GetAllAsync(ct);
        var liked = all.FirstOrDefault(p => p.Id == LikedPlaylistId);
        liked?.Name = LocalizationService.Instance["Playlist_Liked"];
        return all;
    }

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(
        string playlistId, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, ct);
        var pageIds = trackIds.Skip(offset).Take(limit).ToList();

        if (pageIds.Count == 0) return [];

        await _registry.PreloadAsync(pageIds, ct);

        var tracks = new List<TrackInfo>(pageIds.Count);
        foreach (var id in pageIds)
        {
            var track = _registry.TryGet(id);
            if (track != null) tracks.Add(track);
        }

        return tracks;
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, CancellationToken ct = default)
    {
        var playlist = new Playlist { Name = name, SyncMode = PlaylistSyncMode.LocalOnly };
        await _playlists.UpsertAsync(playlist, ct);
        OnDataChanged?.Invoke();
        return playlist;
    }

    public async Task AddOrUpdatePlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        await _playlists.UpsertAsync(playlist, ct);

        if (playlist.TrackIds.Count > 0)
        {
            var existingTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, ct);
            var existingSet = new HashSet<string>(existingTrackIds);

            var newTrackIds = playlist.TrackIds.Where(id => !existingSet.Contains(id)).ToList();
            if (newTrackIds.Count > 0)
            {
                await _playlists.AddTracksAsync(playlist.Id, newTrackIds, ct);
                Log.Debug($"[LibraryService] Добавлено {newTrackIds.Count} треков в плейлист '{playlist.Name}'");
            }
        }

        OnPlaylistChanged?.Invoke(playlist);
        OnDataChanged?.Invoke();
    }

    public async Task AddTrackToPlaylistAsync(TrackInfo track, string playlistId, CancellationToken ct = default)
    {
        await AddOrUpdateTrackAsync(track, ct);
        await _playlists.AddTrackAsync(playlistId, track.Id, null, ct);
        track.InPlaylists.Add(playlistId);
        _registry.UpdatePinStatus(track);
        OnDataChanged?.Invoke();
    }

    public async Task RemoveTrackFromPlaylistAsync(string trackId, string playlistId, CancellationToken ct = default)
    {
        await _playlists.RemoveTrackAsync(playlistId, trackId, ct);
        var track = _registry.TryGet(trackId);
        if (track != null)
        {
            track.InPlaylists.Remove(playlistId);
            _registry.UpdatePinStatus(track);
        }
        OnDataChanged?.Invoke();
    }

    public async Task MoveTrackInPlaylistAsync(string playlistId, int oldIndex, int newIndex, CancellationToken ct = default)
    {
        await _playlists.MoveTrackAsync(playlistId, oldIndex, newIndex, ct);
        OnDataChanged?.Invoke();
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
    {
        if (IsSystemPlaylist(playlistId)) return;
        await _playlists.RenameAsync(playlistId, newName, ct);
        OnDataChanged?.Invoke();
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        if (IsSystemPlaylist(playlistId)) return;

        foreach (var track in _registry.GetPinnedTracks())
            track.InPlaylists.Remove(playlistId);

        await _playlists.DeleteAsync(playlistId, ct);

        OnPlaylistRemoved?.Invoke(playlistId);
        OnDataChanged?.Invoke();
    }

    public async Task<bool> IsTrackInPlaylistAsync(string trackId, string playlistId, CancellationToken ct = default)
    {
        return await _playlists.ContainsTrackAsync(playlistId, trackId, ct);
    }

    public static bool IsSystemPlaylist(string id) => id == LikedPlaylistId;

    #endregion

    #region Settings

    public string DownloadPath
    {
        get => string.IsNullOrEmpty(Settings.DownloadPath) ? G.Folder.Downloads : Settings.DownloadPath;
        set { Settings.DownloadPath = value; SaveSettings(); }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        update(Settings);
        SaveSettings();
    }

    private void SaveSettings() => _saveSettingsSignal.OnNext(Unit.Default);

    #endregion

    #region Events

    private void OnLanguageChanged(object? _, string __)
    {
        OnDataChanged?.Invoke();
    }

    #endregion

    #region Cleanup

    public async Task ResetAsync(CancellationToken ct = default)
    {
        _registry.Clear();

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        await ctx.Database.EnsureDeletedAsync(ct);
        await ctx.Database.EnsureCreatedAsync(ct);
        await ctx.OptimizeAsync(ct);
        await ctx.EnsureFtsTablesAsync(ct);

        Settings = new AppSettings();
        await EnsureLikedPlaylistAsync(ct);
        OnDataChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        _saveSubscription.Dispose();
        _saveSettingsSignal.Dispose();

        await _registry.FlushAsync();
        await _settings.SetAsync("AppSettings", Settings);

        GC.SuppressFinalize(this);
    }

    #endregion
}