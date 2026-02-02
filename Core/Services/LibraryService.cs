using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using LMP.Core.Data;
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

    private AppSettings _appSettings = new();
    public AppSettings Settings => _appSettings;

    // Fake Account cache
    private string? _fakeAccountName;
    private string? _fakeAccountAvatarUrl;

    public event Action? OnDataChanged;
    public event Action? OnFakeAccountChanged;
    public event Action<TrackInfo>? OnTrackUpdated;

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
                try { await _settings.SetAsync("AppSettings", _appSettings); }
                catch (Exception ex) { Log.Error($"[LibraryService] Settings save failed: {ex.Message}"); }
            });
    }

    #region Initialization

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Create/migrate database
        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        await ctx.Database.EnsureCreatedAsync(ct);
        await ctx.OptimizeAsync(ct);
        await ctx.EnsureFtsTablesAsync(ct);

        // Migrate from JSON if exists
        var jsonPath = G.File.Library;
        if (File.Exists(jsonPath))
        {
            await MigrateFromJsonAsync(jsonPath, ct);
        }

        // Load settings
        _appSettings = await _settings.GetOrDefaultAsync("AppSettings", new AppSettings(), ct);
        // ИНИЦИАЛИЗИРУЕМ СТАТИКУ
        YoutubeClientUtils.CurrentProfile = _appSettings.YoutubeClient;

        // Hydrate cache
        await _registry.HydrateAsync(ct);

        // Ensure liked playlist
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

            // Step 1: Migrate all tracks first
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

            // Step 2: Migrate playlists (create playlist entries first, then add tracks)
            if (legacy.Playlists?.Count > 0)
            {
                int playlistsMigrated = 0;
                int totalTracksAdded = 0;
                int totalTracksMissing = 0;

                foreach (var legacyPl in legacy.Playlists.Values)
                {
                    try
                    {
                        // Convert LegacyPlaylist to Playlist
                        var playlist = legacyPl.ToPlaylist();

                        // First, create the playlist itself
                        await _playlists.UpsertAsync(playlist, ct);
                        playlistsMigrated++;

                        // Then, add only tracks that exist in the database
                        var validTrackIds = legacyPl.TrackIds
                            .Where(migratedTrackIds.Contains)
                            .ToList();

                        var missingCount = legacyPl.TrackIds.Count - validTrackIds.Count;
                        if (missingCount > 0)
                        {
                            totalTracksMissing += missingCount;
                            Log.Warn($"[Migration] Playlist '{legacyPl.Name}': {missingCount} tracks not found in library");
                        }

                        // Use batch add for efficiency
                        var added = await _playlists.AddTracksAsync(playlist.Id, validTrackIds, ct);
                        totalTracksAdded += added;

                        Log.Debug($"[Migration] Playlist '{legacyPl.Name}': {added}/{legacyPl.TrackIds.Count} tracks linked");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[Migration] Failed to migrate playlist {legacyPl.Name}: {ex.Message}");
                    }
                }

                Log.Info($"[Migration] Migrated {playlistsMigrated} playlists, added {totalTracksAdded} track links ({totalTracksMissing} tracks were missing)");
            }

            // Step 3: Migrate recently played (only for existing tracks)
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

            // Step 4: Migrate settings
            _appSettings = MapLegacySettings(legacy);
            await _settings.SetAsync("AppSettings", _appSettings, ct);

            // Backup old file
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
        FakeAccountChannelUrl = d.FakeAccountChannelUrl,
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

    /// <summary>
    /// Full-text search in database.
    /// </summary>
    public async Task<List<TrackInfo>> SearchTracksAsync(
        string query, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var tracks = await _tracks.SearchAsync(query, limit, offset, ct);
        foreach (var t in tracks)
        {
            t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            _registry.RegisterOrUpdate(t);
        }
        return tracks;
    }

    /// <summary>
    /// Gets total duration of all tracks in playlist.
    /// </summary>
    public async Task<TimeSpan> GetPlaylistTotalDurationAsync(string playlistId, CancellationToken ct = default)
    {
        var totalTicks = await _playlists.GetTotalDurationTicksAsync(playlistId, ct);
        return TimeSpan.FromTicks(totalTicks);
    }

    /// <summary>
    /// Gets all tracks from the library with pagination.
    /// </summary>
    public async Task<List<TrackInfo>> GetAllTracksAsync(
        int limit = 10000,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await _tracks.GetAllAsync(limit, offset, ct);
        foreach (var t in tracks)
        {
            t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            _registry.RegisterOrUpdate(t);
        }
        return tracks;
    }

    /// <summary>
    /// Gets only local and downloaded tracks (for offline search).
    /// </summary>
    public async Task<List<TrackInfo>> GetLocalTracksAsync(
        int limit = 1000,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tracks = await _tracks.GetLocalTracksAsync(limit, offset, ct);
        foreach (var t in tracks)
        {
            t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
            _registry.RegisterOrUpdate(t);
        }
        return tracks;
    }

    /// <summary>
    /// Gets total count of tracks in the library.
    /// </summary>
    public async Task<int> GetTrackCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountAsync(ct);
    }

    /// <summary>
    /// Gets count of local/downloaded tracks.
    /// </summary>
    public async Task<int> GetLocalTrackCountAsync(CancellationToken ct = default)
    {
        return await _tracks.CountLocalAsync(ct);
    }

    /// <summary>
    /// Searches tracks in the local library (offline-capable).
    /// Uses FTS for fast full-text search.
    /// </summary>
    public async Task<List<TrackInfo>> SearchLocalTracksAsync(
        string query,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetLocalTracksAsync(limit, 0, ct);

        // Сначала получаем все локальные треки
        var allLocal = await _tracks.GetLocalTracksAsync(limit * 2, 0, ct);

        // Фильтруем в памяти (для небольших коллекций это быстрее чем SQL LIKE)
        var filtered = allLocal
            .Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        foreach (var t in filtered)
        {
            t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
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

    public async Task ToggleLikeAsync(TrackInfo track, CancellationToken ct = default)
    {
        track.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(track.Id, ct);
        var canonical = _registry.RegisterOrUpdate(track);

        canonical.IsLiked = !canonical.IsLiked;
        if (canonical.IsLiked) canonical.IsDisliked = false;

        if (canonical.IsLiked)
        {
            await _playlists.AddTrackAsync(LikedPlaylistId, canonical.Id, 0, ct);
            canonical.InPlaylists.Add(LikedPlaylistId);
        }
        else
        {
            await _playlists.RemoveTrackAsync(LikedPlaylistId, canonical.Id, ct);
            canonical.InPlaylists.Remove(LikedPlaylistId);
        }

        await _tracks.SetLikedAsync(canonical.Id, canonical.IsLiked, ct);
        _registry.UpdatePinStatus(canonical);

        OnDataChanged?.Invoke();
        OnTrackUpdated?.Invoke(canonical);
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
        foreach (var t in tracks)
        {
            t.InPlaylists = await _playlists.GetPlaylistsForTrackAsync(t.Id, ct);
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

    /// <summary>
    /// Gets all playlists with their track counts.
    /// </summary>
    public async Task<List<(Playlist Playlist, int TrackCount)>> GetAllPlaylistsWithCountsAsync(CancellationToken ct = default)
    {
        var results = await _playlists.GetAllWithCountsAsync(ct);

        // Update "Liked" playlist name
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
        if (liked != null)
        {
            liked.Name = LocalizationService.Instance["Playlist_Liked"];
        }
        return all;
    }

    /// <summary>
    /// Gets playlist tracks with pagination support.
    /// </summary>
    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(
        string playlistId, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var trackIds = await _playlists.GetTrackIdsAsync(playlistId, ct);
        var pageIds = trackIds.Skip(offset).Take(limit).ToList();

        await _registry.PreloadAsync(pageIds, ct);

        var tracks = new List<TrackInfo>(pageIds.Count);
        foreach (var id in pageIds)
        {
            var track = await _registry.GetOrLoadAsync(id, ct);
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

        // Если плейлист содержит TrackIds, синхронизируем связи
        if (playlist.TrackIds.Count > 0)
        {
            // Получаем текущие треки плейлиста из БД
            var existingTrackIds = await _playlists.GetTrackIdsAsync(playlist.Id, ct);
            var existingSet = new HashSet<string>(existingTrackIds);

            // Добавляем только новые треки
            var newTrackIds = playlist.TrackIds.Where(id => !existingSet.Contains(id)).ToList();
            if (newTrackIds.Count > 0)
            {
                await _playlists.AddTracksAsync(playlist.Id, newTrackIds, ct);
                Log.Debug($"[LibraryService] Added {newTrackIds.Count} tracks to playlist '{playlist.Name}'");
            }
        }

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
        get => string.IsNullOrEmpty(_appSettings.DownloadPath) ? G.Folder.Downloads : _appSettings.DownloadPath;
        set { _appSettings.DownloadPath = value; SaveSettings(); }
    }

    public void UpdateSettings(Action<AppSettings> update)
    {
        update(_appSettings);
        SaveSettings();
    }

    private void SaveSettings() => _saveSettingsSignal.OnNext(Unit.Default);

    #endregion

    #region Fake Account

    public bool HasFakeAccount => !string.IsNullOrEmpty(_appSettings.FakeAccountChannelUrl);
    public string? FakeAccountUrl => _appSettings.FakeAccountChannelUrl;
    public string? FakeAccountName => _fakeAccountName;
    public string? FakeAccountAvatarUrl => _fakeAccountAvatarUrl;

    public void SetFakeAccount(string url, string name, string avatar)
    {
        _appSettings.FakeAccountChannelUrl = url;
        _fakeAccountName = name;
        _fakeAccountAvatarUrl = avatar;
        SaveSettings();
        OnFakeAccountChanged?.Invoke();
        OnDataChanged?.Invoke();
    }

    public void UpdateFakeAccountCache(string name, string avatar)
    {
        _fakeAccountName = name;
        _fakeAccountAvatarUrl = avatar;
        OnFakeAccountChanged?.Invoke();
    }

    public void ClearFakeAccount()
    {
        _appSettings.FakeAccountChannelUrl = null;
        _fakeAccountName = null;
        _fakeAccountAvatarUrl = null;
        SaveSettings();
        OnFakeAccountChanged?.Invoke();
        OnDataChanged?.Invoke();
    }

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
        _fakeAccountName = null;
        _fakeAccountAvatarUrl = null;

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        await ctx.Database.EnsureDeletedAsync(ct);
        await ctx.Database.EnsureCreatedAsync(ct);
        await ctx.OptimizeAsync(ct);
        await ctx.EnsureFtsTablesAsync(ct);

        _appSettings = new AppSettings();
        await EnsureLikedPlaylistAsync(ct);
        OnDataChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        _saveSubscription.Dispose();
        _saveSettingsSignal.Dispose();

        // Final flush
        await _registry.FlushAsync();
        await _settings.SetAsync("AppSettings", _appSettings);

        GC.SuppressFinalize(this);
    }

    #endregion
}