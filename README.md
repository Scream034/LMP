Нет, дело не в этом.
Вот посмотри старый код, я нашёл какой коммит ломает:
```
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public class CookieAuthService
{
    private readonly string _storagePath;
    private string _rawCookies = "";
    
    // Используем Firefox, так как он стабильнее для эмуляции WebRemix
    private readonly string _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);
    public string UserAgent => _userAgent;

    public event Action? OnAuthStateChanged;
    
    // Событие, чтобы уведомить логгер или UI, что куки обновились на диске
    public event Action? OnCookiesUpdated; 

    public CookieAuthService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "auth_cookies.txt");

        Load();
    }

    private void Load()
    {
        if (File.Exists(_storagePath))
        {
            _rawCookies = File.ReadAllText(_storagePath);
        }
    }

    // Сохранение, вызванное пользователем (вставка текста)
    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        var clean = Regex.Replace(cookies, @"^Cookie:\s*", "", RegexOptions.IgnoreCase);
        clean = clean.Replace("\r", "").Replace("\n", "");
        clean = clean.Trim().Trim('"');

        _rawCookies = clean;
        File.WriteAllText(_storagePath, _rawCookies);
        OnAuthStateChanged?.Invoke();
    }

    // АВТОМАТИЧЕСКОЕ СОХРАНЕНИЕ
    // Вызывается таймером из YoutubeProvider
    public void SyncCookiesFromContainer(CookieContainer container)
    {
        try 
        {
            // Собираем куки с двух основных доменов, так как YouTube размазывает их
            var musicUri = new Uri("https://music.youtube.com");
            var mainUri = new Uri("https://youtube.com");
            
            var cookiesMusic = container.GetCookies(musicUri).Cast<Cookie>();
            var cookiesMain = container.GetCookies(mainUri).Cast<Cookie>();

            // Объединяем и убираем дубликаты по имени
            var allCookies = cookiesMusic.Concat(cookiesMain)
                .GroupBy(c => c.Name)
                .Select(g => g.First()) // Берем первую (обычно самую свежую)
                .ToList();

            if (allCookies.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var cookie in allCookies)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{cookie.Name}={cookie.Value}");
            }

            var newCookiesString = sb.ToString();

            // Пишем на диск, только если строка реально изменилась
            // Это предотвращает износ SSD постоянной записью одного и того же
            if (!string.Equals(_rawCookies, newCookiesString, StringComparison.Ordinal))
            {
                _rawCookies = newCookiesString;
                File.WriteAllText(_storagePath, _rawCookies);
                OnCookiesUpdated?.Invoke(); // Уведомляем систему
            }
        }
        catch (Exception)
        {
            // Игнорируем ошибки при фоновом сохранении, чтобы не крашить плеер
        }
    }

    public void Logout()
    {
        _rawCookies = "";
        if (File.Exists(_storagePath)) File.Delete(_storagePath);
        OnAuthStateChanged?.Invoke();
    }

    public CookieContainer GetCookieContainer()
    {
        var container = new CookieContainer();
        if (string.IsNullOrWhiteSpace(_rawCookies)) return container;

        // Разделители: точка с запятой (стандарт) или запятая (иногда встречается при копировании)
        var pairs = _rawCookies.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        
        var domains = new[] { 
            new Uri("https://youtube.com"), 
            new Uri("https://music.youtube.com"),
            new Uri("https://www.youtube.com") 
        };

        foreach (var pair in pairs)
        {
            var p = pair.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            
            var eqIndex = p.IndexOf('=');
            if (eqIndex > 0)
            {
                var name = p[..eqIndex].Trim();
                var value = p[(eqIndex + 1)..].Trim();
                
                foreach (var d in domains)
                {
                    try { container.Add(d, new Cookie(name, value)); } catch { }
                }
            }
        }
        return container;
    }
}
```

```
// === ФАЙЛ: Core/Services/ImageCacheService.cs ===
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Управляет загрузкой и кэшированием изображений.
/// Реализует двухуровневый кэш: Память (LRU) + Диск.
/// С поддержкой динамических лимитов из настроек.
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private const int MaxMemoryCacheItems = 60;
    
    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _downloadSemaphore = new(5);

    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes = 0;
    private bool _isDisposed;

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "ImageCache");
        Directory.CreateDirectory(_cacheFolder);

        _ = Task.Run(CalculateDiskCacheSizeAsync);
    }

    public async Task<Bitmap?> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var key = GetCacheKey(url);

        if (_memoryCache.TryGetValue(key, out var cached))
        {
            TouchLru(key);
            return cached.Bitmap;
        }

        return await LoadFromDiskOrNetwork(url, key, ct);
    }

    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;
        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Where(u => !_memoryCache.ContainsKey(GetCacheKey(u)))
            .Take(15)
            .Select(u => EnsureCachedDiskOnlyAsync(u, ct));
        
        try { await Task.WhenAll(tasks); } catch { }
    }

    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
        GC.Collect(2, GCCollectionMode.Optimized);
        Log.Info("Memory cache cleared.");
    }
    
    public async Task ClearAllAsync()
    {
        ClearMemoryCache();
        
        // Очистка диска
        var files = Directory.GetFiles(_cacheFolder);
        foreach (var f in files)
        {
            try 
            {
                // Пытаемся взять лок на файл если он используется
                var key = Path.GetFileNameWithoutExtension(f);
                var lockObj = GetFileLock(key);
                await lockObj.WaitAsync();
                try { File.Delete(f); } finally { lockObj.Release(); }
            }
            catch { }
        }
        Interlocked.Exchange(ref _currentDiskCacheBytes, 0);
        Log.Info("Image disk cache cleared.");
    }
    
    public (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder);
            long totalSize = files.Sum(static f => new FileInfo(f).Length);
            Interlocked.Exchange(ref _currentDiskCacheBytes, totalSize);
            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    private SemaphoreSlim GetFileLock(string key) => _fileLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private async Task<Bitmap?> LoadFromDiskOrNetwork(string url, string key, CancellationToken ct)
    {
        var fileLock = GetFileLock(key);

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (_memoryCache.TryGetValue(key, out var cached))
                {
                    TouchLru(key);
                    return cached.Bitmap;
                }

                await fileLock.WaitAsync(ct);
                try
                {
                    var diskPath = GetDiskPath(key);

                    if (!File.Exists(diskPath))
                    {
                        var bytes = await _http.GetByteArrayAsync(url, ct);
                        await File.WriteAllBytesAsync(diskPath, bytes, ct);
                        Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);
                        
                        // Check limit
                        long limitBytes = (long)_library.Data.Storage.ImageCacheLimitMb * 1024 * 1024;
                        if (_currentDiskCacheBytes > limitBytes)
                        {
                            _ = Task.Run(CleanupDiskCacheAsync, CancellationToken.None);
                        }
                    }

                    if (File.Exists(diskPath))
                    {
                        return await Task.Run(() =>
                        {
                            try
                            {
                                using var stream = File.OpenRead(diskPath);
                                var bmp = Bitmap.DecodeToWidth(stream, 300);
                                AddToMemoryCache(key, bmp);
                                return bmp;
                            }
                            catch (Exception)
                            {
                                try { File.Delete(diskPath); } catch { }
                                return null;
                            }
                        }, ct);
                    }
                    return null;
                }
                finally { fileLock.Release(); }
            }
            finally { _downloadSemaphore.Release(); }
        }
        catch { return null; }
    }

    private async Task EnsureCachedDiskOnlyAsync(string url, CancellationToken ct)
    {
        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);
        if (File.Exists(diskPath)) return;
        _ = await LoadFromDiskOrNetwork(url, key, ct);
    }

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        lock (_lruLock)
        {
            while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _memoryCache.TryRemove(oldest, out _);
            }
            if (_memoryCache.TryAdd(key, new CachedImage { Bitmap = bitmap, CachedAt = DateTime.UtcNow }))
            {
                _lruOrder.AddFirst(key);
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruOrder.Contains(key))
            {
                _lruOrder.Remove(key);
                _lruOrder.AddFirst(key);
            }
        }
    }

    private static string GetCacheKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    private string GetDiskPath(string key) => Path.Combine(_cacheFolder, $"{key}.jpg");

    private async Task CalculateDiskCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder);
            long total = 0;
            foreach (var file in files) total += new FileInfo(file).Length;
            Interlocked.Exchange(ref _currentDiskCacheBytes, total);
        }
        catch { }
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder)
                .Select(static f => new FileInfo(f))
                .OrderBy(static f => f.LastAccessTime)
                .ToList();

            long limitBytes = (long)_library.Data.Storage.ImageCacheLimitMb * 1024 * 1024;
            long targetSize = limitBytes / 2;
            long deleted = 0;

            foreach (var file in files)
            {
                if (_currentDiskCacheBytes - deleted <= targetSize) break;

                var key = Path.GetFileNameWithoutExtension(file.Name);
                var fileLock = GetFileLock(key);
                if (await fileLock.WaitAsync(0))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted += size;
                        _fileLocks.TryRemove(key, out _);
                    }
                    catch { }
                    finally { fileLock.Release(); }
                }
            }
            Interlocked.Add(ref _currentDiskCacheBytes, -deleted);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
        _http.Dispose();
        foreach (var l in _fileLocks.Values) l.Dispose();
        _fileLocks.Clear();
        GC.SuppressFinalize(this);
    }
}
```

```
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
    private const string LibraryFileName = "library.json";

    private readonly string _libraryPath;
    private readonly string _appFolder;

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
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _appFolder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(_appFolder);
        _libraryPath = Path.Combine(_appFolder, LibraryFileName);

        LocalizationService.Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        _saveSubscription = _saveSignal
            .Throttle(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Subscribe(async _ => await SaveInternalAsync());

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
            if (File.Exists(_libraryPath))
            {
                using var fs = new FileStream(_libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Data = JsonSerializer.Deserialize<LibraryData>(fs) ?? new LibraryData();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load library: {ex.Message}");
            Data = new LibraryData();
        }
        EnsureLikedPlaylist();
        Directory.CreateDirectory(DownloadPath);
    }

    public void Save()
    {
        _saveSignal.OnNext(Unit.Default);
    }

    private async Task SaveInternalAsync()
    {
        try
        {
            var tempFile = _libraryPath + ".tmp";

            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(fs, Data, new JsonSerializerOptions { WriteIndented = true });
            }

            File.Move(tempFile, _libraryPath, true);
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

    // ... остальные методы без изменений ...

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
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(Data, options);
            File.WriteAllText(_libraryPath, json);
        }
        catch { }

        GC.SuppressFinalize(this);
    }
}


```

```
// === ФАЙЛ: Core/Services/MemoryFirstCachingStream.cs ===
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using LMP.Core.Models;

namespace LMP.Core.Services;

public sealed class MemoryFirstCachingStream : Stream
{
    private readonly int _chunkSize;
    private readonly int _readAheadChunks;
    private readonly int _maxConcurrentDownloads;
    private readonly int _maxRamChunks;
    private readonly int _downloadTimeoutMs;

    private const int ProgressLogIntervalBytes = 6 * 1024 * 1024;
    private const int MaxFileOpenRetries = 10;
    private const int FileOpenRetryDelayMs = 100;

    private readonly string _trackId;
    private string _url;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _diskRanges;
    private readonly int _totalChunks;

    private readonly ConcurrentDictionary<int, byte[]> _chunks = new();
    private readonly ConcurrentDictionary<int, Task> _pendingDownloads = new();

    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly PriorityQueue<int, int> _downloadQueue = new();
    private readonly HashSet<int> _queuedChunks = [];
    private readonly Lock _queueLock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);
    private readonly Channel<(long Pos, byte[] Data, int Len)> _diskChannel;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationTokenSource _downloadCts;

    private readonly Task _diskWriterTask;
    private Task? _downloadLoop;
    private FileStream? _cacheFile;

    private long _position;
    private long _bytesDownloaded;
    private volatile bool _downloadComplete;
    private volatile bool _disposed;
    private volatile bool _disposing;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _contentLength;

    public override long Position
    {
        get => Volatile.Read(ref _position);
        set => Seek(value, SeekOrigin.Begin);
    }

    public double DownloadProgress => _contentLength <= 0 ? 0 :
        Math.Min((double)Volatile.Read(ref _bytesDownloaded) / _contentLength * 100, 100);

    public bool IsFullyDownloaded => _downloadComplete;

    public MemoryFirstCachingStream(
        string trackId, string url, long contentLength,
        HttpClient http, StreamCacheManager cacheManager, StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _trackId = trackId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;

        _chunkSize = config.ChunkSize;
        _readAheadChunks = config.ReadAheadChunks;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        // Fallback если в конфиге пришел 0 (защита от дурака)
        _maxRamChunks = config.MaxRamChunks > 0 ? config.MaxRamChunks : 50; 
        _downloadTimeoutMs = config.DownloadTimeoutMs;

        _cachePath = cacheManager.GetCachePath(trackId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        _totalChunks = (int)((_contentLength + _chunkSize - 1) / _chunkSize);

        var meta = cacheManager.LoadOrCreateMetadata(trackId, url, contentLength);
        _diskRanges = RangeMap.Deserialize(meta.RangesJson);
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        _cacheFile = OpenCacheFileWithRetry(_cachePath);
        if (_cacheFile != null && _cacheFile.Length < _contentLength)
            _cacheFile.SetLength(_contentLength);

        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);

        Log.Info($"Opened {trackId}: {contentLength / 1024 / 1024}MB. RAM Limit: {_maxRamChunks} chunks.");
    }

    // ... (Остальные методы: OpenCacheFileWithRetry, PreBufferAsync, CancelPendingReads, Read, Seek, TryReadChunk, ReadFromDisk, EnqueueUrgent, EnqueueReadAhead, TryEnqueue, HasChunk - БЕЗ ИЗМЕНЕНИЙ) ...
    // Вставь сюда методы из оригинала, которые я не менял, чтобы не загромождать ответ
    // (OpenCacheFileWithRetry, PreBufferAsync, CancelPendingReads, Read, Seek, TryReadChunk, ReadFromDisk, EnqueueUrgent, EnqueueReadAhead, TryEnqueue, HasChunk)

    private static FileStream? OpenCacheFileWithRetry(string path)
    {
        for (int attempt = 1; attempt <= MaxFileOpenRetries; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            catch (IOException) when (attempt < MaxFileOpenRetries)
            {
                Thread.Sleep(FileOpenRetryDelayMs * attempt);
            }
            catch { return null; }
        }
        return null;
    }

    public async Task<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed || _disposing) return false;
        try {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token, _disposeCts.Token);
            var token = linkedCts.Token;
            _downloadLoop ??= Task.Run(() => DownloadLoopAsync(token), token);

            if (HasChunk(0)) return true;
            EnqueueUrgent(0);

            var sw = Stopwatch.StartNew();
            while (!HasChunk(0))
            {
                if (token.IsCancellationRequested) return false;
                if (!_dataAvailable.Wait(200, token))
                    if (sw.ElapsedMilliseconds > _downloadTimeoutMs) return false;
                if (!HasChunk(0)) _dataAvailable.Reset();
            }
            return true;
        }
        catch { return false; }
    }

    public void CancelPendingReads() { try { _disposeCts.Cancel(); } catch { } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;
        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / _chunkSize);
        int offsetInChunk = (int)(pos % _chunkSize);
        int toRead = Math.Min(count, _chunkSize - offsetInChunk);

        try
        {
            // Ожидание чанка
            while (!HasChunk(chunkIndex))
            {
                if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;
                EnqueueUrgent(chunkIndex);
                try { if (!_dataAvailable.Wait(500, _disposeCts.Token)) { } } catch { return 0; }
                if (!HasChunk(chunkIndex)) _dataAvailable.Reset();
            }

            int bytesRead = TryReadChunk(chunkIndex, offsetInChunk, buffer, offset, toRead);
            if (bytesRead > 0)
            {
                Interlocked.Add(ref _position, bytesRead);
                EnqueueReadAhead(chunkIndex);
            }
            return bytesRead;
        }
        catch { return 0; }
    }
    
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _contentLength + offset,
            _ => throw new ArgumentException("Invalid origin")
        };
        newPos = Math.Clamp(newPos, 0, _contentLength);
        Volatile.Write(ref _position, newPos);
        int newChunk = (int)(newPos / _chunkSize);
        EnqueueUrgent(newChunk);
        return newPos;
    }

    private int TryReadChunk(int idx, int off, byte[] buf, int bufOff, int count)
    {
        if (_chunks.TryGetValue(idx, out var chunk))
        {
            int usefulDataLength = (idx == _totalChunks - 1) ? (int)(_contentLength - ((long)idx * _chunkSize)) : _chunkSize;
            int available = Math.Min(count, usefulDataLength - off);
            if (available > 0) Buffer.BlockCopy(chunk, off, buf, bufOff, available);
            return available;
        }
        long start = (long)idx * _chunkSize;
        if (_diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength)))
            return ReadFromDisk(start + off, buf, bufOff, count);
        return 0;
    }

    private int ReadFromDisk(long pos, byte[] buf, int off, int count)
    {
        if (_cacheFile == null || _disposing) return 0;
        try
        {
            _fileSemaphore.Wait(_disposeCts.Token);
            try
            {
                if (_cacheFile == null) return 0;
                _cacheFile.Seek(pos, SeekOrigin.Begin);
                return _cacheFile.Read(buf, off, count);
            }
            finally { _fileSemaphore.Release(); }
        }
        catch { return 0; }
    }

    private void EnqueueUrgent(int idx)
    {
        lock (_queueLock)
        {
            TryEnqueue(idx, 0);
            for (int i = 1; i <= 3 && idx + i < _totalChunks; i++) TryEnqueue(idx + i, i);
        }
    }

    private void EnqueueReadAhead(int current)
    {
        lock (_queueLock)
        {
            for (int i = 1; i <= _readAheadChunks && current + i < _totalChunks; i++)
                TryEnqueue(current + i, 50 + i);
        }
    }

    private void TryEnqueue(int idx, int priority)
    {
        if (HasChunk(idx)) return;
        if (_pendingDownloads.ContainsKey(idx)) return;
        if (_queuedChunks.Add(idx)) _downloadQueue.Enqueue(idx, priority);
    }
    
    private bool HasChunk(int idx)
    {
        if (_chunks.ContainsKey(idx)) return true;
        long start = (long)idx * _chunkSize;
        return _diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength));
    }

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastLog = 0;

        while (!ct.IsCancellationRequested && !_disposing)
        {
            int chunk = -1;
            lock (_queueLock)
            {
                while (_downloadQueue.Count > 0)
                {
                    var c = _downloadQueue.Dequeue();
                    _queuedChunks.Remove(c);
                    if (!HasChunk(c) && !_pendingDownloads.ContainsKey(c)) { chunk = c; break; }
                }
            }

            if (chunk < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    break;
                }
                try { await Task.Delay(100, ct); } catch { break; }
                continue;
            }

            try { await _downloadSemaphore.WaitAsync(ct); } catch { break; }
            _ = DownloadChunkSafeAsync(chunk, ct);

            long bytes = Volatile.Read(ref _bytesDownloaded);
            if (bytes - lastLog >= ProgressLogIntervalBytes)
            {
                lastLog = bytes;
                // Log.Info...
            }
        }
    }

    private async Task DownloadChunkSafeAsync(int idx, CancellationToken ct)
    {
        try
        {
            await DownloadChunkAsync(idx, ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Warn($"Chunk {idx} error: {ex.Message}");
        }
        finally
        {
            _pendingDownloads.TryRemove(idx, out _);
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadChunkAsync(int idx, CancellationToken ct)
    {
        if (HasChunk(idx) || _disposing) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDownloads.TryAdd(idx, tcs.Task)) return;

        byte[]? buffer = null;
        int maxRetries = 2;
        int retry = 0;

        while (retry <= maxRetries)
        {
            try
            {
                long start = (long)idx * _chunkSize;
                long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);

                using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token);
                cts.CancelAfter(_downloadTimeoutMs);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (retry < maxRetries && _urlRefresher != null)
                    {
                        await _refreshLock.WaitAsync(cts.Token);
                        try
                        {
                             var newUrl = await _urlRefresher(cts.Token);
                             if (!string.IsNullOrEmpty(newUrl)) _url = newUrl;
                        }
                        finally { _refreshLock.Release(); }
                        retry++;
                        continue;
                    }
                }

                resp.EnsureSuccessStatusCode();

                // Rent buffer
                buffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
                using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);

                int totalRead = 0, bytesRead;
                while ((bytesRead = await netStream.ReadAsync(buffer, totalRead, _chunkSize - totalRead, cts.Token)) > 0)
                    totalRead += bytesRead;

                if (!_chunks.ContainsKey(idx) && !_disposing)
                {
                    _chunks[idx] = buffer;
                    Interlocked.Add(ref _bytesDownloaded, totalRead);
                    _dataAvailable.Set();

                    if (_cacheFile != null && !_disposing)
                    {
                        // Copy for disk write
                        byte[] diskBuf = ArrayPool<byte>.Shared.Rent(totalRead);
                        Buffer.BlockCopy(buffer, 0, diskBuf, 0, totalRead);
                        
                        // ПИШЕМ В КАНАЛ. Если канал полон и мы отменяем, writeAsync выбросит исключение,
                        // и мы должны будем вернуть diskBuf (см. finally/catch)
                        // НО здесь мы передаем владение каналу.
                        await _diskChannel.Writer.WriteAsync((start, diskBuf, totalRead), cts.Token);
                    }
                    
                    // Buffer успешно передан в _chunks, обнуляем ссылку, чтобы finally не вернул его
                    buffer = null; 
                    
                    // Trigger RAM cleanup
                    if (_chunks.Count > _maxRamChunks) TrimRamCache();
                }
                
                tcs.SetResult();
                break; 
            }
            catch (Exception ex)
            {
                if (retry >= maxRetries || ex is OperationCanceledException)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                await Task.Delay(500, ct);
                retry++;
            }
            finally
            {
                // Если buffer не null, значит мы не успели его сохранить в _chunks
                // (ошибка или отмена), возвращаем в пул.
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void TrimRamCache()
    {
        // Более строгая логика очистки
        if (_chunks.Count <= _maxRamChunks) return;

        int current = (int)(Volatile.Read(ref _position) / _chunkSize);
        
        // Удаляем все, что слишком далеко позади или слишком далеко впереди
        var toRemove = _chunks.Keys
            .Where(i => i < current - 2 || i > current + _readAheadChunks * 2)
            .ToList(); // ToList чтобы не держать лок коллекции

        foreach (var i in toRemove)
        {
            if (_chunks.TryRemove(i, out var buf))
                ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private bool IsAllDownloaded()
    {
        for (int i = 0; i < _totalChunks; i++) if (!HasChunk(i)) return false;
        return true;
    }

    // ИСПРАВЛЕННЫЙ DiskWriterLoop
    private async Task DiskWriterLoopAsync()
    {
        int bytesWrittenSinceSave = 0;
        const int SaveThreshold = 512 * 1024;

        try
        {
            // Читаем, пока канал не закроется ИЛИ пока не отменят
            while (await _diskChannel.Reader.WaitToReadAsync(_disposeCts.Token))
            {
                while (_diskChannel.Reader.TryRead(out var item))
                {
                    var (pos, data, len) = item;
                    try
                    {
                        if (_disposing || _cacheFile == null) continue; // Просто вернем буфер в finally

                        await _fileSemaphore.WaitAsync(_disposeCts.Token);
                        try
                        {
                            if (_cacheFile != null)
                            {
                                _cacheFile.Seek(pos, SeekOrigin.Begin);
                                await _cacheFile.WriteAsync(data, 0, len, _disposeCts.Token);
                            }
                        }
                        finally { _fileSemaphore.Release(); }

                        _diskRanges.MarkComplete(pos, pos + len);
                        bytesWrittenSinceSave += len;

                        if (bytesWrittenSinceSave >= SaveThreshold)
                        {
                            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
                            bytesWrittenSinceSave = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException) Log.Error($"Disk write error: {ex.Message}");
                    }
                    finally
                    {
                        // КРИТИЧЕСКИ ВАЖНО: Всегда возвращаем буфер
                        ArrayPool<byte>.Shared.Return(data);
                    }
                }
            }
        }
        catch (OperationCanceledException) 
        {
            // Нормальное завершение при Dispose
        }
        catch (Exception ex)
        {
             Log.Error($"Disk writer loop crash: {ex.Message}");
        }

        // При выходе из цикла (отмена или ошибка) может остаться мусор в канале?
        // WaitToReadAsync выбросит OperationCanceledException, и мы попадем в catch.
        // Но TryRead может не вычитать всё.
        // Поэтому очистку остатков делаем в Dispose или здесь в finally.
        // Но лучше и надежнее в Dispose, когда Writer уже Complete.
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;
        _disposed = true;

        if (disposing)
        {
            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);
            
            // 1. Закрываем Writer, чтобы никто больше не писал
            Try(() => _diskChannel.Writer.TryComplete());

            // 2. Очищаем канал и возвращаем буферы (УТЕЧКА БЫЛА ЗДЕСЬ)
            while (_diskChannel.Reader.TryRead(out var item))
            {
                ArrayPool<byte>.Shared.Return(item.Data);
            }

            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
            Try(_dataAvailable.Set);

            Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAny(_diskWriterTask, Task.Delay(1000));
                    await _fileSemaphore.WaitAsync(2000);
                    try
                    {
                        Try(() => _cacheFile?.Flush());
                        Try(() => _cacheFile?.Dispose());
                        _cacheFile = null;
                    }
                    finally { _fileSemaphore.Release(); }
                }
                catch { }
                finally
                {
                    // 3. Возвращаем все чанки из RAM кэша
                    foreach (var buf in _chunks.Values)
                        Try(() => ArrayPool<byte>.Shared.Return(buf));
                    _chunks.Clear();

                    Try(_fileSemaphore.Dispose);
                    Try(_downloadSemaphore.Dispose);
                    Try(_refreshLock.Dispose);
                    Try(_downloadCts.Dispose);
                    Try(_disposeCts.Dispose);
                }
            });
        }
        base.Dispose(disposing);
    }

    private static void Try(Action a) { try { a(); } catch { } }
}
```

```
using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

public class MusicLibraryManager(
    LibraryService library,
    YoutubeUserDataService ytUser,
    CookieAuthService auth) : ReactiveObject // Changed
{
    private readonly LibraryService _library = library;
    private readonly YoutubeUserDataService _ytUser = ytUser;
    private readonly CookieAuthService _auth = auth; // Changed

    public async Task ToggleLikeAsync(TrackInfo track)
    {
        // Сначала пытаемся отправить запрос, и только при успехе меняем локальный статус
        if (_auth.IsAuthenticated)
        {
            try
            {
                bool newStatus = !track.IsLiked;
                string rating = newStatus ? "like" : "none";
                await _ytUser.RateVideoAsync(track.Id, rating);

                // Только если не вылетело исключение:
                _library.ToggleLike(track);
                Log.Info($"[Sync] Track {track.Id} rated '{rating}' on YouTube.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like: {ex.Message}");
                // Можно добавить уведомление пользователю через событие или DialogService
            }
        }
        else
        {
            // Если оффлайн, просто меняем локально
            _library.ToggleLike(track);
        }
    }


    public async Task SyncLikedTracksAsync()
    {
        if (!_auth.IsAuthenticated) return;

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube...");
            var likedTracks = await _ytUser.GetLikedTracksAsync();
            var localLiked = _library.GetLikedPlaylist();
            int addedCount = 0;

            // Важный момент с порядком! 
            // likedTracks[0] - это самый последний лайкнутый (Newest).
            // Мы вставляем в начало списка (Insert 0).
            // Чтобы сохранить порядок [Newest, Old1, Old2...], нужно вставлять с конца.
            // Иначе получится [Old2, Old1, Newest].
            
            // Если likedTracks пуст или null, метод вернет управление без ошибок
            if (likedTracks == null || likedTracks.Count == 0) return;

            // Переворачиваем список для вставки
            var tracksToProcess = ((IEnumerable<TrackInfo>)likedTracks).Reverse();

            foreach (var track in tracksToProcess)
            {
                track.IsLiked = true;
                _library.AddOrUpdateTrack(track);

                if (!localLiked.TrackIds.Contains(track.Id))
                {
                    localLiked.TrackIds.Insert(0, track.Id);
                    track.InPlaylists.Add("liked");
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                localLiked.UpdatedAt = DateTime.Now;
                _library.AddOrUpdatePlaylist(localLiked);
                Log.Info($"[Sync] Successfully added {addedCount} new liked tracks.");
            } else
            {
                Log.Info("[Sync] No new liked tracks found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Liked tracks sync failed: {ex.Message}");
        }
    }

    public async Task CreatePlaylistAsync(string name, PlaylistSyncMode mode)
    {
        var newPlaylist = _library.CreatePlaylist(name);
        newPlaylist.SyncMode = mode;

        if (mode == PlaylistSyncMode.TwoWaySync && _auth.IsAuthenticated)
        {
            try
            {
                var ytId = await _ytUser.CreatePlaylistAsync(name, "Created via LiteMusicPlayer");
                newPlaylist.YoutubeId = ytId;
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to create remote playlist. {ex.Message}");
                newPlaylist.SyncMode = PlaylistSyncMode.LocalOnly;
            }
        }
        _library.AddOrUpdatePlaylist(newPlaylist);
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.RemovePlaylist(playlistId);

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            try
            {
                await _ytUser.DeletePlaylistAsync(playlist.YoutubeId);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Error deleting remote playlist: {ex.Message}");
            }
        }
    }

    public async Task UploadPlaylistToAccountAsync(string localPlaylistId)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = _library.GetPlaylist(localPlaylistId);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var ytId = await _ytUser.CreatePlaylistAsync(localPl.Name, "Uploaded from LiteMusicPlayer");
            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            _library.AddOrUpdatePlaylist(localPl);

            _ = Task.Run(async () =>
            {
                foreach (var trackId in localPl.TrackIds)
                {
                    if (trackId.StartsWith("yt_"))
                    {
                        await _ytUser.AddTrackToPlaylistAsync(ytId, trackId);
                        await Task.Delay(600);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Upload failed: {ex.Message}");
        }
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.AddOrUpdateTrack(track);

        if (!playlist.TrackIds.Contains(track.Id))
        {
            playlist.TrackIds.Add(track.Id);
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);
            track.InPlaylists.Add(playlistId);
        }

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            try
            {
                await _ytUser.AddTrackToPlaylistAsync(playlist.YoutubeId, track.Id);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Add track failed: {ex.Message}");
            }
        }
    }

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        if (playlist.TrackIds.Remove(trackId))
        {
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);
            var t = _library.GetTrack(trackId);
            if (t != null) t.InPlaylists.Remove(playlistId);
        }
        // Removal from YouTube via InnerTube needs extra logic (setVideoId), skipped for now
    }

    public void ConvertToLocal(string playlistId)
    {
        var pl = _library.GetPlaylist(playlistId);
        if (pl == null) return;

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = [.. pl.TrackIds],
            ThumbnailUrl = pl.ThumbnailUrl,
            Author = "Me"
        };
        _library.AddOrUpdatePlaylist(copy);
    }
}
```

```
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Models;

namespace LMP.Core.Services;

public class CachedSearchResult
{
    public string Query { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
}

public class SearchCacheService
{
    private readonly string _cacheFolder;
    private readonly int _maxCacheFiles = 50;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory LRU cache для горячих запросов
    private readonly Dictionary<string, CachedSearchResult> _memoryCache = [];
    private readonly LinkedList<string> _lruOrder = new();
    private const int MaxMemoryCacheItems = 10;

    // Ленивый доступ к LibraryService (избегаем циклических зависимостей при DI)
    private LibraryService? _libService;
    private LibraryService LibService => _libService ??= Program.Services.GetRequiredService<LibraryService>();

    /// <summary>
    /// TTL из настроек пользователя (в минутах), по умолчанию 60
    /// </summary>
    private TimeSpan CacheTtl => TimeSpan.FromMinutes(
        LibService.Data.SearchCacheTtlMinutes > 0 
            ? LibService.Data.SearchCacheTtlMinutes 
            : 60);

    public SearchCacheService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "SearchCache");
        Directory.CreateDirectory(_cacheFolder);

        // Очистка старого кэша при старте
        _ = Task.Run(CleanupOldCacheAsync);
    }

    /// <summary>
    /// Получить из кэша (память → диск)
    /// </summary>
    public async Task<List<TrackInfo>?> GetAsync(string query, int minCount = 10)
    {
        var key = GetCacheKey(query);
        var ttl = CacheTtl; // Читаем TTL один раз для консистентности

        // 1. Проверяем память
        if (_memoryCache.TryGetValue(key, out var memResult))
        {
            var age = DateTime.UtcNow - memResult.CachedAt;
            
            if (age < ttl && memResult.Tracks.Count >= minCount)
            {
                Log.Info($"[SearchCache] Memory HIT for '{query}' ({memResult.Tracks.Count} tracks, age: {age.TotalMinutes:F0}min, ttl: {ttl.TotalMinutes}min)");
                TouchLru(key);
                return memResult.Tracks;
            }
            
            if (age >= ttl)
            {
                Log.Info($"[SearchCache] Memory EXPIRED for '{query}' (age: {age.TotalMinutes:F0}min > ttl: {ttl.TotalMinutes}min)");
                lock (_memoryCache)
                {
                    _memoryCache.Remove(key);
                    _lruOrder.Remove(key);
                }
            }
        }

        // 2. Проверяем диск
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_cacheFolder, $"{key}.json");
            if (!File.Exists(filePath)) return null;

            var json = await File.ReadAllTextAsync(filePath);
            var cached = JsonSerializer.Deserialize<CachedSearchResult>(json);

            if (cached == null) return null;

            var age = DateTime.UtcNow - cached.CachedAt;

            // Проверяем TTL
            if (age > ttl)
            {
                Log.Info($"[SearchCache] Disk EXPIRED for '{query}' (age: {age.TotalMinutes:F0}min > ttl: {ttl.TotalMinutes}min)");
                File.Delete(filePath);
                return null;
            }

            if (cached.Tracks.Count < minCount)
            {
                Log.Info($"[SearchCache] Disk has only {cached.Tracks.Count} tracks, need {minCount}");
                return null;
            }

            Log.Info($"[SearchCache] Disk HIT for '{query}' ({cached.Tracks.Count} tracks, age: {age.TotalMinutes:F0}min)");

            // Добавляем в память
            AddToMemoryCache(key, cached);

            return cached.Tracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Read error: {ex.Message}");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Сохранить в кэш (память + диск)
    /// </summary>
    public async Task SetAsync(string query, List<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;

        var key = GetCacheKey(query);
        var cached = new CachedSearchResult
        {
            Query = query,
            CachedAt = DateTime.UtcNow,
            Tracks = tracks
        };

        // 1. В память
        AddToMemoryCache(key, cached);

        // 2. На диск (асинхронно)
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(_cacheFolder, $"{key}.json");
            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(filePath, json);
            Log.Info($"[SearchCache] Saved '{query}' to disk ({tracks.Count} tracks)");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Write error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Получить частичные данные для быстрого отображения
    /// </summary>
    public async Task<List<TrackInfo>> GetPartialAsync(string query, int count)
    {
        var cached = await GetAsync(query, minCount: 1);
        return cached?.Take(count).ToList() ?? [];
    }

    private void AddToMemoryCache(string key, CachedSearchResult result)
    {
        lock (_memoryCache)
        {
            if (_memoryCache.ContainsKey(key))
            {
                _memoryCache[key] = result;
                TouchLru(key);
            }
            else
            {
                // Удаляем старые если превышен лимит
                while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();
                    _memoryCache.Remove(oldest);
                }

                _memoryCache[key] = result;
                _lruOrder.AddFirst(key);
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_memoryCache)
        {
            _lruOrder.Remove(key);
            _lruOrder.AddFirst(key);
        }
    }

    private async Task CleanupOldCacheAsync()
    {
        try
        {
            var ttl = CacheTtl;
            var files = Directory.GetFiles(_cacheFolder, "*.json")
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ToList();

            int deletedCount = 0;

            // Удаляем старые файлы (превышение лимита)
            foreach (var file in files.Skip(_maxCacheFiles))
            {
                file.Delete();
                deletedCount++;
                Log.Info($"[SearchCache] Deleted excess cache: {file.Name}");
            }

            // Удаляем просроченные по TTL
            foreach (var file in files.Take(_maxCacheFiles))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > ttl)
                {
                    file.Delete();
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                Log.Info($"[SearchCache] Cleanup: deleted {deletedCount} files (ttl: {ttl.TotalMinutes}min)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Cleanup error: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private static string GetCacheKey(string query)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(query.ToLowerInvariant().Trim()));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Инвалидирует кэш для конкретного запроса
    /// </summary>
    public void InvalidateQuery(string query)
    {
        var key = GetCacheKey(query);

        lock (_memoryCache)
        {
            _memoryCache.Remove(key);
            _lruOrder.Remove(key);
        }

        try
        {
            var filePath = Path.Combine(_cacheFolder, $"{key}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log.Info($"[SearchCache] Invalidated '{query}'");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Invalidate error: {ex.Message}");
        }
    }

    public void ClearAll()
    {
        lock (_memoryCache)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }

        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder, "*.json"))
            {
                File.Delete(file);
            }
            Log.Info("[SearchCache] Cleared all cache");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Clear error: {ex.Message}");
        }
    }

    /// <summary>
    /// Принудительная очистка просроченных записей
    /// Вызывается при изменении TTL в настройках
    /// </summary>
    public async Task CleanupExpiredAsync()
    {
        await CleanupOldCacheAsync();
        
        // Также чистим память
        var ttl = CacheTtl;
        var now = DateTime.UtcNow;
        
        lock (_memoryCache)
        {
            var expiredKeys = _memoryCache
                .Where(kv => now - kv.Value.CachedAt > ttl)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _memoryCache.Remove(key);
                _lruOrder.Remove(key);
            }

            if (expiredKeys.Count > 0)
            {
                Log.Info($"[SearchCache] Cleaned {expiredKeys.Count} expired memory entries");
            }
        }
    }

    /// <summary>
    /// Статистика кэша
    /// </summary>
    public (int MemoryItems, int DiskItems, long DiskSizeBytes, int TtlMinutes) GetStats()
    {
        int memCount = _memoryCache.Count;
        var files = Directory.GetFiles(_cacheFolder, "*.json");
        long size = files.Sum(static f => new FileInfo(f).Length);
        int ttl = (int)CacheTtl.TotalMinutes;
        return (memCount, files.Length, size, ttl);
    }
}


```

```

// === ФАЙЛ: Core/Services/StreamCacheManager.cs ===
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LMP.Core.Models;

namespace LMP.Core.Services;

public class StreamCacheMetadata
{
    public string TrackId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public string RangesJson { get; set; } = "[]";
    public string Codec { get; set; } = "";
    public int Bitrate { get; set; }
    public string Container { get; set; } = "";
}

public class StreamCacheManager : IDisposable
{
    private readonly LibraryService _library;
    private readonly string _cacheFolder;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    public StreamCacheManager(LibraryService library)
    {
        _library = library;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheFolder = Path.Combine(appData, "LiteMusicPlayer", "StreamCache");
        Directory.CreateDirectory(_cacheFolder);
        _ = Task.Run(CleanupOldCacheAsync);
    }

    public string GetCachePath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(_cacheFolder, $"{safeId}.cache");
    }

    public string GetMetaPath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(_cacheFolder, $"{safeId}.meta");
    }

    public StreamCacheMetadata? TryGetMetadata(string trackId)
    {
        var metaPath = GetMetaPath(trackId);
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<StreamCacheMetadata>(json);
        }
        catch { return null; }
    }

    public StreamCacheMetadata LoadOrCreateMetadata(string trackId, string url, long contentLength)
    {
        var meta = TryGetMetadata(trackId);

        if (meta != null && meta.ContentLength == contentLength)
        {
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
            return meta;
        }

        var newMeta = new StreamCacheMetadata
        {
            TrackId = trackId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]"
        };

        var cachePath = GetCachePath(trackId);
        if (File.Exists(cachePath)) try { File.Delete(cachePath); } catch { }

        SaveMetadata(trackId, newMeta);
        return newMeta;
    }

    public void SaveMetadata(string trackId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(trackId);
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    public void UpdateRanges(string trackId, RangeMap ranges)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            meta.RangesJson = ranges.Serialize();
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
        }
    }

    public void UpdateStreamInfo(string trackId, string codec, int bitrate, string container)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            meta.Codec = codec;
            meta.Bitrate = bitrate;
            meta.Container = container;
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
        }
    }

    public RangeMap LoadRanges(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        return meta != null ? RangeMap.Deserialize(meta.RangesJson) : new RangeMap();
    }

    public bool IsFullyCached(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        if (meta == null) return false;
        if (!File.Exists(GetCachePath(trackId))) return false;
        var ranges = RangeMap.Deserialize(meta.RangesJson);
        return ranges.IsFullyDownloaded(meta.ContentLength);
    }
    
    public async Task ClearAllAsync()
    {
        await _cleanupLock.WaitAsync();
        try
        {
            foreach (var file in Directory.GetFiles(_cacheFolder))
            {
                try { File.Delete(file); } catch { }
            }
            Log.Info("All stream cache cleared");
        }
        finally { _cleanupLock.Release(); }
    }
    
    public (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(_cacheFolder, "*.cache");
            long size = files.Sum(static f => new FileInfo(f).Length);
            return (files.Length, size / 1024 / 1024);
        }
        catch { return (0, 0); }
    }

    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;

        try
        {
            var files = Directory.GetFiles(_cacheFolder, "*.cache")
                .Select(static f => new FileInfo(f))
                .ToList();

            long totalSize = files.Sum(static f => f.Length);
            long maxCacheBytes = (long)_library.Data.Storage.AudioCacheLimitMb * 1024 * 1024;

            if (totalSize <= maxCacheBytes) return;

            Log.Info($"Stream cache size {totalSize / 1024 / 1024}MB exceeds limit {maxCacheBytes / 1024 / 1024}MB, cleaning...");

            var metaFiles = files
                .Select(static f => new
                {
                    CacheFile = f,
                    MetaFile = new FileInfo(Path.ChangeExtension(f.FullName, ".meta")),
                    LastAccess = GetLastAccessTime(Path.ChangeExtension(f.FullName, ".meta"))
                })
                .OrderBy(static x => x.LastAccess)
                .ToList();

            long targetSize = maxCacheBytes * 70 / 100;
            long deleted = 0;

            foreach (var item in metaFiles)
            {
                if (totalSize - deleted <= targetSize) break;
                try
                {
                    var size = item.CacheFile.Length;
                    item.CacheFile.Delete();
                    if (item.MetaFile.Exists) item.MetaFile.Delete();
                    deleted += size;
                }
                catch { }
            }
            Log.Info($"Cleaned {deleted / 1024 / 1024}MB");
        }
        finally { _cleanupLock.Release(); }
    }

    private static DateTime GetLastAccessTime(string metaPath)
    {
        try
        {
            if (File.Exists(metaPath))
            {
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("LastAccessedAt", out var prop) && prop.TryGetDateTime(out var dt))
                    return dt;
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    private static string GetSafeFileName(string trackId)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(trackId));
        return Convert.ToHexString(bytes)[..32];
    }

    public void Dispose() => _cleanupLock.Dispose();
}
```

```
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.Core.Services;

/// <summary>
/// Настройки темы приложения.
/// Все цвета хранятся в HEX-формате (#RRGGBB или #AARRGGBB).
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>Имя темы для отображения</summary>
    public string Name { get; set; } = "Paralax Purple";

    // BACKGROUNDS - Фоновые цвета

    /// <summary>Основной фон окна</summary>
    public string BgPrimary { get; set; } = "#0F0B15";

    /// <summary>Фон карточек и сайдбара</summary>
    public string BgSecondary { get; set; } = "#1A1625";

    /// <summary>Фон диалогов и меню</summary>
    public string BgElevated { get; set; } = "#252033";

    /// <summary>Разделители и границы</summary>
    public string BgHighlight { get; set; } = "#322A45";

    /// <summary>Hover-состояние</summary>
    public string BgHover { get; set; } = "#3E3456";

    // SKELETON / LOADING - Цвета загрузки

    /// <summary>Скелетон светлый</summary>
    public string BgSkeleton { get; set; } = "#2A2438";

    /// <summary>Скелетон темный</summary>
    public string BgSkeletonDeep { get; set; } = "#15121C";

    /// <summary>Оверлей (полупрозрачный)</summary>
    public string BgOverlay { get; set; } = "#CC0A080F";

    // ACCENT - Акцентные цвета бренда

    /// <summary>Основной акцентный цвет (кнопки, ссылки)</summary>
    public string AccentColor { get; set; } = "#8A2BE2";

    /// <summary>Акцент при наведении</summary>
    public string AccentHover { get; set; } = "#A560F0";

    // SEMANTIC - Системные цвета

    /// <summary>Цвет ошибки</summary>
    public string SystemError { get; set; } = "#FF5555";

    /// <summary>Фон ошибки</summary>
    public string SystemErrorBg { get; set; } = "#331010";

    /// <summary>Информационный цвет</summary>
    public string SystemInfoBlue { get; set; } = "#8BE9FD";

    /// <summary>Предупреждение</summary>
    public string SystemWarnOrange { get; set; } = "#FFB86C";

    // TEXT - Цвета текста

    /// <summary>Основной текст</summary>
    public string TextPrimary { get; set; } = "#F8F8F2";

    /// <summary>Вторичный текст (подзаголовки)</summary>
    public string TextSecondary { get; set; } = "#BFB6D3";

    /// <summary>Приглушенный текст</summary>
    public string TextMuted { get; set; } = "#6272A4";

    /// <summary>Темный текст (на светлом фоне)</summary>
    public string TextDark { get; set; } = "#0F0B15";

    // SERIALIZATION

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }
}

/// <summary>
/// Сервис управления темами приложения.
/// Отвечает за загрузку, сохранение и применение тем.
/// </summary>
public sealed class ThemeManagerService
{
    private const string ThemeFileName = "theme.json";
    private readonly string _themePath;
    private ThemeSettings? _cachedTheme;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ThemeManagerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        Directory.CreateDirectory(appFolder);
        _themePath = Path.Combine(appFolder, ThemeFileName);
    }

    // PUBLIC API

    /// <summary>
    /// Загружает и применяет тему при старте приложения
    /// </summary>
    public void LoadAndApplyThemeOnStartup()
    {
        var theme = LoadThemeFromDisk();
        ApplyTheme(theme);
    }

    /// <summary>
    /// Применяет тему к ресурсам приложения
    /// </summary>
    public void ApplyTheme(ThemeSettings theme)
    {
        if (Application.Current?.Resources is not { } resources)
            return;

        _cachedTheme = theme;

        // Backgrounds
        SetColor(resources, "BgPrimary", theme.BgPrimary);
        SetColor(resources, "BgSecondary", theme.BgSecondary);
        SetColor(resources, "BgElevated", theme.BgElevated);
        SetColor(resources, "BgHighlight", theme.BgHighlight);
        SetColor(resources, "BgHover", theme.BgHover);
        SetColor(resources, "BgSkeleton", theme.BgSkeleton);
        SetColor(resources, "BgSkeletonDeep", theme.BgSkeletonDeep);
        SetColor(resources, "BgOverlay", theme.BgOverlay);

        // Accent
        SetColor(resources, "Accent", theme.AccentColor);
        SetColor(resources, "AccentHover", theme.AccentHover);

        // Semantic
        SetColor(resources, "SystemError", theme.SystemError);
        SetColor(resources, "SystemErrorBg", theme.SystemErrorBg);
        SetColor(resources, "SystemInfoBlue", theme.SystemInfoBlue);
        SetColor(resources, "SystemWarnOrange", theme.SystemWarnOrange);

        // Text
        SetColor(resources, "TextPrimary", theme.TextPrimary);
        SetColor(resources, "TextSecondary", theme.TextSecondary);
        SetColor(resources, "TextMuted", theme.TextMuted);
        SetColor(resources, "TextDark", theme.TextDark);

        // System accent compatibility
        if (TryParseColor(theme.AccentColor, out var accent))
        {
            resources["SystemAccentColor"] = accent;
            if (TryParseColor(theme.AccentHover, out var accentHover))
                resources["SystemAccentColorLight1"] = accentHover;
        }

        Log.Info($"Theme '{theme.Name}' applied.");
    }

    /// <summary>
    /// Сохраняет тему на диск
    /// </summary>
    public void SaveTheme(ThemeSettings theme)
    {
        try
        {
            var json = JsonSerializer.Serialize(theme, JsonOptions);
            File.WriteAllText(_themePath, json);
            _cachedTheme = theme;
            Log.Info($"Theme '{theme.Name}' saved.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save theme: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает текущую загруженную тему
    /// </summary>
    public ThemeSettings GetCurrentTheme()
    {
        return _cachedTheme ?? LoadThemeFromDisk();
    }

    /// <summary>
    /// Возвращает дефолтную тему (Paralax Purple)
    /// </summary>
    public static ThemeSettings GetDefaultTheme() => new() { IsBuiltIn = true };

    /// <summary>
    /// Сбрасывает тему к дефолтной
    /// </summary>
    public void ResetToDefault()
    {
        try
        {
            if (File.Exists(_themePath))
                File.Delete(_themePath);
        }
        catch { /* Игнорируем ошибку удаления */ }

        var def = GetDefaultTheme();
        SaveTheme(def);
        ApplyTheme(def);
    }

    /// <summary>
    /// Возвращает список встроенных пресетов тем
    /// </summary>
    public static IReadOnlyList<ThemeSettings> GetBuiltInPresets() =>
    [
        // ═══ 1. PARALAX PURPLE (Default) ═══
        new ThemeSettings { IsBuiltIn = true },

        // ═══ 2. CLASSIC GREEN (Spotify-like) ═══
        new ThemeSettings
        {
            Name = "Classic Green",
            IsBuiltIn = true,
            BgPrimary = "#121212",
            BgSecondary = "#1E1E1E",
            BgElevated = "#282828",
            BgHighlight = "#404040",
            BgHover = "#505050",
            BgSkeleton = "#282828",
            BgSkeletonDeep = "#1a1a1a",
            BgOverlay = "#CC121212",
            AccentColor = "#1DB954",
            AccentHover = "#1ED760",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#B3B3B3",
            TextMuted = "#888888",
            TextDark = "#000000"
        },

        // ═══ 3. OCEAN DEEP ═══
        new ThemeSettings
        {
            Name = "Ocean Deep",
            IsBuiltIn = true,
            BgPrimary = "#001219",
            BgSecondary = "#001f2d",
            BgElevated = "#002d42",
            BgHighlight = "#003b57",
            BgHover = "#00506b",
            BgSkeleton = "#002535",
            BgSkeletonDeep = "#000d12",
            BgOverlay = "#CC001219",
            AccentColor = "#00B4D8",
            AccentHover = "#48CAE4",
            TextPrimary = "#E0FBFC",
            TextSecondary = "#98C1D9",
            TextMuted = "#5B8FA8",
            TextDark = "#001219"
        },

        // ═══ 4. AMOLED BLACK ═══
        new ThemeSettings
        {
            Name = "AMOLED Black",
            IsBuiltIn = true,
            BgPrimary = "#000000",
            BgSecondary = "#0A0A0A",
            BgElevated = "#141414",
            BgHighlight = "#1F1F1F",
            BgHover = "#2A2A2A",
            BgSkeleton = "#141414",
            BgSkeletonDeep = "#050505",
            BgOverlay = "#CC000000",
            AccentColor = "#FFFFFF",
            AccentHover = "#E0E0E0",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#A0A0A0",
            TextMuted = "#606060",
            TextDark = "#000000"
        },

        // ═══ 5. WARM SUNSET ═══
        new ThemeSettings
        {
            Name = "Warm Sunset",
            IsBuiltIn = true,
            BgPrimary = "#1A1210",
            BgSecondary = "#261A16",
            BgElevated = "#33221C",
            BgHighlight = "#4A3228",
            BgHover = "#5C3F32",
            BgSkeleton = "#2A1C18",
            BgSkeletonDeep = "#120E0C",
            BgOverlay = "#CC1A1210",
            AccentColor = "#FF6B35",
            AccentHover = "#FF8C5A",
            TextPrimary = "#FFF5F0",
            TextSecondary = "#D4B5A5",
            TextMuted = "#8B7265",
            TextDark = "#1A1210"
        },

        // ═══ 6. DRACULA ═══
        new ThemeSettings
        {
            Name = "Dracula",
            IsBuiltIn = true,
            BgPrimary = "#282a36",
            BgSecondary = "#21222c",
            BgElevated = "#343746",
            BgHighlight = "#44475a",
            BgHover = "#4d5066",
            BgSkeleton = "#343746",
            BgSkeletonDeep = "#1e1f29",
            BgOverlay = "#CC282a36",
            AccentColor = "#bd93f9",
            AccentHover = "#d4b8ff",
            TextPrimary = "#f8f8f2",
            TextSecondary = "#bfbfbf",
            TextMuted = "#6272a4",
            TextDark = "#282a36"
        }
    ];

    // PRIVATE HELPERS

    private ThemeSettings LoadThemeFromDisk()
    {
        try
        {
            if (File.Exists(_themePath))
            {
                var json = File.ReadAllText(_themePath);
                var theme = JsonSerializer.Deserialize<ThemeSettings>(json, JsonOptions);
                if (theme != null)
                {
                    _cachedTheme = theme;
                    return theme;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load theme: {ex.Message}");
        }

        // Сохраняем дефолтную при первом запуске
        var def = GetDefaultTheme();
        SaveTheme(def);
        return def;
    }

    private static void SetColor(IResourceDictionary resources, string key, string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            Log.Error($"Invalid color: {key}={hex}");
            color = Colors.Magenta; // Яркий цвет для отладки
        }

        resources[key] = color;
        resources[$"{key}Brush"] = new SolidColorBrush(color);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

```

using LMP.Core.Youtube;

using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Playlist = LMP.Core.Models.Playlist;
using LMP.Core.Youtube.Channels;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Net;

namespace LMP.Core.Services;

public partial class YoutubeProvider : IDisposable
{
    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    private YoutubeClient _youtube = null!;
    private readonly CookieAuthService _cookieAuth;
    private readonly Timer? _cookieSyncTimer;
    private CookieContainer? _activeContainer; // Ссылка на текущий контейнер
    private readonly string _downloadFolder;
    private readonly LibraryService? _libraryService;

    private readonly Dictionary<string, StreamCacheEntry> _streamCache = [];
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);

    private class StreamCacheEntry
    {
        public required string Url { get; init; }
        public long Size { get; init; }
        public int Bitrate { get; init; }
        public required string Codec { get; init; }
        public required string Container { get; init; }
        public DateTime Obtained { get; init; }
    }

    public bool IsReady { get; private set; }

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    public YoutubeProvider() : this(null!, null!)
    {
    }

    public YoutubeProvider(LibraryService? libraryService, CookieAuthService cookieAuth)
    {
        _libraryService = libraryService;
        _cookieAuth = cookieAuth;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "LiteMusicPlayer");
        _downloadFolder = Path.Combine(appFolder, "Downloads");
        Directory.CreateDirectory(_downloadFolder);

        if (_cookieAuth != null)
        {
            ReloadClient();
            _cookieAuth.OnAuthStateChanged += ReloadClient;

            // Запускаем таймер: старт через 1 мин, повтор каждые 3 мин
            _cookieSyncTimer = new Timer(SyncCookiesCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3));
        }
    }

    private void SyncCookiesCallback(object? state)
    {
        if (_activeContainer != null)
        {
            _cookieAuth.SyncCookiesFromContainer(_activeContainer);
        }
    }

    public void ReloadClient()
    {
        // Получаем контейнер и сохраняем ссылку на него в классе
        _activeContainer = _cookieAuth.GetCookieContainer();

        var handler = new HttpClientHandler
        {
            CookieContainer = _activeContainer, // Используем этот инстанс
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false
        };

        var baseHttpClient = new HttpClient(handler);
        // Важно: передаем тот же _activeContainer в наш Handler для генерации хешей
        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, _activeContainer, disposeClient: true);
        var finalHttpClient = new HttpClient(youtubeHandler, disposeHandler: true);

        _youtube = new YoutubeClient(finalHttpClient);

        Log.Info($"[YouTube] Client reloaded. Cookies: {_activeContainer.Count}");
    }

    public Task InitializeAsync()
    {
        IsReady = true;
        NotifyStatus("[YouTube] Initialized");
        return Task.CompletedTask;
    }

    public YoutubeClient GetClient() => _youtube;

    // --- ПЕРСОНАЛИЗАЦИЯ ---

    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (!_cookieAuth.IsAuthenticated) return [];

        try
        {
            // MusicClient теперь возвращает MusicShelf, который нужно сконвертировать в HomeSection
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);
            var sections = new List<HomeSection>();

            foreach (var shelf in shelves)
            {
                var section = new HomeSection { Title = shelf.Title };

                foreach (var item in shelf.Items)
                {
                    // MusicItem -> TrackInfo
                    // Выбираем лучшее превью
                    var thumbUrl = item.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url
                                   ?? $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg";

                    var track = new TrackInfo
                    {
                        Id = "yt_" + item.Id,
                        Title = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = thumbUrl,
                        Duration = item.Duration ?? TimeSpan.Zero,
                        IsMusic = true,
                        Url = $"https://music.youtube.com/watch?v={item.Id}"
                    };

                    if (item.Type == "Playlist" || item.Type == "Album")
                    {
                        track.Id = "yt_pl_" + item.Id;
                    }

                    section.Tracks.Add(track);
                }

                if (section.Tracks.Count > 0)
                    sections.Add(section);
            }

            return sections;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to get home: {ex.Message}");
            return [];
        }
    }

    // --- ЛАЙКИ И ПЛЕЙЛИСТЫ (WRITE) ---

    public async Task LikeTrackAsync(string trackId, bool like)
    {
        if (!_cookieAuth.IsAuthenticated) return;
        try
        {
            var vid = trackId.Replace("yt_", "");
            await _youtube.Music.LikeTrackAsync(vid, like);
            Log.Info($"[Music] Liked status set to {like} for {vid}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw;
        }
    }

    public async Task<string?> CreatePlaylistAsync(string title)
    {
        if (!_cookieAuth.IsAuthenticated) return null;
        try
        {
            var id = await _youtube.Music.CreatePlaylistAsync(title);
            return id;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    public async Task AddToPlaylistAsync(string playlistId, string trackId)
    {
        if (!_cookieAuth.IsAuthenticated) return;
        try
        {
            await _youtube.Music.AddToPlaylistAsync(playlistId, trackId.Replace("yt_", ""));
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to add to playlist: {ex.Message}");
        }
    }

    #region RefreshStreamUrlAsync

    public async Task<(string Url, long Size, int Bitrate, string Codec, string Container)?> RefreshStreamUrlAsync(
       TrackInfo track,
       bool forceRefresh = false,
       CancellationToken ct = default)
    {
        string? videoId = ExtractVideoIdFromTrack(track);
        if (string.IsNullOrEmpty(videoId))
        {
            NotifyError("[YouTube] Could not extract video ID");
            return null;
        }

        var sw = Stopwatch.StartNew();

        if (forceRefresh)
            NotifyStatus($"[YouTube] [{videoId}] 403 detected. Forcing stream URL refresh...");
        else
            NotifyStatus($"[YouTube] [{videoId}] Getting stream URL...");

        string? targetContainer = track.TransientContainer;
        int targetBitrate = track.TransientBitrate;

        if (string.IsNullOrEmpty(targetContainer))
        {
            if (_libraryService?.Data.RememberTrackFormat == true)
            {
                targetContainer = track.PreferredContainer;
                targetBitrate = track.PreferredBitrate;
            }
        }

        string cacheKey = GenerateCacheKey(videoId, targetContainer, targetBitrate);

        if (!forceRefresh && TryGetFromCache(cacheKey, out var cached))
        {
            track.StreamUrl = cached.Url;
            NotifyStatus($"[YouTube] [{videoId}] Using cached URL ({cached.Codec}/{cached.Bitrate}kbps)");
            return (cached.Url, cached.Size, cached.Bitrate, cached.Codec, cached.Container);
        }

        try
        {
            // VideoId.Parse нужен, так как GetManifestAsync принимает структуру VideoId
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
            {
                NotifyError($"[YouTube] [{videoId}] No audio streams found");
                return null;
            }

            AudioOnlyStreamInfo? selectedStream = SelectBestStream(audioStreams, targetContainer, targetBitrate);

            if (selectedStream == null)
            {
                NotifyError($"[YouTube] [{videoId}] Could not select audio stream");
                return null;
            }

            var url = selectedStream.Url;
            var size = selectedStream.Size.Bytes;
            var bitrate = (int)selectedStream.Bitrate.KiloBitsPerSecond;
            var container = selectedStream.Container.Name;
            var codec = DetermineCodec(container, selectedStream);

            sw.Stop();
            NotifyStatus($"[YouTube] [{videoId}] Got stream: {codec}/{bitrate}kbps ({container}) in {sw.ElapsedMilliseconds}ms");

            CacheStreamUrl(cacheKey, url, size, bitrate, codec, container);

            track.StreamUrl = url;
            return (url, size, bitrate, codec, container);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] [{videoId}] Error: {ex.Message}");
            return null;
        }
    }

    private AudioOnlyStreamInfo? SelectBestStream(
        List<AudioOnlyStreamInfo> streams,
        string? preferredContainer,
        int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        if (!string.IsNullOrEmpty(preferredContainer))
        {
            var containerStreams = streams.Where(s =>
                s.Container.Name.Equals(preferredContainer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (containerStreams.Count > 0)
            {
                if (preferredBitrate > 0)
                {
                    return containerStreams.MinBy(s => Math.Abs(s.Bitrate.KiloBitsPerSecond - preferredBitrate));
                }
                return containerStreams.First();
            }
        }

        var qualityPref = _libraryService?.Data.QualityPreference ?? AudioQualityPreference.BestAvailable;

        return qualityPref switch
        {
            AudioQualityPreference.BestAvailable => streams.FirstOrDefault(),
            AudioQualityPreference.Standard => streams.FirstOrDefault(s => s.Container.Name == "mp4")
                                ?? streams.FirstOrDefault(),
            _ => streams.FirstOrDefault(),
        };
    }

    private static string DetermineCodec(string container, AudioOnlyStreamInfo stream)
    {
        var codecStr = stream.AudioCodec;

        if (!string.IsNullOrEmpty(codecStr))
        {
            if (codecStr.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
            if (codecStr.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (codecStr.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (codecStr.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        }

        return container.ToLower() switch
        {
            "webm" => "Opus",
            "mp4" => "AAC",
            "m4a" => "AAC",
            _ => container.ToUpper()
        };
    }

    public async Task<List<StreamOption>> GetStreamOptionsAsync(string videoId)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId)) return [];

        try
        {
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId);

            return [.. manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .Select(s => new StreamOption
                {
                    Container = s.Container.Name,
                    Bitrate = s.Bitrate.KiloBitsPerSecond,
                    Codec = DetermineCodec(s.Container.Name, s),
                    SizeMb = s.Size.MegaBytes
                })];
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetStreamOptions error: {ex.Message}");
            return [];
        }
    }

    #endregion

    #region Cache

    private static string GenerateCacheKey(string videoId, string? container, int bitrate = 0)
    {
        var key = string.IsNullOrEmpty(container) ? videoId : $"{videoId}_{container}";
        if (bitrate > 0) key += $"_{bitrate}";
        return key;
    }

    private bool TryGetFromCache(string cacheKey, out StreamCacheEntry result)
    {
        if (_streamCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Obtained < _streamCacheLifetime)
            {
                result = cached;
                return true;
            }
            _streamCache.Remove(cacheKey);
        }

        result = null!;
        return false;
    }

    private void CacheStreamUrl(string cacheKey, string url, long size, int bitrate, string codec, string container)
    {
        _streamCache[cacheKey] = new StreamCacheEntry
        {
            Url = url,
            Size = size,
            Bitrate = bitrate,
            Codec = codec,
            Container = container,
            Obtained = DateTime.UtcNow
        };

        if (_streamCache.Count > MaxCacheSize) CleanupExpiredCache();
    }

    private void CleanupExpiredCache()
    {
        var expired = _streamCache
            .Where(kv => DateTime.UtcNow - kv.Value.Obtained > _streamCacheLifetime)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired) _streamCache.Remove(key);
    }

    public void ClearCache()
    {
        _streamCache.Clear();
        Log.Info("[YouTube] Stream cache cleared");
    }

    #endregion

    #region Search, Playlist, etc.

    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return QueryType.DirectUrl;
        }

        return QueryType.Search;
    }

    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        if (match.Success) return match.Groups[1].Value;
        try { return VideoId.TryParse(url)?.Value; } catch { return null; }
    }

    private static string? ExtractVideoIdFromTrack(TrackInfo track)
    {
        string cleanId = track.Id?.Trim() ?? "";
        if (cleanId.StartsWith("yt_"))
        {
            var rawId = cleanId[3..];
            var safeId = new string([.. rawId.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);
            if (ValidYoutubeId.IsMatch(safeId)) return safeId;
        }
        if (!string.IsNullOrWhiteSpace(track.Url)) return ExtractVideoId(track.Url);
        return null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            // VideoClient теперь возвращает TrackInfo
            var track = await _youtube.Videos.GetAsync(videoId);
            return track;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    #region Search

    public async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) yield break;

        var sw = Stopwatch.StartNew();
        int count = 0;

        NotifyStatus($"[YouTube] Starting streaming search for '{query}' (Filter: {filter})...");

        // SearchClient возвращает Batch<ISearchResult>
        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var tracks = new List<TrackInfo>();

            foreach (var result in batch.Items)
            {
                if (count >= maxResults) break;

                // TrackInfo реализует ISearchResult, поэтому просто кастим
                if (result is TrackInfo track)
                {
                    tracks.Add(track);
                    count++;
                }
                // Плейлисты (Playlist) тоже реализуют ISearchResult, но для списка треков они не подходят
                // Если нужны и плейлисты, их можно обрабатывать отдельно, но здесь возвращаем List<TrackInfo>
            }

            if (tracks.Count > 0)
            {
                NotifyStatus($"[YouTube] Got batch: +{tracks.Count} items (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        NotifyStatus($"[YouTube] Search complete: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    public async Task<List<TrackInfo>> SearchFastAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.Video,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var sw = Stopwatch.StartNew();
        var results = new List<TrackInfo>(maxResults);

        try
        {
            // Используем специализированный метод для получения треков
            var enumerable = _youtube.Search.GetVideosAsync(query, ct);

            await foreach (var track in enumerable)
            {
                if (results.Count >= maxResults) break;
                results.Add(track);
            }

            sw.Stop();
            NotifyStatus($"[YouTube] Fast search '{query}' (Filter: {filter}): {results.Count} results in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            NotifyStatus($"[YouTube] Search cancelled after {results.Count} results");
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] SearchFastAsync error: {ex.Message}");
        }

        return results;
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 100)
    {
        return await SearchFastAsync(query, maxResults, SearchFilter.Video);
    }

    /// <summary>
    /// Поиск с поддержкой продолжения (continuation) для ленивой загрузки
    /// </summary>
    public class SearchSession : IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly SearchFilter _filter; // Храним фильтр
        private readonly HashSet<string> _seenIds = [];
        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private bool _disposed;
        private readonly List<TrackInfo> _buffer = [];

        public bool HasMore => (_hasMore || _buffer.Count > 0) && !_disposed && _seenIds.Count < _maxResults;
        public int LoadedCount => _seenIds.Count;

        // Конструктор обновлен для приема SearchFilter
        internal SearchSession(
            YoutubeClient youtube,
            string query,
            int maxResults = 300,
            SearchFilter filter = SearchFilter.Video,
            IEnumerable<string>? skipTrackIds = null)
        {
            _youtube = youtube;
            _query = query;
            _maxResults = maxResults;
            _filter = filter;
            _seenIds = [];

            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    var cleanId = id.StartsWith("yt_") ? id[3..] : id;
                    _seenIds.Add(cleanId);
                }
            }
        }

        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 50, CancellationToken ct = default)
        {
            if (_disposed || _seenIds.Count >= _maxResults) return [];

            var results = new List<TrackInfo>();

            while (results.Count < count && _buffer.Count > 0)
            {
                results.Add(_buffer[0]);
                _buffer.RemoveAt(0);
            }

            while (results.Count < count && _hasMore && _seenIds.Count < _maxResults)
            {
                try
                {
                    // Используем _filter при создании перечислителя
                    _enumerator ??= _youtube.Search
                        .GetResultBatchesAsync(_query, _filter, ct)
                        .GetAsyncEnumerator(ct);

                    if (!await _enumerator.MoveNextAsync())
                    {
                        _hasMore = false;
                        break;
                    }

                    var batch = _enumerator.Current;

                    foreach (var item in batch.Items)
                    {
                        if (_seenIds.Count >= _maxResults) break;

                        string? id = null;
                        TrackInfo? track = null;

                        // Логика обработки разных типов результатов
                        if (item is VideoSearchResult video)
                        {
                            id = video.Id.Value;
                            if (_seenIds.Add(id))
                                track = ConvertSearchResultToTrackInfo(video);
                        }
                        else if (item is PlaylistSearchResult playlist)
                        {
                            id = playlist.Id.Value;
                            if (_seenIds.Add(id))
                                track = ConvertPlaylistSearchResultToTrackInfo(playlist);
                        }

                        if (track != null)
                        {
                            if (results.Count < count)
                                results.Add(track);
                            else
                                _buffer.Add(track);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error($"[SearchSession] Error: {ex.Message}");
                    _hasMore = false;
                    break;
                }
            }

            return results;
        }

        // ... (Dispose метод остается прежним)
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hasMore = false;
            _buffer.Clear();

            if (_enumerator != null)
            {
                _ = _enumerator.DisposeAsync().AsTask();
            }
        }
    }

    private SearchSession? _currentSearchSession;

    /// <summary>
    /// Создает сессию поиска для ленивой загрузки (с поддержкой фильтра)
    /// </summary>
    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, query, maxResults, filter, skipTrackIds);

        var skipCount = skipTrackIds?.Count() ?? 0;
        NotifyStatus($"[YouTube] Created search session for '{query}' (max: {maxResults}, filter: {filter})");

        return _currentSearchSession;
    }

    /// <summary>
    /// Быстрый первоначальный поиск с сессией
    /// </summary>
    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 50,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video, // Добавлен аргумент
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return ([], null!);

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults, filter);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct);

        sw.Stop();
        NotifyStatus($"[YouTube] Initial search '{query}': {tracks.Count} results in {sw.ElapsedMilliseconds}ms (Filter: {filter})");

        return (tracks, session);
    }
    #endregion

    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;
        try
        {
            var playlistId = PlaylistId.TryParse(url);
            if (playlistId == null) return null;

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            // GetVideosAsync возвращает IAsyncEnumerable<TrackInfo>
            var tracks = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            NotifyStatus($"[YouTube] Playlist '{playlist.Name}': {tracks.Count} tracks");
            return (playlist.Name, tracks.ToList());
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<(string ChannelName, List<PlaylistSearchResult> Playlists)?> GetChannelPlaylistsForSyncAsync(string channelUrl, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(channelUrl, ct);
        if (channel is null) return null;

        NotifyStatus($"[YouTube] Fetching playlists from channel: {channel.Title}...");

        try
        {
            var results = new List<PlaylistSearchResult>();

            // GetPlaylistsAsync возвращает IAsyncEnumerable<Playlist>
            await foreach (var pl in _youtube.Channels.GetPlaylistsAsync(channel.Id, ct))
            {
                if (pl.Name.Equals("Uploads", StringComparison.OrdinalIgnoreCase)) continue;

                // Маппим наш LMP.Core.Models.Playlist в PlaylistSearchResult (если он еще нужен)
                // Или меняем сигнатуру метода на возврат List<Playlist>
                // Для совместимости создадим PlaylistSearchResult вручную
                var thumbs = new List<Thumbnail>();
                if (!string.IsNullOrEmpty(pl.ThumbnailUrl)) thumbs.Add(new Thumbnail(pl.ThumbnailUrl, new Resolution(0, 0)));

                var auth = pl.Author != null ? new Author(new ChannelId(channel.Id.Value), pl.Author) : null;

                results.Add(new PlaylistSearchResult(
                   new PlaylistId(pl.YoutubeId ?? ""),
                   pl.Name,
                   auth,
                   thumbs
               ));
            }

            NotifyStatus($"[YouTube] Found {results.Count} playlists.");
            return (channel.Title, results);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error parsing channel playlists: {ex.Message}");
            return (channel.Title, []);
        }
    }

    public async Task<List<Models.Playlist>> GetUserPlaylistsByAuthAsync()
    {
        var userDataService = Program.Services.GetRequiredService<YoutubeUserDataService>();
        return await userDataService.GetMyPlaylistsAsync();
    }

    public async Task<Playlist?> ImportPlaylistAsync(string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            // PlaylistClient.GetAsync возвращает Playlist
            var plId = new PlaylistId(playlistId);
            var playlist = await _youtube.Playlists.GetAsync(plId, ct);

            // Настраиваем режим
            playlist.SyncMode = isAccountSync ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.CloudPublic;

            // Загружаем треки
            var tracks = await _youtube.Playlists.GetVideosAsync(plId, ct).CollectAsync();

            foreach (var track in tracks)
            {
                _libraryService?.AddOrUpdateTrack(track);
                playlist.TrackIds.Add(track.Id);
            }
            return playlist;
        }
        catch (Exception ex)
        {
            NotifyError($"Error importing playlist {playlistId}: {ex.Message}");
            return null;
        }
    }

    public async Task<(string Name, string AvatarUrl)?> GetChannelInfoAsync(string url, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(url, ct);
        if (channel == null) return null;
        return (channel.Title, channel.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault()?.Url ?? "");
    }

    private async Task<Channel?> GetChannelFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (url.Contains("/channel/"))
            {
                var id = url.Split("/channel/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetAsync(new ChannelId(id), ct);
            }
            if (url.Contains("/@"))
            {
                var handle = url.Split("/@")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByHandleAsync(new ChannelHandle(handle), ct);
            }
            if (url.Contains("/c/"))
            {
                var slug = url.Split("/c/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetBySlugAsync(new ChannelSlug(slug), ct);
            }
            if (url.Contains("/user/"))
            {
                var user = url.Split("/user/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByUserAsync(new UserName(user), ct);
            }

            NotifyError("[YouTube] Could not recognize channel URL format.");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error getting channel info: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url)) return [];
        try
        {
            var videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) return [];
            var mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";

            // GetPlaylistAsync возвращает (Name, List<TrackInfo>)
            var result = await GetPlaylistAsync(mixUrl);
            if (result == null) return [];

            var tracks = result.Value.Tracks.Take(count).ToList();
            foreach (var t in tracks) t.RadioSeedId = sourceTrack.Id;
            return tracks;
        }
        catch { return []; }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            var url = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(url);
            return result?.Tracks.Take(count).ToList() ?? await SearchAsync("top music 2024", count);
        }
        catch
        {
            return await SearchAsync("top music 2024", count);
        }
    }

    public async Task<string?> DownloadTrackAsync(
        TrackInfo track,
        IProgress<float>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url)) return null;
        try
        {
            var videoId = ExtractVideoId(track.Url);
            if (string.IsNullOrEmpty(videoId)) return null;

            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(_downloadFolder, fileName);

            var prog = progress != null ? new Progress<double>(p => progress.Report((float)p)) : null;

            await _youtube.Videos.Streams.DownloadAsync(stream, filePath, progress: prog, cancellationToken: ct);
            NotifyStatus($"[YouTube] Downloaded: {fileName}");
            return filePath;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Download error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Helpers

    private static TrackInfo ConvertPlaylistSearchResultToTrackInfo(PlaylistSearchResult playlist)
    {
        var thumb = playlist.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault();
        return new TrackInfo
        {
            // Используем префикс yt_pl_ чтобы отличить плейлист от трека, если UI поддерживает это
            // Или можно использовать просто yt_, но тогда при попытке воспроизвести как трек будет ошибка
            Id = $"yt_pl_{playlist.Id.Value}",
            Title = playlist.Title,
            Author = playlist.Author?.ChannelTitle ?? "Unknown",
            Url = playlist.Url,
            Duration = TimeSpan.Zero, // У плейлистов нет длительности в поиске
            ThumbnailUrl = thumb?.Url ?? "",
            IsMusic = false // Плейлист сам по себе не музыкальный файл
        };
    }

    private static TrackInfo ConvertToTrackInfo(Video video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private static TrackInfo ConvertSearchResultToTrackInfo(VideoSearchResult video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? "",
            IsOfficialArtist = video.IsOfficialArtist,
            IsMusic = video.IsMusic
        };
    }

    private static TrackInfo ConvertPlaylistVideoToTrackInfo(PlaylistVideo video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string([.. name.Where(c => !invalid.Contains(c))]);
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    private void NotifyStatus(string message)
    {
        Log.Info(message);
        OnStatusChanged?.Invoke(message);
    }

    private void NotifyError(string message)
    {
        Log.Error(message);
        OnError?.Invoke(message);
    }

    public void Dispose()
    {
        _cookieSyncTimer?.Dispose();

        // Финальное сохранение перед выходом
        if (_activeContainer != null)
        {
            _cookieAuth.SyncCookiesFromContainer(_activeContainer);
        }

        _cookieAuth.OnAuthStateChanged -= ReloadClient;
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubeVideoRegex();

    [GeneratedRegex(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubePlaylistRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex _ValidYoutubeId();

    #endregion
}

/// <summary>
/// Информация о доступном аудио потоке
/// </summary>
public class StreamOption
{
    /// <summary>Контейнер (webm/mp4)</summary>
    public string Container { get; set; } = "";

    /// <summary>Битрейт в kbps</summary>
    public double Bitrate { get; set; }

    /// <summary>Кодек (Opus/AAC)</summary>
    public string Codec { get; set; } = "";

    /// <summary>Размер в мегабайтах</summary>
    public double SizeMb { get; set; }

    /// <summary>Отображаемое имя для UI</summary>
    public string DisplayName => $"{Codec} {Bitrate:F0}kbps ({Container})";
}

public class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}
```

```
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Bridge;

internal partial class VideoWatchPage(string rawContent)
{
    public bool IsAvailable => !rawContent.Contains("og:url") || rawContent.Contains("video_id");

    public DateTimeOffset? UploadDate =>
        MyRegex().Match(rawContent)
            .Groups[1].Value.NullIfWhiteSpace()
            ?.Pipe(s => DateTimeOffset.TryParse(s, out var d) ? d : (DateTimeOffset?)null);

    // Лайки и дизлайки убираем — YouTube API их часто не отдает в HTML без JS, 
    // а для плеера это лишний мусор в памяти.

    // Парсинг лайков без AngleSharp
    // YouTube часто меняет формат, ищем "likeCount":"12345" или в тултипе "12,345 likes"
    public long? LikeCount
    {
        get
        {
            // Вариант 1: JSON внутри initialData
            var matchJson = LikeRegex1().Match(rawContent);
            if (matchJson.Success && long.TryParse(matchJson.Groups[1].Value, out var l1))
                return l1;

            // Вариант 2: Старый формат текста
            var matchText = LikeRegex2().Match(rawContent);
            if (matchText.Success)
            {
                var clean = matchText.Groups[1].Value.Replace(",", "").Replace(".", "");
                if (long.TryParse(clean, out var l2)) return l2;
            }

            return null; // Не нашли (скрыты или новый лейаут)
        }
    }

    // То же самое для дизлайков (обычно 0 или скрыты)
    public long? DislikeCount => 0;

    public PlayerResponse? PlayerResponse
    {
        get
        {
            // 1. Пробуем найти ytInitialPlayerResponse
            var json = Regex.Match(rawContent, @"var\s+ytInitialPlayerResponse\s*=\s*(\{.*?\});", RegexOptions.Singleline)
                .Groups[1].Value;

            if (!string.IsNullOrWhiteSpace(json))
            {
                return PlayerResponse.Parse(json);
            }

            // 2. Пробуем найти ytplayer.config (старый формат, иногда встречается)
            var configJson = Regex.Match(rawContent, @"ytplayer\.config\s*=\s*(\{.*?\});", RegexOptions.Singleline)
                .Groups[1].Value;

            if (!string.IsNullOrWhiteSpace(configJson))
            {
                var config = Json.TryParse(configJson);
                var argsResponse = config?.GetPropertyOrNull("args")?.GetPropertyOrNull("player_response")?.GetStringOrNull();
                if (!string.IsNullOrWhiteSpace(argsResponse))
                {
                    return PlayerResponse.Parse(argsResponse);
                }
            }

            return null;
        }
    }

    public static VideoWatchPage? TryParse(string raw)
    {
        // Простая проверка на наличие признаков страницы видео
        if (!raw.Contains("ytInitialPlayerResponse") && !raw.Contains("ytplayer.config"))
            return null;

        return new VideoWatchPage(raw);
    }

    [GeneratedRegex(@"itemprop=""datePublished"" content=""(.*?)(?:"")")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"""likeCount""\s*:\s*""(\d+)""")]
    private static partial Regex LikeRegex1();
    [GeneratedRegex(@"([\d,\.]+)\s+likes")]
    private static partial Regex LikeRegex2();
}
```

```
using System.Net;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Represents a syntactically valid YouTube video ID.
/// </summary>
public readonly partial struct VideoId(string value)
{
    /// <summary>
    /// Raw ID value.
    /// </summary>
    public string Value { get; } = value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

public partial struct VideoId
{
    private static bool IsValid(string videoId) =>
        videoId.Length == 11 && videoId.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-');

    private static string? TryNormalize(string? videoIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrUrl))
            return null;

        // Check if already passed an ID
        // yIVRs6YSbOM
        if (IsValid(videoIdOrUrl))
            return videoIdOrUrl;

        // Try to extract the ID from the URL
        // https://www.youtube.com/watch?v=yIVRs6YSbOM
        {
            var id = MyRegex().Match(videoIdOrUrl)
                .Groups[1]
                .Value.Pipe(WebUtility.UrlDecode);

            if (!string.IsNullOrWhiteSpace(id) && IsValid(id))
                return id;
        }

        // Try to extract the ID from the URL (partially shortened)
        // https://youtu.be/watch?v=Fcds0_MrgNU
        {
            var id = MyRegex1().Match(videoIdOrUrl)
                .Groups[1]
                .Value.Pipe(WebUtility.UrlDecode);

            if (!string.IsNullOrWhiteSpace(id) && IsValid(id))
                return id;
        }

        // Try to extract the ID from the URL (shortened)
        // https://youtu.be/yIVRs6YSbOM
        {
            var id = MyRegex2().Match(videoIdOrUrl)
                .Groups[1]
                .Value.Pipe(WebUtility.UrlDecode);

            if (!string.IsNullOrWhiteSpace(id) && IsValid(id))
                return id;
        }

        // Try to extract the ID from the URL (embedded)
        // https://www.youtube.com/embed/yIVRs6YSbOM
        {
            var id = MyRegex3().Match(videoIdOrUrl)
                .Groups[1]
                .Value.Pipe(WebUtility.UrlDecode);

            if (!string.IsNullOrWhiteSpace(id) && IsValid(id))
                return id;
        }

        // Try to extract the ID from the URL (shorts clip)
        // https://www.youtube.com/shorts/sKL1vjP0tIo
        {
            var id = Regex
                .Match(videoIdOrUrl, @"youtube\..+?/shorts/(.*?)(?:\?|&|/|$)")
                .Groups[1]
                .Value.Pipe(WebUtility.UrlDecode);

            if (!string.IsNullOrWhiteSpace(id) && IsValid(id))
                return id;
        }

        // Try to extract the ID from the URL (livestream)
        // https://www.youtube.com/live/jfKfPfyJRdk
        {
            var id = Regex
                .Match(videoIdOrUrl, @"youtube\..+?/live/(.*?)(?:\?|&|/|$)")
                .Groups[1]
                .Value.Pipe(WebUtility.UrlDecode);

            if (!string.IsNullOrWhiteSpace(id) && IsValid(id))
                return id;
        }

        // Invalid input
        return null;
    }

    /// <summary>
    /// Attempts to parse the specified string as a video ID or URL.
    /// Returns null in case of failure.
    /// </summary>
    public static VideoId? TryParse(string? videoIdOrUrl) =>
        TryNormalize(videoIdOrUrl)?.Pipe(static id => new VideoId(id));

    /// <summary>
    /// Parses the specified string as a YouTube video ID or URL.
    /// Throws an exception in case of failure.
    /// </summary>
    public static VideoId Parse(string videoIdOrUrl) =>
        TryParse(videoIdOrUrl)
        ?? throw new ArgumentException($"Invalid YouTube video ID or URL '{videoIdOrUrl}'.");

    /// <summary>
    /// Converts string to ID.
    /// </summary>
    public static implicit operator VideoId(string videoIdOrUrl) => Parse(videoIdOrUrl);

    /// <summary>
    /// Converts ID to string.
    /// </summary>
    public static implicit operator string(VideoId videoId) => videoId.ToString();

    [GeneratedRegex(@"youtube\..+?/watch.*?v=(.*?)(?:&|/|$)")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"youtu\.be/watch.*?v=(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"youtu\.be/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@"youtube\..+?/embed/(.*?)(?:\?|&|/|$)")]
    private static partial Regex MyRegex3();
}

public partial struct VideoId : IEquatable<VideoId>
{
    /// <inheritdoc />
    public bool Equals(VideoId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is VideoId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator ==(VideoId left, VideoId right) => left.Equals(right);

    /// <summary>
    /// Equality check.
    /// </summary>
    public static bool operator !=(VideoId left, VideoId right) => !(left == right);
}

```

```
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace LMP.Features.Settings.Controls;

/// <summary>
/// Popup-контент для выбора цвета с RGB-слайдерами и HEX-вводом.
/// </summary>
public partial class ColorPickerPopup : UserControl, INotifyPropertyChanged
{
    #region INotifyPropertyChanged

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion

    #region Events

    public event EventHandler<Color>? ColorConfirmed;
    public event EventHandler? ColorCancelled;

    #endregion

    #region Styled Properties

    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ColorPickerPopup, Color>(nameof(SelectedColor), Colors.White);

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    #endregion

    #region Bindable Properties

    private byte _redValue;
    public byte RedValue
    {
        get => _redValue;
        set
        {
            if (_redValue == value) return;
            _redValue = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromRgb();
        }
    }

    private byte _greenValue;
    public byte GreenValue
    {
        get => _greenValue;
        set
        {
            if (_greenValue == value) return;
            _greenValue = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromRgb();
        }
    }

    private byte _blueValue;
    public byte BlueValue
    {
        get => _blueValue;
        set
        {
            if (_blueValue == value) return;
            _blueValue = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromRgb();
        }
    }

    private string _hexInput = "#FFFFFF";
    public string HexInput
    {
        get => _hexInput;
        set
        {
            if (_hexInput == value) return;
            _hexInput = value;
            OnPropertyChanged();
            if (!_isUpdating) UpdateColorFromHex();
        }
    }

    public List<ColorPreset> PresetColors { get; } = GeneratePresets();

    #endregion

    private bool _isUpdating;

    public ColorPickerPopup()
    {
        InitializeComponent();

        // Синхронизация: SelectedColor -> RGB & HEX
        SelectedColorProperty.Changed.AddClassHandler<ColorPickerPopup>(static (sender, _) =>
        {
            sender.SyncFromSelectedColor();
        });
    }

    private void SyncFromSelectedColor()
    {
        if (_isUpdating) return;
        _isUpdating = true;

        var color = SelectedColor;
        _redValue = color.R;
        _greenValue = color.G;
        _blueValue = color.B;
        _hexInput = color.ToString();

        OnPropertyChanged(nameof(RedValue));
        OnPropertyChanged(nameof(GreenValue));
        OnPropertyChanged(nameof(BlueValue));
        OnPropertyChanged(nameof(HexInput));

        _isUpdating = false;
    }

    private void UpdateColorFromRgb()
    {
        _isUpdating = true;

        SelectedColor = Color.FromRgb(_redValue, _greenValue, _blueValue);
        _hexInput = SelectedColor.ToString();
        OnPropertyChanged(nameof(HexInput));

        _isUpdating = false;
    }

    private void UpdateColorFromHex()
    {
        if (string.IsNullOrWhiteSpace(_hexInput)) return;

        if (TryParseHex(_hexInput, out var color))
        {
            _isUpdating = true;

            SelectedColor = color;
            _redValue = color.R;
            _greenValue = color.G;
            _blueValue = color.B;

            OnPropertyChanged(nameof(RedValue));
            OnPropertyChanged(nameof(GreenValue));
            OnPropertyChanged(nameof(BlueValue));

            _isUpdating = false;
        }
    }

    #region Event Handlers

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color color })
        {
            SelectedColor = color;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        ColorConfirmed?.Invoke(this, SelectedColor);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ColorCancelled?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Helpers

    private static bool TryParseHex(string hex, out Color color)
    {
        color = Colors.White;
        try
        {
            if (!hex.StartsWith('#'))
                hex = "#" + hex;

            if (!Regex.IsMatch(hex, @"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$"))
                return false;

            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<ColorPreset> GeneratePresets()
    {
        return
        [
            // Purples
            new("Paralax Purple", "#8A2BE2"),
            new("Violet", "#9B59B6"),
            new("Deep Purple", "#673AB7"),
            new("Lavender", "#B39DDB"),

            // Blues
            new("Electric Blue", "#00B4D8"),
            new("Sky Blue", "#5B9BD5"),
            new("Navy", "#1E3A5F"),
            new("Cyan", "#00BCD4"),

            // Greens
            new("Spotify Green", "#1DB954"),
            new("Emerald", "#2ECC71"),
            new("Teal", "#009688"),
            new("Mint", "#4ECB71"),

            // Warm
            new("Sunset Orange", "#FF6B35"),
            new("Coral", "#FF6B6B"),
            new("Gold", "#FFB86C"),
            new("Pink", "#FF69B4"),

            // Neutrals
            new("White", "#FFFFFF"),
            new("Light Gray", "#B3B3B3"),
            new("Dark Gray", "#404040"),
            new("Pure Black", "#000000"),

            // Backgrounds
            new("Deep Dark", "#0F0B15"),
            new("Spotify Black", "#121212"),
            new("Ocean Dark", "#001219"),
        ];
    }

    #endregion
}

/// <summary>
/// Пресет цвета для быстрого выбора
/// </summary>
public record ColorPreset(string Name, string Hex)
{
    public Color Color => Color.Parse(Hex);
    public SolidColorBrush Brush => new(Color);
}
```

```
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Settings;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly StreamCacheManager _streamCache;
    private readonly ThemeManagerService _themeManager;
    private readonly CookieAuthService _auth; // Changed
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    private bool _isDisposed;
    private bool _isLoadingTheme;

    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    public string AccountName => IsAuthenticated
        ? "YouTube Music User" // Cookie Auth не отдает имя сразу, можно вытащить позже
        : _library.FakeAccountName ?? SL["Auth_NotSignedIn"];

    public string? AccountAvatarUrl => IsAuthenticated
        ? null
        : _library.FakeAccountAvatarUrl;

    public string AccountSubtitle => IsAuthenticated
        ? "Authorized via Cookies"
        : IsFakeAccount ? SL["Account_LimitedAccess"] : SL["Auth_Guest"];

    public List<InternetProfile> InternetProfileOptions { get; } = [.. Enum.GetValues<InternetProfile>()];
    [Reactive] public InternetProfile SelectedInternetProfile { get; set; }
    [Reactive] public bool ProxyEnabled { get; set; }
    [Reactive] public string ProxyHost { get; set; } = "";
    [Reactive] public int ProxyPort { get; set; } = 8080;
    [Reactive] public bool ProxyAuth { get; set; }
    [Reactive] public string ProxyUser { get; set; } = "";
    [Reactive] public string ProxyPass { get; set; } = "";
    [Reactive] public bool NetworkRestartRequired { get; set; }

    [Reactive] public string DownloadPath { get; set; } = string.Empty;
    [Reactive] public int ImageCacheLimitMb { get; set; }
    [Reactive] public int AudioCacheLimitMb { get; set; }
    [Reactive] public string ImageCacheStats { get; private set; } = "...";
    [Reactive] public string AudioCacheStats { get; private set; } = "...";
    [Reactive] public double ImageCacheUsagePercent { get; private set; }
    [Reactive] public double AudioCacheUsagePercent { get; private set; }

    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];
    [Reactive] public ThemeSettings? SelectedPreset { get; set; }
    [Reactive] public Color AccentColor { get; set; }
    [Reactive] public Color BgPrimaryColor { get; set; }
    [Reactive] public Color BgSecondaryColor { get; set; }
    [Reactive] public Color BgElevatedColor { get; set; }
    [Reactive] public Color TextPrimaryColor { get; set; }
    [Reactive] public Color TextSecondaryColor { get; set; }
    [Reactive] public bool HasUnsavedThemeChanges { get; set; }

    public List<AudioQualityPreference> QualityOptions { get; } = [.. Enum.GetValues<AudioQualityPreference>()];
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }
    [Reactive] public AudioQualityPreference QualityPreference { get; set; }
    [Reactive] public bool RememberTrackFormat { get; set; }

    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }
    [Reactive] public int SearchBatchSize { get; set; }
    [Reactive] public bool EnableSearchCache { get; set; }
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SetFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearImageCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudioCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetThemeCommand { get; }

    public SettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        StreamCacheManager streamCache,
        ThemeManagerService themeManager,
        CookieAuthService auth, // Changed
        IDialogService dialog,
        AudioEngine audio,
        YoutubeProvider youtube)
    {
        _library = library;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _streamCache = streamCache;
        _themeManager = themeManager;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _youtube = youtube;

        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);
        SetFakeAccountCommand = ReactiveCommand.CreateFromTask(SetFakeAccountAsync);
        ClearFakeAccountCommand = ReactiveCommand.Create(ClearFakeAccount);
        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(ResetLibraryAsync);
        ClearImageCacheCommand = ReactiveCommand.CreateFromTask(ClearImageCacheAsync);
        ClearAudioCacheCommand = ReactiveCommand.CreateFromTask(ClearAudioCacheAsync);
        ApplyThemeCommand = ReactiveCommand.Create(ApplyTheme);
        ResetThemeCommand = ReactiveCommand.Create(ResetTheme);

        LoadAllSettings();
        UpdateCacheStats();
        SetupSubscriptions();
    }

    private void SetupSubscriptions()
    {
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();
            });

        this.WhenAnyValue(
                x => x.SelectedInternetProfile, x => x.ProxyEnabled,
                x => x.ProxyHost, x => x.ProxyPort,
                x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1)
            .Subscribe(_ =>
            {
                NetworkRestartRequired = true;
                SaveNetworkSettings();
            });

        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(_ => SaveStorageSettings());

        this.WhenAnyValue(
                x => x.AccentColor, x => x.BgPrimaryColor, x => x.BgSecondaryColor,
                x => x.BgElevatedColor, x => x.TextPrimaryColor, x => x.TextSecondaryColor)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (!_isLoadingTheme)
                    HasUnsavedThemeChanges = true;
            });

        this.WhenAnyValue(x => x.SelectedPreset)
            .Skip(1).WhereNotNull()
            .Subscribe(ApplyPresetToColorPickers);

        this.WhenAnyValue(x => x.MaxVolumeLimit).Skip(1).Subscribe(v =>
        {
            _library.Data.MaxVolumeLimit = v;
            _library.Save();
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.TargetGainDb).Skip(1).Subscribe(v =>
        {
            _library.Data.TargetGainDb = v;
            _library.Save();
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.QualityPreference).Skip(1).Subscribe(v =>
        {
            _library.Data.QualityPreference = v;
            _library.Save();
            _youtube.ClearCache();
        });

        this.WhenAnyValue(x => x.DiscordRpcEnabled).Skip(1).Subscribe(v =>
        {
            _library.Data.DiscordRpcEnabled = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.AutoPlayOnPaste).Skip(1).Subscribe(v =>
        {
            _library.Data.AutoPlayOnUrlPaste = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.EnableSmoothLoading).Skip(1).Subscribe(v =>
        {
            _library.Data.EnableSmoothLoading = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.RememberTrackFormat).Skip(1).Subscribe(v =>
        {
            _library.Data.RememberTrackFormat = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.SearchBatchSize).Skip(1).Subscribe(v =>
        {
            _library.Data.SearchBatchSize = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.EnableSearchCache).Skip(1).Subscribe(v =>
        {
            _library.Data.EnableSearchCache = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes).Skip(1).Subscribe(v =>
        {
            _library.Data.SearchCacheTtlMinutes = v;
            _library.Save();
            _ = _searchCache.CleanupExpiredAsync();
        });
    }

    private void LoadAllSettings()
    {
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        SearchBatchSize = _library.Data.SearchBatchSize;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;
        MaxVolumeLimit = _library.Data.MaxVolumeLimit;
        TargetGainDb = _library.Data.TargetGainDb;
        QualityPreference = _library.Data.QualityPreference;
        RememberTrackFormat = _library.Data.RememberTrackFormat;
        EnableSearchCache = _library.Data.EnableSearchCache;
        SearchCacheTtlMinutes = _library.Data.SearchCacheTtlMinutes;
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == _library.Data.LanguageCode) ?? Languages[0];

        FakeChannelInput = _library.FakeAccountUrl ?? "";
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();

        SelectedInternetProfile = _library.Data.InternetProfile;
        ProxyEnabled = _library.Data.Proxy.Enabled;
        ProxyHost = _library.Data.Proxy.Host;
        ProxyPort = _library.Data.Proxy.Port;
        ProxyAuth = _library.Data.Proxy.UseAuth;
        ProxyUser = _library.Data.Proxy.Username;
        ProxyPass = _library.Data.Proxy.Password;

        ImageCacheLimitMb = _library.Data.Storage.ImageCacheLimitMb;
        AudioCacheLimitMb = _library.Data.Storage.AudioCacheLimitMb;

        LoadThemeColors();
    }

    private void LoadThemeColors()
    {
        var theme = _themeManager.GetCurrentTheme();
        ApplyThemeToColorPickers(theme);
        HasUnsavedThemeChanges = false;
    }

    private void ApplyPresetToColorPickers(ThemeSettings preset)
    {
        ApplyThemeToColorPickers(preset);
        HasUnsavedThemeChanges = true;
    }

    private void ApplyThemeToColorPickers(ThemeSettings theme)
    {
        _isLoadingTheme = true;
        try
        {
            AccentColor = ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ParseColorSafe(theme.TextSecondary);
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void ApplyTheme()
    {
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? SL["Theme_Custom"],
            AccentColor = AccentColor.ToString(),
            AccentHover = LightenColor(AccentColor, 0.15).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
            BgOverlay = $"#CC{GetRgbHex(BgPrimaryColor)}",
            TextPrimary = TextPrimaryColor.ToString(),
            TextSecondary = TextSecondaryColor.ToString(),
            TextMuted = DarkenColor(TextSecondaryColor, 0.3).ToString(),
            TextDark = BgPrimaryColor.ToString()
        };

        _themeManager.SaveTheme(theme);
        _themeManager.ApplyTheme(theme);
        HasUnsavedThemeChanges = false;
    }

    private void ResetTheme()
    {
        _themeManager.ResetToDefault();
        LoadThemeColors();
        SelectedPreset = ThemePresets.FirstOrDefault();
    }

    private static Color ParseColorSafe(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Magenta; }
    }

    private static Color LightenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * factor),
            (byte)Math.Min(255, c.G + (255 - c.G) * factor),
            (byte)Math.Min(255, c.B + (255 - c.B) * factor));
    }

    private static Color DarkenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));
    }

    private void SaveNetworkSettings()
    {
        _library.Data.InternetProfile = SelectedInternetProfile;
        _library.Data.Proxy.Enabled = ProxyEnabled;
        _library.Data.Proxy.Host = ProxyHost;
        _library.Data.Proxy.Port = ProxyPort;
        _library.Data.Proxy.UseAuth = ProxyAuth;
        _library.Data.Proxy.Username = ProxyUser;
        _library.Data.Proxy.Password = ProxyPass;
        _library.Save();
    }

    private void SaveStorageSettings()
    {
        _library.Data.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
        _library.Data.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        _library.Save();
        UpdateCacheStats();
    }

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        await _streamCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private void UpdateCacheStats()
    {
        var (imgCount, imgSize) = _imageCache.GetStats();
        var audioStats = _streamCache.GetStats();

        ImageCacheStats = $"{imgSize} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]})";
        AudioCacheStats = $"{audioStats.SizeMb} MB / {AudioCacheLimitMb} MB ({audioStats.FileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0
            ? Math.Clamp((double)imgSize / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0
            ? Math.Clamp((double)audioStats.SizeMb / AudioCacheLimitMb, 0, 1) : 0;
    }

    private async Task SetFakeAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(FakeChannelInput)) return;
        IsLoadingFakeAccount = true;
        try
        {
            var info = await _youtube.GetChannelInfoAsync(FakeChannelInput);
            if (info != null)
            {
                _library.SetFakeAccount(FakeChannelInput, info.Value.Name, info.Value.AvatarUrl);
                RaiseAccountProperties();
                await _dialog.ShowInfoAsync(SL["Dialog_Success"],
                    string.Format(SL["Dialog_Merge_Success"], info.Value.Name));
            }
            else
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Dialog_Merge_Error"]);
            }
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], ex.Message);
        }
        finally
        {
            IsLoadingFakeAccount = false;
        }
    }

    private void ClearFakeAccount()
    {
        _library.ClearFakeAccount();
        FakeChannelInput = "";
        RaiseAccountProperties();
    }

    // --- REPLACED LOGIN LOGIC ---
    private async Task LoginAsync()
    {
        // Используем новый метод IDialogService.ShowInputAsync
        // Примечание: для перевода строк можно использовать Environment.NewLine в prompt
        var cookies = await _dialog.ShowInputAsync("Login",
            "1. Open music.youtube.com in browser\n2. Press F12 -> Network -> Click any request -> Copy 'Cookie' header\n3. Paste here:");

        if (!string.IsNullOrWhiteSpace(cookies))
        {
            _auth.SaveCookies(cookies.Trim());
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();
            await _dialog.ShowInfoAsync("Success", "Cookies saved. Restart might be required for all features.");
        }
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Auth_Logout"], SL["Dialog_LogoutMessage"]))
            return;
        _auth.Logout();
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();
    }

    private void RaiseAccountProperties()
    {
        this.RaisePropertyChanged(nameof(AccountName));
        this.RaisePropertyChanged(nameof(AccountAvatarUrl));
        this.RaisePropertyChanged(nameof(AccountSubtitle));
        this.RaisePropertyChanged(nameof(HasAccount));
        this.RaisePropertyChanged(nameof(IsFakeAccount));
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await _dialog.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
        _library.Save();
    }

    private async Task ClearHistoryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Dialog_ClearHistoryMessage"]))
            return;
        _library.ClearHistory();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"]))
            return;
        _library.Reset();
        LoadAllSettings();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
```

```
using System.Text.Json;
using LMP.Core.Youtube.Utils;

namespace LMP;

public static class Globals
{
    public const string AppId = "LMP";
    public const string AppName = "Lite Music Player";

    public static class Folder
    {
        public readonly static string Data = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + Path.DirectorySeparatorChar + AppId;
        public readonly static string Downloads = Path.Combine(Data, "Downloads");
        public readonly static string ImageCache = Path.Combine(Data, "ImageCache");
        public readonly static string StreamCache = Path.Combine(Data, "StreamCache");
        public readonly static string SearchCache = Path.Combine(Data, "SearchCache");

        public static void Create()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(ImageCache);
            Directory.CreateDirectory(SearchCache);
        }
    }

    public static class File
    {
        public readonly static string Cookie = Path.Combine(Folder.Data, "auth_cookies.txt");
        public readonly static string Library = Path.Combine(Folder.Data, "library.json");
        public readonly static string Theme = Path.Combine(Folder.Data, "theme.json");
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Beautiful = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static readonly JsonSerializerOptions Compact = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}

```

```

using LMP.Core.Services;
using LMP.Features.Home;
using LMP.Features.Library;
using LMP.Features.Player;
using LMP.Features.Playlist;
using LMP.Features.Search;
using LMP.Features.Settings;
using LMP.Features.Shell;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Diagnostics;

namespace LMP;

class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        try
        {
            Console.WriteLine("Logger initializing...");
            Log.Initialize();

            Log.Info("LiteMusicPlayer starting...!");

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal($"Global crash: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI();

    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Info("Configuring services...");

        // --- Core Services ---
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddSingleton<YoutubeUserDataService>();
        services.AddSingleton<MusicLibraryManager>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        // --- Caching ---
        services.AddSingleton<StreamCacheManager>();
        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();
        services.AddSingleton<MemoryMonitor>();

        // --- Audio & Downloads ---
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // --- ViewModels ---
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<MergeConflictViewModel>();
        services.AddTransient<SyncSelectionViewModel>();
        services.AddSingleton<TrackViewModelFactory>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Log.Info("Services registered successfully.");
    }
}
```

А вот как новый код выглядит для тех файлов:
Core/Services/CookieAuthService.cs:
```cs
using System.Text.RegularExpressions;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    private string _rawCookies = "";

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_rawCookies);

    public event Action? OnAuthStateChanged;
    
    public CookieAuthService()
    {
        Load();
    }

    private void Load()
    {
        if (File.Exists(G.File.Cookie))
        {
            _rawCookies = File.ReadAllText(G.File.Cookie);
        }
    }

    /// <summary>
    /// Метод для получения сырой строки (используется провайдером)
    /// </summary>
    /// <returns></returns>
    public string GetRawCookies()
    {
        return _rawCookies;
    }

    /// <summary>
    /// Сохранение, вызванное ТОЛЬКО пользователем (логин/вставка)
    /// </summary>
    /// <param name="cookies"></param>
    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        // Чистим ввод от заголовка "Cookie: " если он есть
        var clean = FindCookieTextRegex().Replace(cookies, "");
        clean = clean.Replace("\r", "").Replace("\n", "");
        clean = clean.Trim().Trim('"');

        // Валидация: проверяем наличие критических полей перед сохранением
        if (!clean.Contains("SAPISID"))
        {
            Log.Warn("[Auth] Attempt to save cookies without SAPISID. Ignoring.");
            return;
        }

        _rawCookies = clean;
        // Перезаписываем файл
        File.WriteAllText(G.File.Cookie, _rawCookies);
        
        Log.Info($"[Auth] Cookies saved manually. Length: {_rawCookies.Length}");
        OnAuthStateChanged?.Invoke();
    }

    public void Logout()
    {
        _rawCookies = "";
        if (File.Exists(G.File.Cookie)) File.Delete(G.File.Cookie);
        OnAuthStateChanged?.Invoke();
    }

    [GeneratedRegex(@"^Cookie:\s*", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex FindCookieTextRegex();
}
```

Core/Services/DownloadService.cs:
```cs
using LMP.Core.Models;

namespace LMP.Core.Services;

public class DownloadService
{
    private readonly YoutubeProvider _youtube;
    private readonly LibraryService _library;

    private readonly Dictionary<string, DownloadTask> _activeTasks = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(3);

    public event Action<string, float>? OnProgress;
    public event Action<string, bool, string?>? OnCompleted;

    public DownloadService(YoutubeProvider youtube, LibraryService library)
    {
        _youtube = youtube;
        _library = library;
    }

    public bool IsDownloading(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.ContainsKey(trackId);
        }
    }

    public float GetProgress(string trackId)
    {
        lock (_lock)
        {
            return _activeTasks.TryGetValue(trackId, out var task) ? task.Progress : 0f;
        }
    }

    public int ActiveDownloadsCount
    {
        get
        {
            lock (_lock)
            {
                return _activeTasks.Count;
            }
        }
    }

    public void StartDownload(TrackInfo track)
    {
        lock (_lock)
        {
            if (_activeTasks.ContainsKey(track.Id) || track.IsDownloaded)
                return;

            var cts = new CancellationTokenSource();
            _activeTasks[track.Id] = new DownloadTask { CancellationSource = cts };
        }

        Task.Run(async () =>
        {
            await _downloadSemaphore.WaitAsync();

            try
            {
                var progress = new Progress<float>(p =>
                {
                    lock (_lock)
                    {
                        if (_activeTasks.TryGetValue(track.Id, out var task))
                            task.Progress = p;
                    }
                    OnProgress?.Invoke(track.Id, p);
                });

                CancellationToken ct;
                lock (_lock)
                {
                    if (!_activeTasks.TryGetValue(track.Id, out var task))
                        return;
                    ct = task.CancellationSource.Token;
                }

                string? path = await _youtube.DownloadTrackAsync(track, progress, ct);

                if (!string.IsNullOrEmpty(path))
                {
                    track.IsDownloaded = true;
                    track.LocalPath = path;
                    _library.AddOrUpdateTrack(track);
                    OnCompleted?.Invoke(track.Id, true, path);
                }
                else
                {
                    OnCompleted?.Invoke(track.Id, false, null);
                }
            }
            catch (OperationCanceledException)
            {
                OnCompleted?.Invoke(track.Id, false, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Download error: {ex.Message}\n{ex.StackTrace}");
                OnCompleted?.Invoke(track.Id, false, null);
            }
            finally
            {
                lock (_lock)
                {
                    _activeTasks.Remove(track.Id);
                }
                _downloadSemaphore.Release();
            }
        });
    }

    public void CancelDownload(string trackId)
    {
        lock (_lock)
        {
            if (_activeTasks.TryGetValue(trackId, out var task))
            {
                task.CancellationSource.Cancel();
            }
        }
    }

    public void CancelAllDownloads()
    {
        lock (_lock)
        {
            foreach (var task in _activeTasks.Values)
            {
                task.CancellationSource.Cancel();
            }
        }
    }

    private class DownloadTask
    {
        public float Progress { get; set; }
        public CancellationTokenSource CancellationSource { get; set; } = new();
    }
}


```

Core/Services/ImageCacheService.cs:
```cs
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace LMP.Core.Services;

/// <summary>
/// Управляет загрузкой и кэшированием изображений.
/// Реализует двухуровневый кэш: Память (LRU) + Диск.
/// С поддержкой динамических лимитов из настроек.
/// </summary>
public sealed class ImageCacheService : IDisposable
{
    private class CachedImage
    {
        public Bitmap? Bitmap { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private const int MaxMemoryCacheItems = 60;
    
    private readonly HttpClient _http;
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _downloadSemaphore = new(5);

    private readonly ConcurrentDictionary<string, CachedImage> _memoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Lock _lruLock = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private long _currentDiskCacheBytes = 0;
    private bool _isDisposed;

    public ImageCacheService(LibraryService library)
    {
        _library = library;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        _ = Task.Run(CalculateDiskCacheSizeAsync);
    }

    public async Task<Bitmap?> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (_isDisposed || string.IsNullOrEmpty(url)) return null;

        var key = GetCacheKey(url);

        if (_memoryCache.TryGetValue(key, out var cached))
        {
            TouchLru(key);
            return cached.Bitmap;
        }

        return await LoadFromDiskOrNetwork(url, key, ct);
    }

    public async Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        if (_isDisposed) return;
        var tasks = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .Where(u => !_memoryCache.ContainsKey(GetCacheKey(u)))
            .Take(15)
            .Select(u => EnsureCachedDiskOnlyAsync(u, ct));
        
        try { await Task.WhenAll(tasks); } catch { }
    }

    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }
        GC.Collect(2, GCCollectionMode.Optimized);
        Log.Info("Memory cache cleared.");
    }
    
    public async Task ClearAllAsync()
    {
        ClearMemoryCache();
        
        // Очистка диска
        var files = Directory.GetFiles(G.Folder.ImageCache);
        foreach (var f in files)
        {
            try 
            {
                // Пытаемся взять лок на файл если он используется
                var key = Path.GetFileNameWithoutExtension(f);
                var lockObj = GetFileLock(key);
                await lockObj.WaitAsync();
                try { File.Delete(f); } finally { lockObj.Release(); }
            }
            catch { }
        }
        Interlocked.Exchange(ref _currentDiskCacheBytes, 0);
        Log.Info("Image disk cache cleared.");
    }
    
    public (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache);
            long totalSize = files.Sum(static f => new FileInfo(f).Length);
            Interlocked.Exchange(ref _currentDiskCacheBytes, totalSize);
            return (files.Length, totalSize / 1024 / 1024);
        }
        catch
        {
            return (0, 0);
        }
    }

    private SemaphoreSlim GetFileLock(string key) => _fileLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private async Task<Bitmap?> LoadFromDiskOrNetwork(string url, string key, CancellationToken ct)
    {
        var fileLock = GetFileLock(key);

        try
        {
            await _downloadSemaphore.WaitAsync(ct);
            try
            {
                if (_memoryCache.TryGetValue(key, out var cached))
                {
                    TouchLru(key);
                    return cached.Bitmap;
                }

                await fileLock.WaitAsync(ct);
                try
                {
                    var diskPath = GetDiskPath(key);

                    if (!File.Exists(diskPath))
                    {
                        var bytes = await _http.GetByteArrayAsync(url, ct);
                        await File.WriteAllBytesAsync(diskPath, bytes, ct);
                        Interlocked.Add(ref _currentDiskCacheBytes, bytes.Length);
                        
                        // Check limit
                        long limitBytes = (long)_library.Data.Storage.ImageCacheLimitMb * 1024 * 1024;
                        if (_currentDiskCacheBytes > limitBytes)
                        {
                            _ = Task.Run(CleanupDiskCacheAsync, CancellationToken.None);
                        }
                    }

                    if (File.Exists(diskPath))
                    {
                        return await Task.Run(() =>
                        {
                            try
                            {
                                using var stream = File.OpenRead(diskPath);
                                var bmp = Bitmap.DecodeToWidth(stream, 300);
                                AddToMemoryCache(key, bmp);
                                return bmp;
                            }
                            catch (Exception)
                            {
                                try { File.Delete(diskPath); } catch { }
                                return null;
                            }
                        }, ct);
                    }
                    return null;
                }
                finally { fileLock.Release(); }
            }
            finally { _downloadSemaphore.Release(); }
        }
        catch { return null; }
    }

    private async Task EnsureCachedDiskOnlyAsync(string url, CancellationToken ct)
    {
        var key = GetCacheKey(url);
        var diskPath = GetDiskPath(key);
        if (File.Exists(diskPath)) return;
        _ = await LoadFromDiskOrNetwork(url, key, ct);
    }

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        lock (_lruLock)
        {
            while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
            {
                var oldest = _lruOrder.Last!.Value;
                _lruOrder.RemoveLast();
                _memoryCache.TryRemove(oldest, out _);
            }
            if (_memoryCache.TryAdd(key, new CachedImage { Bitmap = bitmap, CachedAt = DateTime.UtcNow }))
            {
                _lruOrder.AddFirst(key);
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_lruLock)
        {
            if (_lruOrder.Contains(key))
            {
                _lruOrder.Remove(key);
                _lruOrder.AddFirst(key);
            }
        }
    }

    private static string GetCacheKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..32];
    }

    private static string GetDiskPath(string key) => Path.Combine(G.Folder.ImageCache, $"{key}.jpg");

    private async Task CalculateDiskCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache);
            long total = 0;
            foreach (var file in files) total += new FileInfo(file).Length;
            Interlocked.Exchange(ref _currentDiskCacheBytes, total);
        }
        catch { }
    }

    private async Task CleanupDiskCacheAsync()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.ImageCache)
                .Select(static f => new FileInfo(f))
                .OrderBy(static f => f.LastAccessTime)
                .ToList();

            long limitBytes = (long)_library.Data.Storage.ImageCacheLimitMb * 1024 * 1024;
            long targetSize = limitBytes / 2;
            long deleted = 0;

            foreach (var file in files)
            {
                if (_currentDiskCacheBytes - deleted <= targetSize) break;

                var key = Path.GetFileNameWithoutExtension(file.Name);
                var fileLock = GetFileLock(key);
                if (await fileLock.WaitAsync(0))
                {
                    try
                    {
                        var size = file.Length;
                        file.Delete();
                        deleted += size;
                        _fileLocks.TryRemove(key, out _);
                    }
                    catch { }
                    finally { fileLock.Release(); }
                }
            }
            Interlocked.Add(ref _currentDiskCacheBytes, -deleted);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ClearMemoryCache();
        _downloadSemaphore.Dispose();
        _http.Dispose();
        foreach (var l in _fileLocks.Values) l.Dispose();
        _fileLocks.Clear();
        GC.SuppressFinalize(this);
    }
}
```

Core/Services/LibraryService.cs:
```cs
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


```

Core/Services/MemoryFirstCachingStream.cs:
```cs
// === ФАЙЛ: Core/Services/MemoryFirstCachingStream.cs ===
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using LMP.Core.Models;

namespace LMP.Core.Services;

public sealed class MemoryFirstCachingStream : Stream
{
    private readonly int _chunkSize;
    private readonly int _readAheadChunks;
    private readonly int _maxConcurrentDownloads;
    private readonly int _maxRamChunks;
    private readonly int _downloadTimeoutMs;

    private const int ProgressLogIntervalBytes = 6 * 1024 * 1024;
    private const int MaxFileOpenRetries = 10;
    private const int FileOpenRetryDelayMs = 100;

    private readonly string _trackId;
    private string _url;
    private readonly Func<CancellationToken, Task<string?>>? _urlRefresher;

    private readonly long _contentLength;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly StreamCacheManager _cacheManager;
    private readonly RangeMap _diskRanges;
    private readonly int _totalChunks;

    private readonly ConcurrentDictionary<int, byte[]> _chunks = new();
    private readonly ConcurrentDictionary<int, Task> _pendingDownloads = new();

    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly PriorityQueue<int, int> _downloadQueue = new();
    private readonly HashSet<int> _queuedChunks = [];
    private readonly Lock _queueLock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);
    private readonly Channel<(long Pos, byte[] Data, int Len)> _diskChannel;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationTokenSource _downloadCts;

    private readonly Task _diskWriterTask;
    private Task? _downloadLoop;
    private FileStream? _cacheFile;

    private long _position;
    private long _bytesDownloaded;
    private volatile bool _downloadComplete;
    private volatile bool _disposed;
    private volatile bool _disposing;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _contentLength;

    public override long Position
    {
        get => Volatile.Read(ref _position);
        set => Seek(value, SeekOrigin.Begin);
    }

    public double DownloadProgress => _contentLength <= 0 ? 0 :
        Math.Min((double)Volatile.Read(ref _bytesDownloaded) / _contentLength * 100, 100);

    public bool IsFullyDownloaded => _downloadComplete;

    public MemoryFirstCachingStream(
        string trackId, string url, long contentLength,
        HttpClient http, StreamCacheManager cacheManager, StreamingConfig config,
        Func<CancellationToken, Task<string?>>? urlRefresher = null)
    {
        _trackId = trackId;
        _url = url;
        _contentLength = contentLength;
        _http = http;
        _cacheManager = cacheManager;
        _urlRefresher = urlRefresher;

        _chunkSize = config.ChunkSize;
        _readAheadChunks = config.ReadAheadChunks;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        // Fallback если в конфиге пришел 0 (защита от дурака)
        _maxRamChunks = config.MaxRamChunks > 0 ? config.MaxRamChunks : 50;
        _downloadTimeoutMs = config.DownloadTimeoutMs;

        _cachePath = StreamCacheManager.GetCachePath(trackId);
        _downloadCts = new CancellationTokenSource();
        _downloadSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        _totalChunks = (int)((_contentLength + _chunkSize - 1) / _chunkSize);

        var meta = cacheManager.LoadOrCreateMetadata(trackId, url, contentLength);
        _diskRanges = RangeMap.Deserialize(meta.RangesJson);
        _bytesDownloaded = _diskRanges.DownloadedBytes;

        _cacheFile = OpenCacheFileWithRetry(_cachePath);
        if (_cacheFile != null && _cacheFile.Length < _contentLength)
            _cacheFile.SetLength(_contentLength);

        _diskChannel = Channel.CreateBounded<(long, byte[], int)>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        _diskWriterTask = Task.Run(DiskWriterLoopAsync);

        Log.Info($"Opened {trackId}: {contentLength / 1024 / 1024}MB. RAM Limit: {_maxRamChunks} chunks.");
    }

    private static FileStream? OpenCacheFileWithRetry(string path)
    {
        // 1. Сначала проверяем директорию, чтобы избежать DirectoryNotFoundException
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to create cache directory: {ex.Message}");
                return null; // Если не можем создать папку, нет смысла пытаться создать файл
            }
        }

        for (int attempt = 1; attempt <= MaxFileOpenRetries; attempt++)
        {
            try
            {
                // Используем FileShare.ReadWrite, чтобы позволить другим процессам (или потокам) читать
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            catch (IOException) when (attempt < MaxFileOpenRetries)
            {
                // Это ловит "File is being used by another process"
                Thread.Sleep(FileOpenRetryDelayMs * attempt);
            }
            catch (Exception ex)
            {
                // Логируем реальную ошибку, чтобы не гадать (например, AccessDenied)
                Log.Error($"Cache file open error ({path}): {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public async Task<bool> PreBufferAsync(CancellationToken ct)
    {
        if (_disposed || _disposing) return false;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token, _disposeCts.Token);
            var token = linkedCts.Token;
            _downloadLoop ??= Task.Run(() => DownloadLoopAsync(token), token);

            if (HasChunk(0)) return true;
            EnqueueUrgent(0);

            var sw = Stopwatch.StartNew();
            while (!HasChunk(0))
            {
                if (token.IsCancellationRequested) return false;
                if (!_dataAvailable.Wait(200, token))
                    if (sw.ElapsedMilliseconds > _downloadTimeoutMs) return false;
                if (!HasChunk(0)) _dataAvailable.Reset();
            }
            return true;
        }
        catch { return false; }
    }

    public void CancelPendingReads() { try { _disposeCts.Cancel(); } catch { } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;
        long pos = Volatile.Read(ref _position);
        if (pos >= _contentLength) return 0;

        count = (int)Math.Min(count, _contentLength - pos);
        if (count <= 0) return 0;

        int chunkIndex = (int)(pos / _chunkSize);
        int offsetInChunk = (int)(pos % _chunkSize);
        int toRead = Math.Min(count, _chunkSize - offsetInChunk);

        try
        {
            // Ожидание чанка
            while (!HasChunk(chunkIndex))
            {
                if (_disposed || _disposing || _disposeCts.IsCancellationRequested) return 0;
                EnqueueUrgent(chunkIndex);
                try { if (!_dataAvailable.Wait(500, _disposeCts.Token)) { } } catch { return 0; }
                if (!HasChunk(chunkIndex)) _dataAvailable.Reset();
            }

            int bytesRead = TryReadChunk(chunkIndex, offsetInChunk, buffer, offset, toRead);
            if (bytesRead > 0)
            {
                Interlocked.Add(ref _position, bytesRead);
                EnqueueReadAhead(chunkIndex);
            }
            return bytesRead;
        }
        catch { return 0; }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_disposed) return 0;
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _contentLength + offset,
            _ => throw new ArgumentException("Invalid origin")
        };
        newPos = Math.Clamp(newPos, 0, _contentLength);
        Volatile.Write(ref _position, newPos);
        int newChunk = (int)(newPos / _chunkSize);
        EnqueueUrgent(newChunk);
        return newPos;
    }

    private int TryReadChunk(int idx, int off, byte[] buf, int bufOff, int count)
    {
        if (_chunks.TryGetValue(idx, out var chunk))
        {
            int usefulDataLength = (idx == _totalChunks - 1) ? (int)(_contentLength - ((long)idx * _chunkSize)) : _chunkSize;
            int available = Math.Min(count, usefulDataLength - off);
            if (available > 0) Buffer.BlockCopy(chunk, off, buf, bufOff, available);
            return available;
        }
        long start = (long)idx * _chunkSize;
        if (_diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength)))
            return ReadFromDisk(start + off, buf, bufOff, count);
        return 0;
    }

    private int ReadFromDisk(long pos, byte[] buf, int off, int count)
    {
        if (_cacheFile == null || _disposing) return 0;
        try
        {
            _fileSemaphore.Wait(_disposeCts.Token);
            try
            {
                if (_cacheFile == null) return 0;
                _cacheFile.Seek(pos, SeekOrigin.Begin);
                return _cacheFile.Read(buf, off, count);
            }
            finally { _fileSemaphore.Release(); }
        }
        catch { return 0; }
    }

    private void EnqueueUrgent(int idx)
    {
        lock (_queueLock)
        {
            TryEnqueue(idx, 0);
            for (int i = 1; i <= 3 && idx + i < _totalChunks; i++) TryEnqueue(idx + i, i);
        }
    }

    private void EnqueueReadAhead(int current)
    {
        lock (_queueLock)
        {
            for (int i = 1; i <= _readAheadChunks && current + i < _totalChunks; i++)
                TryEnqueue(current + i, 50 + i);
        }
    }

    private void TryEnqueue(int idx, int priority)
    {
        if (HasChunk(idx)) return;
        if (_pendingDownloads.ContainsKey(idx)) return;
        if (_queuedChunks.Add(idx)) _downloadQueue.Enqueue(idx, priority);
    }

    private bool HasChunk(int idx)
    {
        if (_chunks.ContainsKey(idx)) return true;
        long start = (long)idx * _chunkSize;
        return _diskRanges.IsRangeComplete(start, Math.Min(start + _chunkSize, _contentLength));
    }

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastLog = 0;

        while (!ct.IsCancellationRequested && !_disposing)
        {
            int chunk = -1;
            lock (_queueLock)
            {
                while (_downloadQueue.Count > 0)
                {
                    var c = _downloadQueue.Dequeue();
                    _queuedChunks.Remove(c);
                    if (!HasChunk(c) && !_pendingDownloads.ContainsKey(c)) { chunk = c; break; }
                }
            }

            if (chunk < 0)
            {
                if (IsAllDownloaded())
                {
                    _downloadComplete = true;
                    break;
                }
                try { await Task.Delay(100, ct); } catch { break; }
                continue;
            }

            try { await _downloadSemaphore.WaitAsync(ct); } catch { break; }
            _ = DownloadChunkSafeAsync(chunk, ct);

            long bytes = Volatile.Read(ref _bytesDownloaded);
            if (bytes - lastLog >= ProgressLogIntervalBytes)
            {
                lastLog = bytes;
                // Log.Info...
            }
        }
    }

    private async Task DownloadChunkSafeAsync(int idx, CancellationToken ct)
    {
        try
        {
            await DownloadChunkAsync(idx, ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Warn($"Chunk {idx} error: {ex.Message}");
        }
        finally
        {
            _pendingDownloads.TryRemove(idx, out _);
            _downloadSemaphore.Release();
        }
    }

    private async Task DownloadChunkAsync(int idx, CancellationToken ct)
    {
        if (HasChunk(idx) || _disposing) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDownloads.TryAdd(idx, tcs.Task)) return;

        byte[]? buffer = null;
        int maxRetries = 2;
        int retry = 0;

        while (retry <= maxRetries)
        {
            try
            {
                long start = (long)idx * _chunkSize;
                long end = Math.Min(start + _chunkSize - 1, _contentLength - 1);

                using var req = new HttpRequestMessage(HttpMethod.Get, _url);
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _downloadCts.Token);
                cts.CancelAfter(_downloadTimeoutMs);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (retry < maxRetries && _urlRefresher != null)
                    {
                        await _refreshLock.WaitAsync(cts.Token);
                        try
                        {
                            var newUrl = await _urlRefresher(cts.Token);
                            if (!string.IsNullOrEmpty(newUrl)) _url = newUrl;
                        }
                        finally { _refreshLock.Release(); }
                        retry++;
                        continue;
                    }
                }

                resp.EnsureSuccessStatusCode();

                // Rent buffer
                buffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
                using var netStream = await resp.Content.ReadAsStreamAsync(cts.Token);

                int totalRead = 0, bytesRead;
                while ((bytesRead = await netStream.ReadAsync(buffer, totalRead, _chunkSize - totalRead, cts.Token)) > 0)
                    totalRead += bytesRead;

                if (!_chunks.ContainsKey(idx) && !_disposing)
                {
                    _chunks[idx] = buffer;
                    Interlocked.Add(ref _bytesDownloaded, totalRead);
                    _dataAvailable.Set();

                    if (_cacheFile != null && !_disposing)
                    {
                        // Copy for disk write
                        byte[] diskBuf = ArrayPool<byte>.Shared.Rent(totalRead);
                        Buffer.BlockCopy(buffer, 0, diskBuf, 0, totalRead);

                        // ПИШЕМ В КАНАЛ. Если канал полон и мы отменяем, writeAsync выбросит исключение,
                        // и мы должны будем вернуть diskBuf (см. finally/catch)
                        // НО здесь мы передаем владение каналу.
                        await _diskChannel.Writer.WriteAsync((start, diskBuf, totalRead), cts.Token);
                    }

                    // Buffer успешно передан в _chunks, обнуляем ссылку, чтобы finally не вернул его
                    buffer = null;

                    // Trigger RAM cleanup
                    if (_chunks.Count > _maxRamChunks) TrimRamCache();
                }

                tcs.SetResult();
                break;
            }
            catch (Exception ex)
            {
                if (retry >= maxRetries || ex is OperationCanceledException)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                await Task.Delay(500, ct);
                retry++;
            }
            finally
            {
                // Если buffer не null, значит мы не успели его сохранить в _chunks
                // (ошибка или отмена), возвращаем в пул.
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void TrimRamCache()
    {
        // Более строгая логика очистки
        if (_chunks.Count <= _maxRamChunks) return;

        int current = (int)(Volatile.Read(ref _position) / _chunkSize);

        // Удаляем все, что слишком далеко позади или слишком далеко впереди
        var toRemove = _chunks.Keys
            .Where(i => i < current - 2 || i > current + _readAheadChunks * 2)
            .ToList(); // ToList чтобы не держать лок коллекции

        foreach (var i in toRemove)
        {
            if (_chunks.TryRemove(i, out var buf))
                ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private bool IsAllDownloaded()
    {
        for (int i = 0; i < _totalChunks; i++) if (!HasChunk(i)) return false;
        return true;
    }

    // ИСПРАВЛЕННЫЙ DiskWriterLoop
    private async Task DiskWriterLoopAsync()
    {
        int bytesWrittenSinceSave = 0;
        const int SaveThreshold = 512 * 1024;

        try
        {
            // Читаем, пока канал не закроется ИЛИ пока не отменят
            while (await _diskChannel.Reader.WaitToReadAsync(_disposeCts.Token))
            {
                while (_diskChannel.Reader.TryRead(out var item))
                {
                    var (pos, data, len) = item;
                    try
                    {
                        if (_disposing || _cacheFile == null) continue; // Просто вернем буфер в finally

                        await _fileSemaphore.WaitAsync(_disposeCts.Token);
                        try
                        {
                            if (_cacheFile != null)
                            {
                                _cacheFile.Seek(pos, SeekOrigin.Begin);
                                await _cacheFile.WriteAsync(data, 0, len, _disposeCts.Token);
                            }
                        }
                        finally { _fileSemaphore.Release(); }

                        _diskRanges.MarkComplete(pos, pos + len);
                        bytesWrittenSinceSave += len;

                        if (bytesWrittenSinceSave >= SaveThreshold)
                        {
                            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
                            bytesWrittenSinceSave = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException) Log.Error($"Disk write error: {ex.Message}");
                    }
                    finally
                    {
                        // КРИТИЧЕСКИ ВАЖНО: Всегда возвращаем буфер
                        ArrayPool<byte>.Shared.Return(data);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальное завершение при Dispose
        }
        catch (Exception ex)
        {
            Log.Error($"Disk writer loop crash: {ex.Message}");
        }

        // При выходе из цикла (отмена или ошибка) может остаться мусор в канале?
        // WaitToReadAsync выбросит OperationCanceledException, и мы попадем в catch.
        // Но TryRead может не вычитать всё.
        // Поэтому очистку остатков делаем в Dispose или здесь в finally.
        // Но лучше и надежнее в Dispose, когда Writer уже Complete.
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposing = true;
        _disposed = true;

        if (disposing)
        {
            Try(_downloadCts.Cancel);
            Try(_disposeCts.Cancel);

            // 1. Закрываем Writer, чтобы никто больше не писал
            Try(() => _diskChannel.Writer.TryComplete());

            // 2. Очищаем канал и возвращаем буферы (УТЕЧКА БЫЛА ЗДЕСЬ)
            while (_diskChannel.Reader.TryRead(out var item))
            {
                ArrayPool<byte>.Shared.Return(item.Data);
            }

            Try(() => _cacheManager.UpdateRanges(_trackId, _diskRanges));
            Try(_dataAvailable.Set);

            Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAny(_diskWriterTask, Task.Delay(1000));
                    await _fileSemaphore.WaitAsync(2000);
                    try
                    {
                        Try(() => _cacheFile?.Flush());
                        Try(() => _cacheFile?.Dispose());
                        _cacheFile = null;
                    }
                    finally { _fileSemaphore.Release(); }
                }
                catch { }
                finally
                {
                    // 3. Возвращаем все чанки из RAM кэша
                    foreach (var buf in _chunks.Values)
                        Try(() => ArrayPool<byte>.Shared.Return(buf));
                    _chunks.Clear();

                    Try(_fileSemaphore.Dispose);
                    Try(_downloadSemaphore.Dispose);
                    Try(_refreshLock.Dispose);
                    Try(_downloadCts.Dispose);
                    Try(_disposeCts.Dispose);
                }
            });
        }
        base.Dispose(disposing);
    }

    private static void Try(Action a) { try { a(); } catch { } }
}
```

Core/Services/LocalizationService.cs:
```cs
using Avalonia.Platform;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace LMP.Core.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public readonly static LocalizationService Instance = new();

    private string _currentLanguage = "en"; // Дефолт - английский
    private Dictionary<string, string> _resources = [];
    private bool _isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    public List<LanguageItem> AvailableLanguages { get; } =
    [
        new() { Code = "en", Name = "English" },
        new() { Code = "ru", Name = "Русский" }
    ];

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value && AvailableLanguages.Any(l => l.Code == value))
            {
                Log.Info($"Changing language: {_currentLanguage} → {value}");
                _currentLanguage = value;
                LoadLanguage(value);

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                LanguageChanged?.Invoke(this, value);
            }
        }
    }

    // Приватный конструктор - НЕ загружаем язык автоматически!
    private LocalizationService()
    {
        // Загружаем английский как fallback
        LoadLanguage("en");
        Log.Info("Service created with default language: en");
    }

    /// <summary>
    /// Инициализация с сохранённым языком. Вызывать при старте приложения!
    /// </summary>
    public void Initialize(string? savedLanguageCode)
    {
        if (_isInitialized) return;

        string langToUse = "en";

        // 1. Приоритет: сохранённые настройки
        if (!string.IsNullOrEmpty(savedLanguageCode) &&
            AvailableLanguages.Any(l => l.Code == savedLanguageCode))
        {
            langToUse = savedLanguageCode;
            Log.Info($"Using saved language: {langToUse}");
        }
        // 2. Fallback: системная локаль
        else
        {
            var sysLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (AvailableLanguages.Any(l => l.Code == sysLang))
            {
                langToUse = sysLang;
                Log.Info($"Using system language: {langToUse}");
            }
            else
            {
                Log.Info($"System language '{sysLang}' not supported, using: en");
            }
        }

        _currentLanguage = langToUse;
        LoadLanguage(langToUse);
        _isInitialized = true;
    }

    private void LoadLanguage(string langCode)
    {
        try
        {
            var uri = new Uri($"avares://LMP/Assets/Localization/{langCode}.json");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                _resources = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                             ?? new Dictionary<string, string>();
                Log.Info($"Loaded {langCode}.json ({_resources.Count} keys)");
            }
            else
            {
                Log.Info($"File not found: {uri}");
                if (langCode != "en") LoadLanguage("en");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Error loading '{langCode}': {ex.Message}");
            if (langCode != "en") LoadLanguage("en");
        }
    }

    /// <summary>
    /// Индексатор для получения строки
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (_resources.TryGetValue(key, out var value))
                return value;

            Log.Info($"Missing key: {key}");
            return $"[{key}]";
        }
    }

    /// <summary>
    /// Альтернативный метод получения строки
    /// </summary>
    public string Get(string key, string? fallback = null)
    {
        if (_resources.TryGetValue(key, out var value))
            return value;
        return fallback ?? $"[{key}]";
    }

    public string RawGet(string key) => _resources[key];

    public string GetPlural(string key, int number)
    {
        if (!_resources.TryGetValue(key, out var val)) return key;

        var forms = val.Split('|', StringSplitOptions.RemoveEmptyEntries);
        int n = Math.Abs(number);

        // Логика для Русского (3 формы: 1 трек, 2 трека, 5 треков)
        if (_currentLanguage == "ru")
        {
            if (forms.Length < 3) return $"{number} {forms[0]}";

            int n100 = n % 100;
            int n10 = n % 10;

            if (n100 > 10 && n100 < 20) return $"{number} {forms[2]}";
            if (n10 > 1 && n10 < 5) return $"{number} {forms[1]}";
            if (n10 == 1) return $"{number} {forms[0]}";
            return $"{number} {forms[2]}";
        }

        // Логика для Английского (2 формы: 1 track, 2 tracks)
        // И общий фоллбек
        if (forms.Length >= 2)
        {
            return n == 1 ? $"{number} {forms[0]}" : $"{number} {forms[1]}";
        }

        return $"{number} {forms[0]}";
    }
}

public class LanguageItem
{
    public required string Code { get; set; }
    public required string Name { get; set; }

    public override string ToString() => Name;
}

```

Core/Services/MusicLibraryManager.cs:
```cs
using LMP.Core.Models;
using ReactiveUI;

namespace LMP.Core.Services;

public class MusicLibraryManager(
    LibraryService library,
    YoutubeUserDataService ytUser,
    CookieAuthService auth) : ReactiveObject // Changed
{
    private readonly LibraryService _library = library;
    private readonly YoutubeUserDataService _ytUser = ytUser;
    private readonly CookieAuthService _auth = auth; // Changed

    public async Task ToggleLikeAsync(TrackInfo track)
    {
        // Сначала пытаемся отправить запрос, и только при успехе меняем локальный статус
        if (_auth.IsAuthenticated)
        {
            try
            {
                bool newStatus = !track.IsLiked;
                string rating = newStatus ? "like" : "none";
                await _ytUser.RateVideoAsync(track.Id, rating);

                // Только если не вылетело исключение:
                _library.ToggleLike(track);
                Log.Info($"[Sync] Track {track.Id} rated '{rating}' on YouTube.");
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to sync like: {ex.Message}");
                // Можно добавить уведомление пользователю через событие или DialogService
            }
        }
        else
        {
            // Если оффлайн, просто меняем локально
            _library.ToggleLike(track);
        }
    }


    public async Task SyncLikedTracksAsync()
    {
        if (!_auth.IsAuthenticated) return;

        try
        {
            Log.Info("[Sync] Starting liked videos sync from YouTube...");
            var likedTracks = await _ytUser.GetLikedTracksAsync();
            var localLiked = _library.GetLikedPlaylist();
            int addedCount = 0;

            // Важный момент с порядком! 
            // likedTracks[0] - это самый последний лайкнутый (Newest).
            // Мы вставляем в начало списка (Insert 0).
            // Чтобы сохранить порядок [Newest, Old1, Old2...], нужно вставлять с конца.
            // Иначе получится [Old2, Old1, Newest].
            
            // Если likedTracks пуст или null, метод вернет управление без ошибок
            if (likedTracks == null || likedTracks.Count == 0) return;

            // Переворачиваем список для вставки
            var tracksToProcess = ((IEnumerable<TrackInfo>)likedTracks).Reverse();

            foreach (var track in tracksToProcess)
            {
                track.IsLiked = true;
                _library.AddOrUpdateTrack(track);

                if (!localLiked.TrackIds.Contains(track.Id))
                {
                    localLiked.TrackIds.Insert(0, track.Id);
                    track.InPlaylists.Add("liked");
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                localLiked.UpdatedAt = DateTime.Now;
                _library.AddOrUpdatePlaylist(localLiked);
                Log.Info($"[Sync] Successfully added {addedCount} new liked tracks.");
            } else
            {
                Log.Info("[Sync] No new liked tracks found.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Sync] Liked tracks sync failed: {ex.Message}");
        }
    }

    public async Task CreatePlaylistAsync(string name, PlaylistSyncMode mode)
    {
        var newPlaylist = _library.CreatePlaylist(name);
        newPlaylist.SyncMode = mode;

        if (mode == PlaylistSyncMode.TwoWaySync && _auth.IsAuthenticated)
        {
            try
            {
                var ytId = await _ytUser.CreatePlaylistAsync(name, $"Created via {G.AppName}");
                newPlaylist.YoutubeId = ytId;
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Failed to create remote playlist. {ex.Message}");
                newPlaylist.SyncMode = PlaylistSyncMode.LocalOnly;
            }
        }
        _library.AddOrUpdatePlaylist(newPlaylist);
    }

    public async Task DeletePlaylistAsync(string playlistId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.RemovePlaylist(playlistId);

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            try
            {
                await _ytUser.DeletePlaylistAsync(playlist.YoutubeId);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Error deleting remote playlist: {ex.Message}");
            }
        }
    }

    public async Task UploadPlaylistToAccountAsync(string localPlaylistId)
    {
        if (!_auth.IsAuthenticated) return;

        var localPl = _library.GetPlaylist(localPlaylistId);
        if (localPl == null || localPl.SyncMode != PlaylistSyncMode.LocalOnly) return;

        try
        {
            var ytId = await _ytUser.CreatePlaylistAsync(localPl.Name, $"Uploaded from {G.AppName}");
            localPl.YoutubeId = ytId;
            localPl.SyncMode = PlaylistSyncMode.TwoWaySync;
            _library.AddOrUpdatePlaylist(localPl);

            _ = Task.Run(async () =>
            {
                foreach (var trackId in localPl.TrackIds)
                {
                    if (trackId.StartsWith("yt_"))
                    {
                        await _ytUser.AddTrackToPlaylistAsync(ytId, trackId);
                        await Task.Delay(600);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Upload failed: {ex.Message}");
        }
    }

    public async Task AddTrackToPlaylistAsync(string playlistId, TrackInfo track)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        _library.AddOrUpdateTrack(track);

        if (!playlist.TrackIds.Contains(track.Id))
        {
            playlist.TrackIds.Add(track.Id);
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);
            track.InPlaylists.Add(playlistId);
        }

        if (playlist.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(playlist.YoutubeId) &&
            _auth.IsAuthenticated)
        {
            try
            {
                await _ytUser.AddTrackToPlaylistAsync(playlist.YoutubeId, track.Id);
            }
            catch (Exception ex)
            {
                Log.Error($"[Sync] Add track failed: {ex.Message}");
            }
        }
    }

    public async Task RemoveTrackFromPlaylistAsync(string playlistId, string trackId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;

        if (playlist.TrackIds.Remove(trackId))
        {
            playlist.UpdatedAt = DateTime.Now;
            _library.AddOrUpdatePlaylist(playlist);
            var t = _library.GetTrack(trackId);
            if (t != null) t.InPlaylists.Remove(playlistId);
        }
        // Removal from YouTube via InnerTube needs extra logic (setVideoId), skipped for now
    }

    public void ConvertToLocal(string playlistId)
    {
        var pl = _library.GetPlaylist(playlistId);
        if (pl == null) return;

        var copy = new Playlist
        {
            Name = pl.Name + " (Local)",
            SyncMode = PlaylistSyncMode.LocalOnly,
            TrackIds = [.. pl.TrackIds],
            ThumbnailUrl = pl.ThumbnailUrl,
            Author = "Me"
        };
        _library.AddOrUpdatePlaylist(copy);
    }
}
```

Core/Services/SearchCacheService.cs:
```cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Models;

namespace LMP.Core.Services;

public class CachedSearchResult
{
    public string Query { get; set; } = "";
    public DateTime CachedAt { get; set; }
    public List<TrackInfo> Tracks { get; set; } = [];
}

public class SearchCacheService
{
    private readonly int _maxCacheFiles = 50;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory LRU cache для горячих запросов
    private readonly Dictionary<string, CachedSearchResult> _memoryCache = [];
    private readonly LinkedList<string> _lruOrder = new();
    private const int MaxMemoryCacheItems = 10;

    // Ленивый доступ к LibraryService (избегаем циклических зависимостей при DI)
    private LibraryService? _libService;
    private LibraryService LibService => _libService ??= Program.Services.GetRequiredService<LibraryService>();

    /// <summary>
    /// TTL из настроек пользователя (в минутах), по умолчанию 60
    /// </summary>
    private TimeSpan CacheTtl => TimeSpan.FromMinutes(
        LibService.Data.SearchCacheTtlMinutes > 0 
            ? LibService.Data.SearchCacheTtlMinutes 
            : 60);

    public SearchCacheService()
    {
        // Очистка старого кэша при старте
        _ = Task.Run(CleanupOldCacheAsync);
    }

    /// <summary>
    /// Получить из кэша (память → диск)
    /// </summary>
    public async Task<List<TrackInfo>?> GetAsync(string query, int minCount = 10)
    {
        var key = GetCacheKey(query);
        var ttl = CacheTtl; // Читаем TTL один раз для консистентности

        // 1. Проверяем память
        if (_memoryCache.TryGetValue(key, out var memResult))
        {
            var age = DateTime.UtcNow - memResult.CachedAt;
            
            if (age < ttl && memResult.Tracks.Count >= minCount)
            {
                Log.Info($"[SearchCache] Memory HIT for '{query}' ({memResult.Tracks.Count} tracks, age: {age.TotalMinutes:F0}min, ttl: {ttl.TotalMinutes}min)");
                TouchLru(key);
                return memResult.Tracks;
            }
            
            if (age >= ttl)
            {
                Log.Info($"[SearchCache] Memory EXPIRED for '{query}' (age: {age.TotalMinutes:F0}min > ttl: {ttl.TotalMinutes}min)");
                lock (_memoryCache)
                {
                    _memoryCache.Remove(key);
                    _lruOrder.Remove(key);
                }
            }
        }

        // 2. Проверяем диск
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(G.Folder.SearchCache, $"{key}.json");
            if (!File.Exists(filePath)) return null;

            var json = await File.ReadAllTextAsync(filePath);
            var cached = JsonSerializer.Deserialize<CachedSearchResult>(json);

            if (cached == null) return null;

            var age = DateTime.UtcNow - cached.CachedAt;

            // Проверяем TTL
            if (age > ttl)
            {
                Log.Info($"[SearchCache] Disk EXPIRED for '{query}' (age: {age.TotalMinutes:F0}min > ttl: {ttl.TotalMinutes}min)");
                File.Delete(filePath);
                return null;
            }

            if (cached.Tracks.Count < minCount)
            {
                Log.Info($"[SearchCache] Disk has only {cached.Tracks.Count} tracks, need {minCount}");
                return null;
            }

            Log.Info($"[SearchCache] Disk HIT for '{query}' ({cached.Tracks.Count} tracks, age: {age.TotalMinutes:F0}min)");

            // Добавляем в память
            AddToMemoryCache(key, cached);

            return cached.Tracks;
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Read error: {ex.Message}");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Сохранить в кэш (память + диск)
    /// </summary>
    public async Task SetAsync(string query, List<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;

        var key = GetCacheKey(query);
        var cached = new CachedSearchResult
        {
            Query = query,
            CachedAt = DateTime.UtcNow,
            Tracks = tracks
        };

        // 1. В память
        AddToMemoryCache(key, cached);

        // 2. На диск (асинхронно)
        await _lock.WaitAsync();
        try
        {
            var filePath = Path.Combine(G.Folder.SearchCache, $"{key}.json");
            var json = JsonSerializer.Serialize(cached, G.Json.Compact);
            await File.WriteAllTextAsync(filePath, json);
            Log.Info($"[SearchCache] Saved '{query}' to disk ({tracks.Count} tracks)");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Write error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Получить частичные данные для быстрого отображения
    /// </summary>
    public async Task<List<TrackInfo>> GetPartialAsync(string query, int count)
    {
        var cached = await GetAsync(query, minCount: 1);
        return cached?.Take(count).ToList() ?? [];
    }

    private void AddToMemoryCache(string key, CachedSearchResult result)
    {
        lock (_memoryCache)
        {
            if (_memoryCache.ContainsKey(key))
            {
                _memoryCache[key] = result;
                TouchLru(key);
            }
            else
            {
                // Удаляем старые если превышен лимит
                while (_memoryCache.Count >= MaxMemoryCacheItems && _lruOrder.Count > 0)
                {
                    var oldest = _lruOrder.Last!.Value;
                    _lruOrder.RemoveLast();
                    _memoryCache.Remove(oldest);
                }

                _memoryCache[key] = result;
                _lruOrder.AddFirst(key);
            }
        }
    }

    private void TouchLru(string key)
    {
        lock (_memoryCache)
        {
            _lruOrder.Remove(key);
            _lruOrder.AddFirst(key);
        }
    }

    private async Task CleanupOldCacheAsync()
    {
        try
        {
            var ttl = CacheTtl;
            var files = Directory.GetFiles(G.Folder.SearchCache, "*.json")
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ToList();

            int deletedCount = 0;

            // Удаляем старые файлы (превышение лимита)
            foreach (var file in files.Skip(_maxCacheFiles))
            {
                file.Delete();
                deletedCount++;
                Log.Info($"[SearchCache] Deleted excess cache: {file.Name}");
            }

            // Удаляем просроченные по TTL
            foreach (var file in files.Take(_maxCacheFiles))
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > ttl)
                {
                    file.Delete();
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                Log.Info($"[SearchCache] Cleanup: deleted {deletedCount} files (ttl: {ttl.TotalMinutes}min)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Cleanup error: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private static string GetCacheKey(string query)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(query.ToLowerInvariant().Trim()));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Инвалидирует кэш для конкретного запроса
    /// </summary>
    public void InvalidateQuery(string query)
    {
        var key = GetCacheKey(query);

        lock (_memoryCache)
        {
            _memoryCache.Remove(key);
            _lruOrder.Remove(key);
        }

        try
        {
            var filePath = Path.Combine(G.Folder.SearchCache, $"{key}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log.Info($"[SearchCache] Invalidated '{query}'");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Invalidate error: {ex.Message}");
        }
    }

    public void ClearAll()
    {
        lock (_memoryCache)
        {
            _memoryCache.Clear();
            _lruOrder.Clear();
        }

        try
        {
            foreach (var file in Directory.GetFiles(G.Folder.SearchCache, "*.json"))
            {
                File.Delete(file);
            }
            Log.Info("[SearchCache] Cleared all cache");
        }
        catch (Exception ex)
        {
            Log.Error($"[SearchCache] Clear error: {ex.Message}");
        }
    }

    /// <summary>
    /// Принудительная очистка просроченных записей
    /// Вызывается при изменении TTL в настройках
    /// </summary>
    public async Task CleanupExpiredAsync()
    {
        await CleanupOldCacheAsync();
        
        // Также чистим память
        var ttl = CacheTtl;
        var now = DateTime.UtcNow;
        
        lock (_memoryCache)
        {
            var expiredKeys = _memoryCache
                .Where(kv => now - kv.Value.CachedAt > ttl)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _memoryCache.Remove(key);
                _lruOrder.Remove(key);
            }

            if (expiredKeys.Count > 0)
            {
                Log.Info($"[SearchCache] Cleaned {expiredKeys.Count} expired memory entries");
            }
        }
    }

    /// <summary>
    /// Статистика кэша
    /// </summary>
    public (int MemoryItems, int DiskItems, long DiskSizeBytes, int TtlMinutes) GetStats()
    {
        int memCount = _memoryCache.Count;
        var files = Directory.GetFiles(G.Folder.SearchCache, "*.json");
        long size = files.Sum(static f => new FileInfo(f).Length);
        int ttl = (int)CacheTtl.TotalMinutes;
        return (memCount, files.Length, size, ttl);
    }
}


```

Core/Services/StreamCacheManager.cs:
```cs

// === ФАЙЛ: Core/Services/StreamCacheManager.cs ===
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LMP.Core.Models;

namespace LMP.Core.Services;

public class StreamCacheMetadata
{
    public string TrackId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public long ContentLength { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public string RangesJson { get; set; } = "[]";
    public string Codec { get; set; } = "";
    public int Bitrate { get; set; }
    public string Container { get; set; } = "";
}

public class StreamCacheManager : IDisposable
{
    private readonly LibraryService _library;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);

    public StreamCacheManager(LibraryService library)
    {
        _library = library;
        _ = Task.Run(CleanupOldCacheAsync);
    }

    public static string GetCachePath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.cache");
    }

    public static string GetMetaPath(string trackId)
    {
        var safeId = GetSafeFileName(trackId);
        return Path.Combine(G.Folder.StreamCache, $"{safeId}.meta");
    }

    public StreamCacheMetadata? TryGetMetadata(string trackId)
    {
        var metaPath = GetMetaPath(trackId);
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<StreamCacheMetadata>(json);
        }
        catch { return null; }
    }

    public StreamCacheMetadata LoadOrCreateMetadata(string trackId, string url, long contentLength)
    {
        var meta = TryGetMetadata(trackId);

        if (meta != null && meta.ContentLength == contentLength)
        {
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
            return meta;
        }

        var newMeta = new StreamCacheMetadata
        {
            TrackId = trackId,
            SourceUrl = url,
            ContentLength = contentLength,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            RangesJson = "[]"
        };

        var cachePath = GetCachePath(trackId);
        if (File.Exists(cachePath)) try { File.Delete(cachePath); } catch { }

        SaveMetadata(trackId, newMeta);
        return newMeta;
    }

    public static void SaveMetadata(string trackId, StreamCacheMetadata meta)
    {
        try
        {
            var metaPath = GetMetaPath(trackId);
            var json = JsonSerializer.Serialize(meta, G.Json.Beautiful);
            File.WriteAllText(metaPath, json);
        }
        catch { }
    }

    public void UpdateRanges(string trackId, RangeMap ranges)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            meta.RangesJson = ranges.Serialize();
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
        }
    }

    public void UpdateStreamInfo(string trackId, string codec, int bitrate, string container)
    {
        var meta = TryGetMetadata(trackId);
        if (meta != null)
        {
            meta.Codec = codec;
            meta.Bitrate = bitrate;
            meta.Container = container;
            meta.LastAccessedAt = DateTime.UtcNow;
            SaveMetadata(trackId, meta);
        }
    }

    public RangeMap LoadRanges(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        return meta != null ? RangeMap.Deserialize(meta.RangesJson) : new RangeMap();
    }

    public bool IsFullyCached(string trackId)
    {
        var meta = TryGetMetadata(trackId);
        if (meta == null) return false;
        if (!File.Exists(GetCachePath(trackId))) return false;
        var ranges = RangeMap.Deserialize(meta.RangesJson);
        return ranges.IsFullyDownloaded(meta.ContentLength);
    }
    
    public async Task ClearAllAsync()
    {
        await _cleanupLock.WaitAsync();
        try
        {
            foreach (var file in Directory.GetFiles(G.Folder.StreamCache))
            {
                try { File.Delete(file); } catch { }
            }
            Log.Info("All stream cache cleared");
        }
        finally { _cleanupLock.Release(); }
    }
    
    public static (int FileCount, long SizeMb) GetStats()
    {
        try
        {
            var files = Directory.GetFiles(G.Folder.StreamCache, "*.cache");
            long size = files.Sum(static f => new FileInfo(f).Length);
            return (files.Length, size / 1024 / 1024);
        }
        catch { return (0, 0); }
    }

    private async Task CleanupOldCacheAsync()
    {
        if (!await _cleanupLock.WaitAsync(0)) return;

        try
        {
            var files = Directory.GetFiles(G.Folder.StreamCache, "*.cache")
                .Select(static f => new FileInfo(f))
                .ToList();

            long totalSize = files.Sum(static f => f.Length);
            long maxCacheBytes = (long)_library.Data.Storage.AudioCacheLimitMb * 1024 * 1024;

            if (totalSize <= maxCacheBytes) return;

            Log.Info($"Stream cache size {totalSize / 1024 / 1024}MB exceeds limit {maxCacheBytes / 1024 / 1024}MB, cleaning...");

            var metaFiles = files
                .Select(static f => new
                {
                    CacheFile = f,
                    MetaFile = new FileInfo(Path.ChangeExtension(f.FullName, ".meta")),
                    LastAccess = GetLastAccessTime(Path.ChangeExtension(f.FullName, ".meta"))
                })
                .OrderBy(static x => x.LastAccess)
                .ToList();

            long targetSize = maxCacheBytes * 70 / 100;
            long deleted = 0;

            foreach (var item in metaFiles)
            {
                if (totalSize - deleted <= targetSize) break;
                try
                {
                    var size = item.CacheFile.Length;
                    item.CacheFile.Delete();
                    if (item.MetaFile.Exists) item.MetaFile.Delete();
                    deleted += size;
                }
                catch { }
            }
            Log.Info($"Cleaned {deleted / 1024 / 1024}MB");
        }
        finally { _cleanupLock.Release(); }
    }

    private static DateTime GetLastAccessTime(string metaPath)
    {
        try
        {
            if (File.Exists(metaPath))
            {
                var json = File.ReadAllText(metaPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("LastAccessedAt", out var prop) && prop.TryGetDateTime(out var dt))
                    return dt;
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    private static string GetSafeFileName(string trackId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(trackId));
        return Convert.ToHexString(bytes)[..32];
    }

    public void Dispose() => _cleanupLock.Dispose();
}
```

Core/Services/ThemeManagerService.cs:
```cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LMP.Core.Services;

/// <summary>
/// Настройки темы приложения.
/// Все цвета хранятся в HEX-формате (#RRGGBB или #AARRGGBB).
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>Имя темы для отображения</summary>
    public string Name { get; set; } = "Paralax Purple";

    // BACKGROUNDS - Фоновые цвета

    /// <summary>Основной фон окна</summary>
    public string BgPrimary { get; set; } = "#0F0B15";

    /// <summary>Фон карточек и сайдбара</summary>
    public string BgSecondary { get; set; } = "#1A1625";

    /// <summary>Фон диалогов и меню</summary>
    public string BgElevated { get; set; } = "#252033";

    /// <summary>Разделители и границы</summary>
    public string BgHighlight { get; set; } = "#322A45";

    /// <summary>Hover-состояние</summary>
    public string BgHover { get; set; } = "#3E3456";

    // SKELETON / LOADING - Цвета загрузки

    /// <summary>Скелетон светлый</summary>
    public string BgSkeleton { get; set; } = "#2A2438";

    /// <summary>Скелетон темный</summary>
    public string BgSkeletonDeep { get; set; } = "#15121C";

    /// <summary>Оверлей (полупрозрачный)</summary>
    public string BgOverlay { get; set; } = "#CC0A080F";

    // ACCENT - Акцентные цвета бренда

    /// <summary>Основной акцентный цвет (кнопки, ссылки)</summary>
    public string AccentColor { get; set; } = "#8A2BE2";

    /// <summary>Акцент при наведении</summary>
    public string AccentHover { get; set; } = "#A560F0";

    // SEMANTIC - Системные цвета

    /// <summary>Цвет ошибки</summary>
    public string SystemError { get; set; } = "#FF5555";

    /// <summary>Фон ошибки</summary>
    public string SystemErrorBg { get; set; } = "#331010";

    /// <summary>Информационный цвет</summary>
    public string SystemInfoBlue { get; set; } = "#8BE9FD";

    /// <summary>Предупреждение</summary>
    public string SystemWarnOrange { get; set; } = "#FFB86C";

    // TEXT - Цвета текста

    /// <summary>Основной текст</summary>
    public string TextPrimary { get; set; } = "#F8F8F2";

    /// <summary>Вторичный текст (подзаголовки)</summary>
    public string TextSecondary { get; set; } = "#BFB6D3";

    /// <summary>Приглушенный текст</summary>
    public string TextMuted { get; set; } = "#6272A4";

    /// <summary>Темный текст (на светлом фоне)</summary>
    public string TextDark { get; set; } = "#0F0B15";

    // SERIALIZATION

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }
}

/// <summary>
/// Сервис управления темами приложения.
/// Отвечает за загрузку, сохранение и применение тем.
/// </summary>
public sealed class ThemeManagerService
{
    private ThemeSettings? _cachedTheme;

    // PUBLIC API

    /// <summary>
    /// Загружает и применяет тему при старте приложения
    /// </summary>
    public void LoadAndApplyThemeOnStartup()
    {
        var theme = LoadThemeFromDisk();
        ApplyTheme(theme);
    }

    /// <summary>
    /// Применяет тему к ресурсам приложения
    /// </summary>
    public void ApplyTheme(ThemeSettings theme)
    {
        if (Application.Current?.Resources is not { } resources)
            return;

        _cachedTheme = theme;

        // Backgrounds
        SetColor(resources, "BgPrimary", theme.BgPrimary);
        SetColor(resources, "BgSecondary", theme.BgSecondary);
        SetColor(resources, "BgElevated", theme.BgElevated);
        SetColor(resources, "BgHighlight", theme.BgHighlight);
        SetColor(resources, "BgHover", theme.BgHover);
        SetColor(resources, "BgSkeleton", theme.BgSkeleton);
        SetColor(resources, "BgSkeletonDeep", theme.BgSkeletonDeep);
        SetColor(resources, "BgOverlay", theme.BgOverlay);

        // Accent
        SetColor(resources, "Accent", theme.AccentColor);
        SetColor(resources, "AccentHover", theme.AccentHover);

        // Semantic
        SetColor(resources, "SystemError", theme.SystemError);
        SetColor(resources, "SystemErrorBg", theme.SystemErrorBg);
        SetColor(resources, "SystemInfoBlue", theme.SystemInfoBlue);
        SetColor(resources, "SystemWarnOrange", theme.SystemWarnOrange);

        // Text
        SetColor(resources, "TextPrimary", theme.TextPrimary);
        SetColor(resources, "TextSecondary", theme.TextSecondary);
        SetColor(resources, "TextMuted", theme.TextMuted);
        SetColor(resources, "TextDark", theme.TextDark);

        // System accent compatibility
        if (TryParseColor(theme.AccentColor, out var accent))
        {
            resources["SystemAccentColor"] = accent;
            if (TryParseColor(theme.AccentHover, out var accentHover))
                resources["SystemAccentColorLight1"] = accentHover;
        }

        Log.Info($"Theme '{theme.Name}' applied.");
    }

    /// <summary>
    /// Сохраняет тему на диск
    /// </summary>
    public void SaveTheme(ThemeSettings theme)
    {
        try
        {
            var json = JsonSerializer.Serialize(theme, G.Json.Beautiful);
            File.WriteAllText(G.File.Theme, json);
            _cachedTheme = theme;
            Log.Info($"Theme '{theme.Name}' saved.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save theme: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает текущую загруженную тему
    /// </summary>
    public ThemeSettings GetCurrentTheme()
    {
        return _cachedTheme ?? LoadThemeFromDisk();
    }

    /// <summary>
    /// Возвращает дефолтную тему (Paralax Purple)
    /// </summary>
    public static ThemeSettings GetDefaultTheme() => new() { IsBuiltIn = true };

    /// <summary>
    /// Сбрасывает тему к дефолтной
    /// </summary>
    public void ResetToDefault()
    {
        try
        {
            if (File.Exists(G.File.Theme))
                File.Delete(G.File.Theme);
        }
        catch { /* Игнорируем ошибку удаления */ }

        var def = GetDefaultTheme();
        SaveTheme(def);
        ApplyTheme(def);
    }

    /// <summary>
    /// Возвращает список встроенных пресетов тем
    /// </summary>
    public static IReadOnlyList<ThemeSettings> GetBuiltInPresets() =>
    [
        // ═══ 1. PARALAX PURPLE (Default) ═══
        new ThemeSettings { IsBuiltIn = true },

        // ═══ 2. CLASSIC GREEN (Spotify-like) ═══
        new ThemeSettings
        {
            Name = "Classic Green",
            IsBuiltIn = true,
            BgPrimary = "#121212",
            BgSecondary = "#1E1E1E",
            BgElevated = "#282828",
            BgHighlight = "#404040",
            BgHover = "#505050",
            BgSkeleton = "#282828",
            BgSkeletonDeep = "#1a1a1a",
            BgOverlay = "#CC121212",
            AccentColor = "#1DB954",
            AccentHover = "#1ED760",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#B3B3B3",
            TextMuted = "#888888",
            TextDark = "#000000"
        },

        // ═══ 3. OCEAN DEEP ═══
        new ThemeSettings
        {
            Name = "Ocean Deep",
            IsBuiltIn = true,
            BgPrimary = "#001219",
            BgSecondary = "#001f2d",
            BgElevated = "#002d42",
            BgHighlight = "#003b57",
            BgHover = "#00506b",
            BgSkeleton = "#002535",
            BgSkeletonDeep = "#000d12",
            BgOverlay = "#CC001219",
            AccentColor = "#00B4D8",
            AccentHover = "#48CAE4",
            TextPrimary = "#E0FBFC",
            TextSecondary = "#98C1D9",
            TextMuted = "#5B8FA8",
            TextDark = "#001219"
        },

        // ═══ 4. AMOLED BLACK ═══
        new ThemeSettings
        {
            Name = "AMOLED Black",
            IsBuiltIn = true,
            BgPrimary = "#000000",
            BgSecondary = "#0A0A0A",
            BgElevated = "#141414",
            BgHighlight = "#1F1F1F",
            BgHover = "#2A2A2A",
            BgSkeleton = "#141414",
            BgSkeletonDeep = "#050505",
            BgOverlay = "#CC000000",
            AccentColor = "#FFFFFF",
            AccentHover = "#E0E0E0",
            TextPrimary = "#FFFFFF",
            TextSecondary = "#A0A0A0",
            TextMuted = "#606060",
            TextDark = "#000000"
        },

        // ═══ 5. WARM SUNSET ═══
        new ThemeSettings
        {
            Name = "Warm Sunset",
            IsBuiltIn = true,
            BgPrimary = "#1A1210",
            BgSecondary = "#261A16",
            BgElevated = "#33221C",
            BgHighlight = "#4A3228",
            BgHover = "#5C3F32",
            BgSkeleton = "#2A1C18",
            BgSkeletonDeep = "#120E0C",
            BgOverlay = "#CC1A1210",
            AccentColor = "#FF6B35",
            AccentHover = "#FF8C5A",
            TextPrimary = "#FFF5F0",
            TextSecondary = "#D4B5A5",
            TextMuted = "#8B7265",
            TextDark = "#1A1210"
        },

        // ═══ 6. DRACULA ═══
        new ThemeSettings
        {
            Name = "Dracula",
            IsBuiltIn = true,
            BgPrimary = "#282a36",
            BgSecondary = "#21222c",
            BgElevated = "#343746",
            BgHighlight = "#44475a",
            BgHover = "#4d5066",
            BgSkeleton = "#343746",
            BgSkeletonDeep = "#1e1f29",
            BgOverlay = "#CC282a36",
            AccentColor = "#bd93f9",
            AccentHover = "#d4b8ff",
            TextPrimary = "#f8f8f2",
            TextSecondary = "#bfbfbf",
            TextMuted = "#6272a4",
            TextDark = "#282a36"
        }
    ];

    // PRIVATE HELPERS

    private ThemeSettings LoadThemeFromDisk()
    {
        try
        {
            if (File.Exists(G.File.Theme))
            {
                var json = File.ReadAllText(G.File.Theme);
                var theme = JsonSerializer.Deserialize<ThemeSettings>(json, G.Json.Beautiful);
                if (theme != null)
                {
                    _cachedTheme = theme;
                    return theme;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load theme: {ex.Message}");
        }

        // Сохраняем дефолтную при первом запуске
        var def = GetDefaultTheme();
        SaveTheme(def);
        return def;
    }

    private static void SetColor(IResourceDictionary resources, string key, string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            Log.Error($"Invalid color: {key}={hex}");
            color = Colors.Magenta; // Яркий цвет для отладки
        }

        resources[key] = color;
        resources[$"{key}Brush"] = new SolidColorBrush(color);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            color = Color.Parse(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

Core/Services/YoutubeProvider.cs:
```cs
using LMP.Core.Youtube;
using LMP.Core.Youtube.Playlists;
using LMP.Core.Youtube.Search;
using LMP.Core.Youtube.Videos;
using LMP.Core.Youtube.Videos.Streams;
using LMP.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net;
using LMP.Core.Youtube.Channels;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.Core.Services;

public partial class YoutubeProvider : IDisposable
{
    private const int DefaultCacheLifetimeHours = 4;
    private const int MaxCacheSize = 200;

    private YoutubeClient _youtube = null!;

    // Исправление warning CS8618: делаем поле nullable, так как есть конструктор без параметров
    private readonly CookieAuthService? _cookieAuth;

    private readonly LibraryService? _libraryService;
    private readonly Dictionary<string, StreamCacheEntry> _streamCache = [];
    private readonly TimeSpan _streamCacheLifetime = TimeSpan.FromHours(DefaultCacheLifetimeHours);

    private class StreamCacheEntry
    {
        public required string Url { get; init; }
        public long Size { get; init; }
        public int Bitrate { get; init; }
        public required string Codec { get; init; }
        public required string Container { get; init; }
        public DateTime Obtained { get; init; }
    }

    public bool IsReady { get; private set; }

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    private static readonly Regex YoutubeVideoRegex = _YoutubeVideoRegex();
    private static readonly Regex YoutubePlaylistRegex = _YoutubePlaylistRegex();
    private static readonly Regex ValidYoutubeId = _ValidYoutubeId();

    public YoutubeProvider() : this(null!, null!)
    {
    }

    public YoutubeProvider(LibraryService? libraryService, CookieAuthService? cookieAuth)
    {
        _libraryService = libraryService;
        _cookieAuth = cookieAuth;

        if (_cookieAuth != null)
        {
            ReloadClient();
            _cookieAuth.OnAuthStateChanged += ReloadClient;

            // Таймер удален для защиты куки от стирания
        }
    }

    public void ReloadClient()
    {
        if (_cookieAuth == null) return;

        // 1. Получаем сырую строку куки из сервиса авторизации
        string rawCookies = _cookieAuth.GetRawCookies();

        var handler = new HttpClientHandler
        {
            // ВАЖНО: Отключаем автоматическую работу с куки.
            // Теперь HttpClient не будет парсить Set-Cookie и очищать наши данные.
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false
        };

        var baseHttpClient = new HttpClient(handler);

        // 2. Передаем сырую строку в наш обработчик
        var youtubeHandler = new YoutubeHttpHandler(baseHttpClient, rawCookies, disposeClient: true);
        var finalHttpClient = new HttpClient(youtubeHandler, disposeHandler: true);

        _youtube = new YoutubeClient(finalHttpClient);

        Log.Info($"[YouTube] Client reloaded. Auth provided: {!string.IsNullOrEmpty(rawCookies)}");
    }

    public Task InitializeAsync()
    {
        IsReady = true;
        NotifyStatus("[YouTube] Initialized");
        return Task.CompletedTask;
    }

    public YoutubeClient GetClient() => _youtube;

    // --- ПЕРСОНАЛИЗАЦИЯ ---

    public async Task<List<HomeSection>> GetPersonalizedHomeAsync(CancellationToken ct = default)
    {
        if (_cookieAuth?.IsAuthenticated != true) return [];

        try
        {
            var shelves = await _youtube.Music.GetPersonalizedHomeAsync(ct);
            var sections = new List<HomeSection>();

            foreach (var shelf in shelves)
            {
                var section = new HomeSection { Title = shelf.Title };

                foreach (var item in shelf.Items)
                {
                    var thumbUrl = item.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url
                                   ?? $"https://i.ytimg.com/vi/{item.Id}/mqdefault.jpg";

                    var track = new TrackInfo
                    {
                        Id = "yt_" + item.Id,
                        Title = item.Title,
                        Author = item.Author ?? "Unknown",
                        ThumbnailUrl = thumbUrl,
                        Duration = item.Duration ?? TimeSpan.Zero,
                        IsMusic = true,
                        Url = $"https://music.youtube.com/watch?v={item.Id}"
                    };

                    if (item.Type == "Playlist" || item.Type == "Album")
                    {
                        track.Id = "yt_pl_" + item.Id;
                    }

                    section.Tracks.Add(track);
                }

                if (section.Tracks.Count > 0)
                    sections.Add(section);
            }

            return sections;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to get home: {ex.Message}");
            return [];
        }
    }

    // --- ЛАЙКИ И ПЛЕЙЛИСТЫ (WRITE) ---

    public async Task LikeTrackAsync(string trackId, bool like)
    {
        if (_cookieAuth?.IsAuthenticated != true) return;
        try
        {
            var vid = trackId.Replace("yt_", "");
            await _youtube.Music.LikeTrackAsync(vid, like);
            Log.Info($"[Music] Liked status set to {like} for {vid}");
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to like: {ex.Message}");
            throw;
        }
    }

    public async Task<string?> CreatePlaylistAsync(string title)
    {
        if (_cookieAuth?.IsAuthenticated != true) return null;
        try
        {
            var id = await _youtube.Music.CreatePlaylistAsync(title);
            return id;
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    public async Task AddToPlaylistAsync(string playlistId, string trackId)
    {
        if (_cookieAuth?.IsAuthenticated != true) return;
        try
        {
            await _youtube.Music.AddToPlaylistAsync(playlistId, trackId.Replace("yt_", ""));
        }
        catch (Exception ex)
        {
            Log.Error($"[Music] Failed to add to playlist: {ex.Message}");
        }
    }

    #region RefreshStreamUrlAsync
    public async Task<(string Url, long Size, int Bitrate, string Codec, string Container)?> RefreshStreamUrlAsync(
       TrackInfo track,
       bool forceRefresh = false,
       CancellationToken ct = default)
    {
        string? videoId = ExtractVideoIdFromTrack(track);
        if (string.IsNullOrEmpty(videoId))
        {
            NotifyError("[YouTube] Could not extract video ID");
            return null;
        }

        var sw = Stopwatch.StartNew();

        if (forceRefresh)
            NotifyStatus($"[YouTube] [{videoId}] 403 detected. Forcing stream URL refresh...");
        else
            NotifyStatus($"[YouTube] [{videoId}] Getting stream URL...");

        string? targetContainer = track.TransientContainer;
        int targetBitrate = track.TransientBitrate;

        if (string.IsNullOrEmpty(targetContainer))
        {
            if (_libraryService?.Data.RememberTrackFormat == true)
            {
                targetContainer = track.PreferredContainer;
                targetBitrate = track.PreferredBitrate;
            }
        }

        string cacheKey = GenerateCacheKey(videoId, targetContainer, targetBitrate);

        if (!forceRefresh && TryGetFromCache(cacheKey, out var cached))
        {
            track.StreamUrl = cached.Url;
            NotifyStatus($"[YouTube] [{videoId}] Using cached URL ({cached.Codec}/{cached.Bitrate}kbps)");
            return (cached.Url, cached.Size, cached.Bitrate, cached.Codec, cached.Container);
        }

        try
        {
            // VideoId.Parse нужен, так как GetManifestAsync принимает структуру VideoId
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var audioStreams = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .ToList();

            if (audioStreams.Count == 0)
            {
                NotifyError($"[YouTube] [{videoId}] No audio streams found");
                return null;
            }

            AudioOnlyStreamInfo? selectedStream = SelectBestStream(audioStreams, targetContainer, targetBitrate);

            if (selectedStream == null)
            {
                NotifyError($"[YouTube] [{videoId}] Could not select audio stream");
                return null;
            }

            var url = selectedStream.Url;
            var size = selectedStream.Size.Bytes;
            var bitrate = (int)selectedStream.Bitrate.KiloBitsPerSecond;
            var container = selectedStream.Container.Name;
            var codec = DetermineCodec(container, selectedStream);

            sw.Stop();
            NotifyStatus($"[YouTube] [{videoId}] Got stream: {codec}/{bitrate}kbps ({container}) in {sw.ElapsedMilliseconds}ms");

            CacheStreamUrl(cacheKey, url, size, bitrate, codec, container);

            track.StreamUrl = url;
            return (url, size, bitrate, codec, container);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] [{videoId}] Error: {ex.Message}");
            return null;
        }
    }

    private AudioOnlyStreamInfo? SelectBestStream(
        List<AudioOnlyStreamInfo> streams,
        string? preferredContainer,
        int preferredBitrate = 0)
    {
        if (streams.Count == 0) return null;

        if (!string.IsNullOrEmpty(preferredContainer))
        {
            var containerStreams = streams.Where(s =>
                s.Container.Name.Equals(preferredContainer, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (containerStreams.Count > 0)
            {
                if (preferredBitrate > 0)
                {
                    return containerStreams.MinBy(s => Math.Abs(s.Bitrate.KiloBitsPerSecond - preferredBitrate));
                }
                return containerStreams.First();
            }
        }

        var qualityPref = _libraryService?.Data.QualityPreference ?? AudioQualityPreference.BestAvailable;

        return qualityPref switch
        {
            AudioQualityPreference.BestAvailable => streams.FirstOrDefault(),
            AudioQualityPreference.Standard => streams.FirstOrDefault(s => s.Container.Name == "mp4")
                                ?? streams.FirstOrDefault(),
            _ => streams.FirstOrDefault(),
        };
    }

    private static string DetermineCodec(string container, AudioOnlyStreamInfo stream)
    {
        var codecStr = stream.AudioCodec;

        if (!string.IsNullOrEmpty(codecStr))
        {
            if (codecStr.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
            if (codecStr.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (codecStr.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) return "AAC";
            if (codecStr.Contains("vorbis", StringComparison.OrdinalIgnoreCase)) return "Vorbis";
        }

        return container.ToLower() switch
        {
            "webm" => "Opus",
            "mp4" => "AAC",
            "m4a" => "AAC",
            _ => container.ToUpper()
        };
    }

    public async Task<List<StreamOption>> GetStreamOptionsAsync(string videoId)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(videoId)) return [];

        try
        {
            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId);

            return [.. manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .Select(s => new StreamOption
                {
                    Container = s.Container.Name,
                    Bitrate = s.Bitrate.KiloBitsPerSecond,
                    Codec = DetermineCodec(s.Container.Name, s),
                    SizeMb = s.Size.MegaBytes
                })];
        }
        catch (Exception ex)
        {
            Log.Error($"[YouTube] GetStreamOptions error: {ex.Message}");
            return [];
        }
    }
    #endregion

    #region Cache
    private static string GenerateCacheKey(string videoId, string? container, int bitrate = 0)
    {
        var key = string.IsNullOrEmpty(container) ? videoId : $"{videoId}_{container}";
        if (bitrate > 0) key += $"_{bitrate}";
        return key;
    }

    private bool TryGetFromCache(string cacheKey, out StreamCacheEntry result)
    {
        if (_streamCache.TryGetValue(cacheKey, out var cached))
        {
            if (DateTime.UtcNow - cached.Obtained < _streamCacheLifetime)
            {
                result = cached;
                return true;
            }
            _streamCache.Remove(cacheKey);
        }

        result = null!;
        return false;
    }

    private void CacheStreamUrl(string cacheKey, string url, long size, int bitrate, string codec, string container)
    {
        _streamCache[cacheKey] = new StreamCacheEntry
        {
            Url = url,
            Size = size,
            Bitrate = bitrate,
            Codec = codec,
            Container = container,
            Obtained = DateTime.UtcNow
        };

        if (_streamCache.Count > MaxCacheSize) CleanupExpiredCache();
    }

    private void CleanupExpiredCache()
    {
        var expired = _streamCache
            .Where(kv => DateTime.UtcNow - kv.Value.Obtained > _streamCacheLifetime)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired) _streamCache.Remove(key);
    }

    public void ClearCache()
    {
        _streamCache.Clear();
        Log.Info("[YouTube] Stream cache cleared");
    }
    #endregion

    #region Search, Playlist, etc.
    public static QueryType DetectQueryType(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return QueryType.None;
        query = query.Trim();

        if (YoutubePlaylistRegex.IsMatch(query)) return QueryType.Playlist;
        if (YoutubeVideoRegex.IsMatch(query) ||
            query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return QueryType.DirectUrl;
        }

        return QueryType.Search;
    }

    public static string? ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YoutubeVideoRegex.Match(url);
        if (match.Success) return match.Groups[1].Value;
        try { return VideoId.TryParse(url)?.Value; } catch { return null; }
    }

    private static string? ExtractVideoIdFromTrack(TrackInfo track)
    {
        string cleanId = track.Id?.Trim() ?? "";
        if (cleanId.StartsWith("yt_"))
        {
            var rawId = cleanId[3..];
            var safeId = new string([.. rawId.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);
            if (ValidYoutubeId.IsMatch(safeId)) return safeId;
        }
        if (!string.IsNullOrWhiteSpace(track.Url)) return ExtractVideoId(track.Url);
        return null;
    }

    public async Task<TrackInfo?> GetTrackByUrlAsync(string url)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var videoId = VideoId.TryParse(url) ?? VideoId.Parse(ExtractVideoId(url) ?? "");
            // VideoClient теперь возвращает TrackInfo
            var track = await _youtube.Videos.GetAsync(videoId);
            return track;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetTrackByUrlAsync error: {ex.Message}");
            return null;
        }
    }

    #region Search
    public async IAsyncEnumerable<List<TrackInfo>> SearchStreamingAsync(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) yield break;

        var sw = Stopwatch.StartNew();
        int count = 0;

        NotifyStatus($"[YouTube] Starting streaming search for '{query}' (Filter: {filter})...");

        // SearchClient возвращает Batch<ISearchResult>
        await foreach (var batch in _youtube.Search.GetResultBatchesAsync(query, filter, ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var tracks = new List<TrackInfo>();

            foreach (var result in batch.Items)
            {
                if (count >= maxResults) break;

                // TrackInfo реализует ISearchResult, поэтому просто кастим
                if (result is TrackInfo track)
                {
                    tracks.Add(track);
                    count++;
                }
                // Плейлисты (Playlist) тоже реализуют ISearchResult, но для списка треков они не подходят
                // Если нужны и плейлисты, их можно обрабатывать отдельно, но здесь возвращаем List<TrackInfo>
            }

            if (tracks.Count > 0)
            {
                NotifyStatus($"[YouTube] Got batch: +{tracks.Count} items (total: {count}) in {sw.ElapsedMilliseconds}ms");
                yield return tracks;
            }

            if (count >= maxResults) break;
        }

        sw.Stop();
        NotifyStatus($"[YouTube] Search complete: {count} results in {sw.ElapsedMilliseconds}ms");
    }

    public async Task<List<TrackInfo>> SearchFastAsync(
        string query,
        int maxResults = 100,
        SearchFilter filter = SearchFilter.Video,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query)) return [];

        var sw = Stopwatch.StartNew();
        var results = new List<TrackInfo>(maxResults);

        try
        {
            // Используем специализированный метод для получения треков
            var enumerable = _youtube.Search.GetVideosAsync(query, ct);

            await foreach (var track in enumerable)
            {
                if (results.Count >= maxResults) break;
                results.Add(track);
            }

            sw.Stop();
            NotifyStatus($"[YouTube] Fast search '{query}' (Filter: {filter}): {results.Count} results in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            NotifyStatus($"[YouTube] Search cancelled after {results.Count} results");
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] SearchFastAsync error: {ex.Message}");
        }

        return results;
    }

    public async Task<List<TrackInfo>> SearchAsync(string query, int maxResults = 100)
    {
        return await SearchFastAsync(query, maxResults, SearchFilter.Video);
    }

    public class SearchSession : IDisposable
    {
        private readonly YoutubeClient _youtube;
        private readonly string _query;
        private readonly int _maxResults;
        private readonly SearchFilter _filter; // Храним фильтр
        private readonly HashSet<string> _seenIds = [];
        private IAsyncEnumerator<Batch<ISearchResult>>? _enumerator;
        private bool _hasMore = true;
        private bool _disposed;
        private readonly List<TrackInfo> _buffer = [];

        public bool HasMore => (_hasMore || _buffer.Count > 0) && !_disposed && _seenIds.Count < _maxResults;
        public int LoadedCount => _seenIds.Count;

        // Конструктор обновлен для приема SearchFilter
        internal SearchSession(
            YoutubeClient youtube,
            string query,
            int maxResults = 300,
            SearchFilter filter = SearchFilter.Video,
            IEnumerable<string>? skipTrackIds = null)
        {
            _youtube = youtube;
            _query = query;
            _maxResults = maxResults;
            _filter = filter;
            _seenIds = [];

            if (skipTrackIds != null)
            {
                foreach (var id in skipTrackIds)
                {
                    var cleanId = id.StartsWith("yt_") ? id[3..] : id;
                    _seenIds.Add(cleanId);
                }
            }
        }

        public async Task<List<TrackInfo>> FetchNextBatchAsync(int count = 50, CancellationToken ct = default)
        {
            if (_disposed || _seenIds.Count >= _maxResults) return [];

            var results = new List<TrackInfo>();

            while (results.Count < count && _buffer.Count > 0)
            {
                results.Add(_buffer[0]);
                _buffer.RemoveAt(0);
            }

            while (results.Count < count && _hasMore && _seenIds.Count < _maxResults)
            {
                try
                {
                    // Используем _filter при создании перечислителя
                    _enumerator ??= _youtube.Search
                        .GetResultBatchesAsync(_query, _filter, ct)
                        .GetAsyncEnumerator(ct);

                    if (!await _enumerator.MoveNextAsync())
                    {
                        _hasMore = false;
                        break;
                    }

                    var batch = _enumerator.Current;

                    foreach (var item in batch.Items)
                    {
                        if (_seenIds.Count >= _maxResults) break;

                        string? id = null;
                        TrackInfo? track = null;

                        // Логика обработки разных типов результатов
                        if (item is VideoSearchResult video)
                        {
                            id = video.Id.Value;
                            if (_seenIds.Add(id))
                                track = ConvertSearchResultToTrackInfo(video);
                        }
                        else if (item is PlaylistSearchResult playlist)
                        {
                            id = playlist.Id.Value;
                            if (_seenIds.Add(id))
                                track = ConvertPlaylistSearchResultToTrackInfo(playlist);
                        }

                        if (track != null)
                        {
                            if (results.Count < count)
                                results.Add(track);
                            else
                                _buffer.Add(track);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error($"[SearchSession] Error: {ex.Message}");
                    _hasMore = false;
                    break;
                }
            }

            return results;
        }

        // ... (Dispose метод остается прежним)
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hasMore = false;
            _buffer.Clear();

            if (_enumerator != null)
            {
                _ = _enumerator.DisposeAsync().AsTask();
            }
        }
    }

    private SearchSession? _currentSearchSession;

    public SearchSession CreateSearchSession(
        string query,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video,
        IEnumerable<string>? skipTrackIds = null)
    {
        _currentSearchSession?.Dispose();
        _currentSearchSession = new SearchSession(_youtube, query, maxResults, filter, skipTrackIds);

        var skipCount = skipTrackIds?.Count() ?? 0;
        NotifyStatus($"[YouTube] Created search session for '{query}' (max: {maxResults}, filter: {filter})");

        return _currentSearchSession;
    }

    public async Task<(List<TrackInfo> Tracks, SearchSession Session)> SearchWithSessionAsync(
        string query,
        int initialCount = 50,
        int maxResults = 300,
        SearchFilter filter = SearchFilter.Video, // Добавлен аргумент
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(query))
            return ([], null!);

        var sw = Stopwatch.StartNew();
        var session = CreateSearchSession(query, maxResults, filter);
        var tracks = await session.FetchNextBatchAsync(initialCount, ct);

        sw.Stop();
        NotifyStatus($"[YouTube] Initial search '{query}': {tracks.Count} results in {sw.ElapsedMilliseconds}ms (Filter: {filter})");

        return (tracks, session);
    }
    #endregion

    public async Task<(string Name, List<TrackInfo> Tracks)?> GetPlaylistAsync(string url)
    {
        if (!IsReady) return null;
        try
        {
            var playlistId = PlaylistId.TryParse(url);
            if (playlistId == null) return null;

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            // GetVideosAsync возвращает IAsyncEnumerable<TrackInfo>
            var tracks = await _youtube.Playlists.GetVideosAsync(playlistId.Value).CollectAsync();

            NotifyStatus($"[YouTube] Playlist '{playlist.Name}': {tracks.Count} tracks");
            return (playlist.Name, tracks.ToList());
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] GetPlaylistAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<(string ChannelName, List<PlaylistSearchResult> Playlists)?> GetChannelPlaylistsForSyncAsync(string channelUrl, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(channelUrl, ct);
        if (channel is null) return null;

        NotifyStatus($"[YouTube] Fetching playlists from channel: {channel.Title}...");

        try
        {
            var results = new List<PlaylistSearchResult>();

            // GetPlaylistsAsync возвращает IAsyncEnumerable<Playlist>
            await foreach (var pl in _youtube.Channels.GetPlaylistsAsync(channel.Id, ct))
            {
                if (pl.Name.Equals("Uploads", StringComparison.OrdinalIgnoreCase)) continue;

                // Маппим наш LMP.Core.Models.Playlist в PlaylistSearchResult (если он еще нужен)
                // Или меняем сигнатуру метода на возврат List<Playlist>
                // Для совместимости создадим PlaylistSearchResult вручную
                var thumbs = new List<Thumbnail>();
                if (!string.IsNullOrEmpty(pl.ThumbnailUrl)) thumbs.Add(new Thumbnail(pl.ThumbnailUrl, new Resolution(0, 0)));

                var auth = pl.Author != null ? new Author(new ChannelId(channel.Id.Value), pl.Author) : null;

                results.Add(new PlaylistSearchResult(
                   new PlaylistId(pl.YoutubeId ?? ""),
                   pl.Name,
                   auth,
                   thumbs
               ));
            }

            NotifyStatus($"[YouTube] Found {results.Count} playlists.");
            return (channel.Title, results);
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error parsing channel playlists: {ex.Message}");
            return (channel.Title, []);
        }
    }

    public static async Task<List<Playlist>> GetUserPlaylistsByAuthAsync()
    {
        var userDataService = Program.Services.GetRequiredService<YoutubeUserDataService>();
        return await userDataService.GetMyPlaylistsAsync();
    }

    public async Task<Playlist?> ImportPlaylistAsync(string playlistId, bool isAccountSync = false, CancellationToken ct = default)
    {
        try
        {
            // PlaylistClient.GetAsync возвращает Playlist
            var plId = new PlaylistId(playlistId);
            var playlist = await _youtube.Playlists.GetAsync(plId, ct);

            // Настраиваем режим
            playlist.SyncMode = isAccountSync ? PlaylistSyncMode.TwoWaySync : PlaylistSyncMode.CloudPublic;

            // Загружаем треки
            var tracks = await _youtube.Playlists.GetVideosAsync(plId, ct).CollectAsync();

            foreach (var track in tracks)
            {
                _libraryService?.AddOrUpdateTrack(track);
                playlist.TrackIds.Add(track.Id);
            }
            return playlist;
        }
        catch (Exception ex)
        {
            NotifyError($"Error importing playlist {playlistId}: {ex.Message}");
            return null;
        }
    }

    public async Task<(string Name, string AvatarUrl)?> GetChannelInfoAsync(string url, CancellationToken ct = default)
    {
        var channel = await GetChannelFromUrlAsync(url, ct);
        if (channel == null) return null;
        return (channel.Title, channel.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault()?.Url ?? "");
    }

    private async Task<Channel?> GetChannelFromUrlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (url.Contains("/channel/"))
            {
                var id = url.Split("/channel/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetAsync(new ChannelId(id), ct);
            }
            if (url.Contains("/@"))
            {
                var handle = url.Split("/@")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByHandleAsync(new ChannelHandle(handle), ct);
            }
            if (url.Contains("/c/"))
            {
                var slug = url.Split("/c/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetBySlugAsync(new ChannelSlug(slug), ct);
            }
            if (url.Contains("/user/"))
            {
                var user = url.Split("/user/")[1].Split('/')[0].Split('?')[0];
                return await _youtube.Channels.GetByUserAsync(new UserName(user), ct);
            }

            NotifyError("[YouTube] Could not recognize channel URL format.");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Error getting channel info: {ex.Message}");
            return null;
        }
    }

    public async Task<List<TrackInfo>> GetRadioAsync(TrackInfo sourceTrack, int count = 25)
    {
        if (!IsReady || string.IsNullOrEmpty(sourceTrack.Url)) return [];
        try
        {
            var videoId = ExtractVideoId(sourceTrack.Url);
            if (string.IsNullOrEmpty(videoId)) return [];
            var mixUrl = $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}";

            // GetPlaylistAsync возвращает (Name, List<TrackInfo>)
            var result = await GetPlaylistAsync(mixUrl);
            if (result == null) return [];

            var tracks = result.Value.Tracks.Take(count).ToList();
            foreach (var t in tracks) t.RadioSeedId = sourceTrack.Id;
            return tracks;
        }
        catch { return []; }
    }

    public async Task<List<TrackInfo>> GetTrendingAsync(int count = 20)
    {
        try
        {
            var url = "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs";
            var result = await GetPlaylistAsync(url);
            return result?.Tracks.Take(count).ToList() ?? await SearchAsync("top music 2024", count);
        }
        catch
        {
            return await SearchAsync("top music 2024", count);
        }
    }

    public async Task<string?> DownloadTrackAsync(
        TrackInfo track,
        IProgress<float>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsReady || string.IsNullOrEmpty(track.Url)) return null;
        try
        {
            var videoId = ExtractVideoId(track.Url);
            if (string.IsNullOrEmpty(videoId)) return null;

            var vId = VideoId.Parse(videoId);
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(vId, ct);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            if (stream == null) return null;

            var fileName = SanitizeFileName($"{track.Author} - {track.Title}.{stream.Container.Name}");
            var filePath = Path.Combine(G.Folder.Downloads, fileName);

            var prog = progress != null ? new Progress<double>(p => progress.Report((float)p)) : null;

            await _youtube.Videos.Streams.DownloadAsync(stream, filePath, progress: prog, cancellationToken: ct);
            NotifyStatus($"[YouTube] Downloaded: {fileName}");
            return filePath;
        }
        catch (Exception ex)
        {
            NotifyError($"[YouTube] Download error: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Helpers
    private static TrackInfo ConvertPlaylistSearchResultToTrackInfo(PlaylistSearchResult playlist)
    {
        var thumb = playlist.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault();
        return new TrackInfo
        {
            // Используем префикс yt_pl_ чтобы отличить плейлист от трека, если UI поддерживает это
            // Или можно использовать просто yt_, но тогда при попытке воспроизвести как трек будет ошибка
            Id = $"yt_pl_{playlist.Id.Value}",
            Title = playlist.Title,
            Author = playlist.Author?.ChannelTitle ?? "Unknown",
            Url = playlist.Url,
            Duration = TimeSpan.Zero, // У плейлистов нет длительности в поиске
            ThumbnailUrl = thumb?.Url ?? "",
            IsMusic = false // Плейлист сам по себе не музыкальный файл
        };
    }

    private static TrackInfo ConvertToTrackInfo(Video video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private static TrackInfo ConvertSearchResultToTrackInfo(VideoSearchResult video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? "",
            IsOfficialArtist = video.IsOfficialArtist,
            IsMusic = video.IsMusic
        };
    }

    private static TrackInfo ConvertPlaylistVideoToTrackInfo(PlaylistVideo video)
    {
        var thumb = video.Thumbnails.OrderByDescending(t => t.Resolution.Width).Skip(1).FirstOrDefault();
        return new TrackInfo
        {
            Id = $"yt_{video.Id.Value}",
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Url = video.Url,
            Duration = video.Duration ?? TimeSpan.Zero,
            ThumbnailUrl = thumb?.Url ?? ""
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string([.. name.Where(c => !invalid.Contains(c))]);
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    private void NotifyStatus(string message)
    {
        Log.Info(message);
        OnStatusChanged?.Invoke(message);
    }

    private void NotifyError(string message)
    {
        Log.Error(message);
        OnError?.Invoke(message);
    }

    public void Dispose()
    {
        if (_cookieAuth != null)
        {
            _cookieAuth.OnAuthStateChanged -= ReloadClient;
        }

        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(
        @"(?:youtube\.com\/(?:watch\?v=|embed\/|v\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubeVideoRegex();

    [GeneratedRegex(
        @"(?:youtube\.com\/.*[?&]list=)([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "ru-RU")]
    private static partial Regex _YoutubePlaylistRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex _ValidYoutubeId();
    #endregion
}

/// <summary>
/// Информация о доступном аудио потоке
/// </summary>
public class StreamOption
{
    /// <summary>Контейнер (webm/mp4)</summary>
    public string Container { get; set; } = "";

    /// <summary>Битрейт в kbps</summary>
    public double Bitrate { get; set; }

    /// <summary>Кодек (Opus/AAC)</summary>
    public string Codec { get; set; } = "";

    /// <summary>Размер в мегабайтах</summary>
    public double SizeMb { get; set; }

    /// <summary>Отображаемое имя для UI</summary>
    public string DisplayName => $"{Codec} {Bitrate:F0}kbps ({Container})";
}

public class HomeSection
{
    public string Title { get; set; } = "";
    public List<TrackInfo> Tracks { get; set; } = [];
}
```

Program.cs:
```cs

using LMP.Core.Services;
using LMP.Features.Home;
using LMP.Features.Library;
using LMP.Features.Player;
using LMP.Features.Playlist;
using LMP.Features.Search;
using LMP.Features.Settings;
using LMP.Features.Shell;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;

namespace LMP;

class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        try
        {
            Console.WriteLine("Logger initializing...");
            Log.Initialize();

            Log.Info($"{G.AppId} starting...!");

            G.Folder.Create();

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal($"Global crash: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI();

    private static void ConfigureServices(IServiceCollection services)
    {
        Log.Info("Configuring services...");

        // --- Core Services ---
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThemeManagerService>();
        services.AddSingleton<CookieAuthService>();
        services.AddSingleton<YoutubeProvider>();
        services.AddSingleton<YoutubeUserDataService>();
        services.AddSingleton<MusicLibraryManager>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        // --- Caching ---
        services.AddSingleton<StreamCacheManager>();
        services.AddSingleton<SearchCacheService>();
        services.AddSingleton<ImageCacheService>();
        services.AddSingleton<MemoryMonitor>();

        // --- Audio & Downloads ---
        services.AddSingleton<AudioEngine>();
        services.AddSingleton<DownloadService>();

        // --- ViewModels ---
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<QueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<PlaylistViewModel>();
        services.AddTransient<MergeConflictViewModel>();
        services.AddTransient<SyncSelectionViewModel>();
        services.AddSingleton<TrackViewModelFactory>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<PlayerBarViewModel>();

        Log.Info("Services registered successfully.");
    }
}



```

App.axaml.cs:
```cs
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using LMP.Features.Shell;
using LMP.Core.Services;
using AsyncImageLoader;

namespace LMP;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("Framework initialization completed.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 0. Load theme BEFORE any UI is created
            var themeManager = Program.Services.GetRequiredService<ThemeManagerService>();
            themeManager.LoadAndApplyThemeOnStartup();

            // 1. Initialize localization
            var library = Program.Services.GetRequiredService<LibraryService>();
            LocalizationService.Instance.Initialize(library.Data.LanguageCode);

            // 2. Create UI
            var mainWindowVM = Program.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowVM
            };
            Log.Info("Main window created and shown.");

            var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
            ImageLoader.AsyncImageLoader = new CachedImageLoader(imageCache);

            // 3. Background initialization tasks
            _ = Task.Run(static async () =>
            {
                try
                {
                    var youtube = Program.Services.GetRequiredService<YoutubeProvider>();
                    await youtube.InitializeAsync();
                    var musicLibraryManager = Program.Services.GetRequiredService<MusicLibraryManager>();
                    await musicLibraryManager.SyncLikedTracksAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"Background initialization failed: {ex.Message}");
                }
            });

#if DEBUG
            desktop.MainWindow.AttachDevTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

Features/Settings/SettingsViewModel.cs:
```cs
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Features.Settings;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly StreamCacheManager _streamCache;
    private readonly ThemeManagerService _themeManager;
    private readonly CookieAuthService _auth; // Changed
    private readonly IDialogService _dialog;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    private bool _isDisposed;
    private bool _isLoadingTheme;

    [Reactive] public bool IsAuthenticated { get; private set; }
    [Reactive] public string FakeChannelInput { get; set; } = string.Empty;
    [Reactive] public bool IsLoadingFakeAccount { get; set; }

    public bool HasAccount => IsAuthenticated || _library.HasFakeAccount;
    public bool IsFakeAccount => !IsAuthenticated && _library.HasFakeAccount;

    public string AccountName => IsAuthenticated
        ? "YouTube Music User" // Cookie Auth не отдает имя сразу, можно вытащить позже
        : _library.FakeAccountName ?? SL["Auth_NotSignedIn"];

    public string? AccountAvatarUrl => IsAuthenticated
        ? null
        : _library.FakeAccountAvatarUrl;

    public string AccountSubtitle => IsAuthenticated
        ? "Authorized via Cookies"
        : IsFakeAccount ? SL["Account_LimitedAccess"] : SL["Auth_Guest"];

    public List<InternetProfile> InternetProfileOptions { get; } = [.. Enum.GetValues<InternetProfile>()];
    [Reactive] public InternetProfile SelectedInternetProfile { get; set; }
    [Reactive] public bool ProxyEnabled { get; set; }
    [Reactive] public string ProxyHost { get; set; } = "";
    [Reactive] public int ProxyPort { get; set; } = 8080;
    [Reactive] public bool ProxyAuth { get; set; }
    [Reactive] public string ProxyUser { get; set; } = "";
    [Reactive] public string ProxyPass { get; set; } = "";
    [Reactive] public bool NetworkRestartRequired { get; set; }

    [Reactive] public string DownloadPath { get; set; } = string.Empty;
    [Reactive] public int ImageCacheLimitMb { get; set; }
    [Reactive] public int AudioCacheLimitMb { get; set; }
    [Reactive] public string ImageCacheStats { get; private set; } = "...";
    [Reactive] public string AudioCacheStats { get; private set; } = "...";
    [Reactive] public double ImageCacheUsagePercent { get; private set; }
    [Reactive] public double AudioCacheUsagePercent { get; private set; }

    public ObservableCollection<ThemeSettings> ThemePresets { get; } = [];
    [Reactive] public ThemeSettings? SelectedPreset { get; set; }
    [Reactive] public Color AccentColor { get; set; }
    [Reactive] public Color BgPrimaryColor { get; set; }
    [Reactive] public Color BgSecondaryColor { get; set; }
    [Reactive] public Color BgElevatedColor { get; set; }
    [Reactive] public Color TextPrimaryColor { get; set; }
    [Reactive] public Color TextSecondaryColor { get; set; }
    [Reactive] public bool HasUnsavedThemeChanges { get; set; }

    public List<AudioQualityPreference> QualityOptions { get; } = [.. Enum.GetValues<AudioQualityPreference>()];
    [Reactive] public int MaxVolumeLimit { get; set; }
    [Reactive] public float TargetGainDb { get; set; }
    [Reactive] public AudioQualityPreference QualityPreference { get; set; }
    [Reactive] public bool RememberTrackFormat { get; set; }

    [Reactive] public bool DiscordRpcEnabled { get; set; }
    [Reactive] public bool AutoPlayOnPaste { get; set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }
    [Reactive] public int SearchBatchSize { get; set; }
    [Reactive] public bool EnableSearchCache { get; set; }
    [Reactive] public int SearchCacheTtlMinutes { get; set; }

    public static List<LanguageItem> Languages => LocalizationService.Instance.AvailableLanguages;
    [Reactive] public LanguageItem? SelectedLanguage { get; set; }

    public ReactiveCommand<Unit, Unit> BrowseDownloadPathCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SetFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFakeAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearImageCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudioCacheCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetThemeCommand { get; }

    public SettingsViewModel(
        LibraryService library,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        StreamCacheManager streamCache,
        ThemeManagerService themeManager,
        CookieAuthService auth, // Changed
        IDialogService dialog,
        AudioEngine audio,
        YoutubeProvider youtube)
    {
        _library = library;
        _searchCache = searchCache;
        _imageCache = imageCache;
        _streamCache = streamCache;
        _themeManager = themeManager;
        _auth = auth;
        _dialog = dialog;
        _audio = audio;
        _youtube = youtube;

        foreach (var preset in ThemeManagerService.GetBuiltInPresets())
            ThemePresets.Add(preset);

        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync);
        LogoutCommand = ReactiveCommand.CreateFromTask(LogoutAsync);
        SetFakeAccountCommand = ReactiveCommand.CreateFromTask(SetFakeAccountAsync);
        ClearFakeAccountCommand = ReactiveCommand.Create(ClearFakeAccount);
        BrowseDownloadPathCommand = ReactiveCommand.CreateFromTask(BrowseDownloadPathAsync);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        ResetLibraryCommand = ReactiveCommand.CreateFromTask(ResetLibraryAsync);
        ClearImageCacheCommand = ReactiveCommand.CreateFromTask(ClearImageCacheAsync);
        ClearAudioCacheCommand = ReactiveCommand.CreateFromTask(ClearAudioCacheAsync);
        ApplyThemeCommand = ReactiveCommand.Create(ApplyTheme);
        ResetThemeCommand = ReactiveCommand.Create(ResetTheme);

        LoadAllSettings();
        UpdateCacheStats();
        SetupSubscriptions();
    }

    private void SetupSubscriptions()
    {
        this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1).WhereNotNull()
            .Subscribe(lang =>
            {
                LocalizationService.Instance.CurrentLanguage = lang.Code;
                _library.Data.LanguageCode = lang.Code;
                _library.Save();
            });

        this.WhenAnyValue(
                x => x.SelectedInternetProfile, x => x.ProxyEnabled,
                x => x.ProxyHost, x => x.ProxyPort,
                x => x.ProxyAuth, x => x.ProxyUser, x => x.ProxyPass)
            .Skip(1)
            .Subscribe(_ =>
            {
                NetworkRestartRequired = true;
                SaveNetworkSettings();
            });

        this.WhenAnyValue(x => x.ImageCacheLimitMb, x => x.AudioCacheLimitMb)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(_ => SaveStorageSettings());

        this.WhenAnyValue(
                x => x.AccentColor, x => x.BgPrimaryColor, x => x.BgSecondaryColor,
                x => x.BgElevatedColor, x => x.TextPrimaryColor, x => x.TextSecondaryColor)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (!_isLoadingTheme)
                    HasUnsavedThemeChanges = true;
            });

        this.WhenAnyValue(x => x.SelectedPreset)
            .Skip(1).WhereNotNull()
            .Subscribe(ApplyPresetToColorPickers);

        this.WhenAnyValue(x => x.MaxVolumeLimit).Skip(1).Subscribe(v =>
        {
            _library.Data.MaxVolumeLimit = v;
            _library.Save();
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.TargetGainDb).Skip(1).Subscribe(v =>
        {
            _library.Data.TargetGainDb = v;
            _library.Save();
            _audio.UpdateAudioSettings();
        });

        this.WhenAnyValue(x => x.QualityPreference).Skip(1).Subscribe(v =>
        {
            _library.Data.QualityPreference = v;
            _library.Save();
            _youtube.ClearCache();
        });

        this.WhenAnyValue(x => x.DiscordRpcEnabled).Skip(1).Subscribe(v =>
        {
            _library.Data.DiscordRpcEnabled = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.AutoPlayOnPaste).Skip(1).Subscribe(v =>
        {
            _library.Data.AutoPlayOnUrlPaste = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.EnableSmoothLoading).Skip(1).Subscribe(v =>
        {
            _library.Data.EnableSmoothLoading = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.RememberTrackFormat).Skip(1).Subscribe(v =>
        {
            _library.Data.RememberTrackFormat = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.SearchBatchSize).Skip(1).Subscribe(v =>
        {
            _library.Data.SearchBatchSize = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.EnableSearchCache).Skip(1).Subscribe(v =>
        {
            _library.Data.EnableSearchCache = v;
            _library.Save();
        });

        this.WhenAnyValue(x => x.SearchCacheTtlMinutes).Skip(1).Subscribe(v =>
        {
            _library.Data.SearchCacheTtlMinutes = v;
            _library.Save();
            _ = _searchCache.CleanupExpiredAsync();
        });
    }

    private void LoadAllSettings()
    {
        DownloadPath = _library.DownloadPath;
        DiscordRpcEnabled = _library.Data.DiscordRpcEnabled;
        AutoPlayOnPaste = _library.Data.AutoPlayOnUrlPaste;
        SearchBatchSize = _library.Data.SearchBatchSize;
        EnableSmoothLoading = _library.Data.EnableSmoothLoading;
        MaxVolumeLimit = _library.Data.MaxVolumeLimit;
        TargetGainDb = _library.Data.TargetGainDb;
        QualityPreference = _library.Data.QualityPreference;
        RememberTrackFormat = _library.Data.RememberTrackFormat;
        EnableSearchCache = _library.Data.EnableSearchCache;
        SearchCacheTtlMinutes = _library.Data.SearchCacheTtlMinutes;
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == _library.Data.LanguageCode) ?? Languages[0];

        FakeChannelInput = _library.FakeAccountUrl ?? "";
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();

        SelectedInternetProfile = _library.Data.InternetProfile;
        ProxyEnabled = _library.Data.Proxy.Enabled;
        ProxyHost = _library.Data.Proxy.Host;
        ProxyPort = _library.Data.Proxy.Port;
        ProxyAuth = _library.Data.Proxy.UseAuth;
        ProxyUser = _library.Data.Proxy.Username;
        ProxyPass = _library.Data.Proxy.Password;

        ImageCacheLimitMb = _library.Data.Storage.ImageCacheLimitMb;
        AudioCacheLimitMb = _library.Data.Storage.AudioCacheLimitMb;

        LoadThemeColors();
    }

    private void LoadThemeColors()
    {
        var theme = _themeManager.GetCurrentTheme();
        ApplyThemeToColorPickers(theme);
        HasUnsavedThemeChanges = false;
    }

    private void ApplyPresetToColorPickers(ThemeSettings preset)
    {
        ApplyThemeToColorPickers(preset);
        HasUnsavedThemeChanges = true;
    }

    private void ApplyThemeToColorPickers(ThemeSettings theme)
    {
        _isLoadingTheme = true;
        try
        {
            AccentColor = ParseColorSafe(theme.AccentColor);
            BgPrimaryColor = ParseColorSafe(theme.BgPrimary);
            BgSecondaryColor = ParseColorSafe(theme.BgSecondary);
            BgElevatedColor = ParseColorSafe(theme.BgElevated);
            TextPrimaryColor = ParseColorSafe(theme.TextPrimary);
            TextSecondaryColor = ParseColorSafe(theme.TextSecondary);
        }
        finally
        {
            _isLoadingTheme = false;
        }
    }

    private void ApplyTheme()
    {
        static string GetRgbHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var theme = new ThemeSettings
        {
            Name = SelectedPreset?.Name ?? SL["Theme_Custom"],
            AccentColor = AccentColor.ToString(),
            AccentHover = LightenColor(AccentColor, 0.15).ToString(),
            BgPrimary = BgPrimaryColor.ToString(),
            BgSecondary = BgSecondaryColor.ToString(),
            BgElevated = BgElevatedColor.ToString(),
            BgHighlight = LightenColor(BgSecondaryColor, 0.1).ToString(),
            BgHover = LightenColor(BgSecondaryColor, 0.2).ToString(),
            BgSkeleton = LightenColor(BgSecondaryColor, 0.05).ToString(),
            BgSkeletonDeep = DarkenColor(BgSecondaryColor, 0.2).ToString(),
            BgOverlay = $"#CC{GetRgbHex(BgPrimaryColor)}",
            TextPrimary = TextPrimaryColor.ToString(),
            TextSecondary = TextSecondaryColor.ToString(),
            TextMuted = DarkenColor(TextSecondaryColor, 0.3).ToString(),
            TextDark = BgPrimaryColor.ToString()
        };

        _themeManager.SaveTheme(theme);
        _themeManager.ApplyTheme(theme);
        HasUnsavedThemeChanges = false;
    }

    private void ResetTheme()
    {
        _themeManager.ResetToDefault();
        LoadThemeColors();
        SelectedPreset = ThemePresets.FirstOrDefault();
    }

    private static Color ParseColorSafe(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Magenta; }
    }

    private static Color LightenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * factor),
            (byte)Math.Min(255, c.G + (255 - c.G) * factor),
            (byte)Math.Min(255, c.B + (255 - c.B) * factor));
    }

    private static Color DarkenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - factor)),
            (byte)(c.G * (1 - factor)),
            (byte)(c.B * (1 - factor)));
    }

    private void SaveNetworkSettings()
    {
        _library.Data.InternetProfile = SelectedInternetProfile;
        _library.Data.Proxy.Enabled = ProxyEnabled;
        _library.Data.Proxy.Host = ProxyHost;
        _library.Data.Proxy.Port = ProxyPort;
        _library.Data.Proxy.UseAuth = ProxyAuth;
        _library.Data.Proxy.Username = ProxyUser;
        _library.Data.Proxy.Password = ProxyPass;
        _library.Save();
    }

    private void SaveStorageSettings()
    {
        _library.Data.Storage.ImageCacheLimitMb = ImageCacheLimitMb;
        _library.Data.Storage.AudioCacheLimitMb = AudioCacheLimitMb;
        _library.Save();
        UpdateCacheStats();
    }

    private async Task ClearImageCacheAsync()
    {
        await _imageCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private async Task ClearAudioCacheAsync()
    {
        await _streamCache.ClearAllAsync();
        UpdateCacheStats();
    }

    private void UpdateCacheStats()
    {
        var (imgCount, imgSize) = _imageCache.GetStats();
        var audioStats = StreamCacheManager.GetStats();

        ImageCacheStats = $"{imgSize} MB / {ImageCacheLimitMb} MB ({imgCount} {SL["Common_Files"]})";
        AudioCacheStats = $"{audioStats.SizeMb} MB / {AudioCacheLimitMb} MB ({audioStats.FileCount} {SL["Common_Files"]})";

        ImageCacheUsagePercent = ImageCacheLimitMb > 0
            ? Math.Clamp((double)imgSize / ImageCacheLimitMb, 0, 1) : 0;
        AudioCacheUsagePercent = AudioCacheLimitMb > 0
            ? Math.Clamp((double)audioStats.SizeMb / AudioCacheLimitMb, 0, 1) : 0;
    }

    private async Task SetFakeAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(FakeChannelInput)) return;
        IsLoadingFakeAccount = true;
        try
        {
            var info = await _youtube.GetChannelInfoAsync(FakeChannelInput);
            if (info != null)
            {
                _library.SetFakeAccount(FakeChannelInput, info.Value.Name, info.Value.AvatarUrl);
                RaiseAccountProperties();
                await _dialog.ShowInfoAsync(SL["Dialog_Success"],
                    string.Format(SL["Dialog_Merge_Success"], info.Value.Name));
            }
            else
            {
                await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], SL["Dialog_Merge_Error"]);
            }
        }
        catch (Exception ex)
        {
            await _dialog.ShowInfoAsync(SL["Dialog_Error_Title"], ex.Message);
        }
        finally
        {
            IsLoadingFakeAccount = false;
        }
    }

    private void ClearFakeAccount()
    {
        _library.ClearFakeAccount();
        FakeChannelInput = "";
        RaiseAccountProperties();
    }

    // --- REPLACED LOGIN LOGIC ---
    private async Task LoginAsync()
    {
        // Используем новый метод IDialogService.ShowInputAsync
        // Примечание: для перевода строк можно использовать Environment.NewLine в prompt
        var cookies = await _dialog.ShowInputAsync("Login",
            "1. Open music.youtube.com in browser\n2. Press F12 -> Network -> Click any request -> Copy 'Cookie' header\n3. Paste here:");

        if (!string.IsNullOrWhiteSpace(cookies))
        {
            _auth.SaveCookies(cookies.Trim());
            IsAuthenticated = _auth.IsAuthenticated;
            RaiseAccountProperties();
            await _dialog.ShowInfoAsync("Success", "Cookies saved. Restart might be required for all features.");
        }
    }

    private async Task LogoutAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Auth_Logout"], SL["Dialog_LogoutMessage"]))
            return;
        _auth.Logout();
        IsAuthenticated = _auth.IsAuthenticated;
        RaiseAccountProperties();
    }

    private void RaiseAccountProperties()
    {
        this.RaisePropertyChanged(nameof(AccountName));
        this.RaisePropertyChanged(nameof(AccountAvatarUrl));
        this.RaisePropertyChanged(nameof(AccountSubtitle));
        this.RaisePropertyChanged(nameof(HasAccount));
        this.RaisePropertyChanged(nameof(IsFakeAccount));
    }

    private async Task BrowseDownloadPathAsync()
    {
        var newPath = await _dialog.SelectFolderAsync(DownloadPath);
        if (string.IsNullOrEmpty(newPath)) return;
        DownloadPath = newPath;
        _library.DownloadPath = newPath;
        _library.Save();
    }

    private async Task ClearHistoryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Confirm_Title"], SL["Dialog_ClearHistoryMessage"]))
            return;
        _library.ClearHistory();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_HistoryCleared"]);
    }

    private async Task ResetLibraryAsync()
    {
        if (!await _dialog.ConfirmAsync(SL["Dialog_Warning_Title"], SL["Dialog_ResetMessage"]))
            return;
        _library.Reset();
        LoadAllSettings();
        await _dialog.ShowInfoAsync(SL["Dialog_Done_Title"], SL["Dialog_ResetComplete"]);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
```