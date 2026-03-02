// Core/Services/PlaylistSyncService.cs

using LMP.Core.Models;
using LMP.Core.Youtube.Music;
using LMP.Core.Youtube.Playlists;
using LMP.UI.Dialogs;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис синхронизации одного плейлиста с YouTube.
///
/// <para><b>Зачем нужен:</b></para>
/// <para>
/// Ранее синхронизация была размазана по <c>MusicLibraryManager</c> (массовая),
/// <c>PlaylistEditService</c> (привязка/отвязка) и <c>PlaylistViewModel</c> (кнопка Refresh пустая).
/// Этот сервис — единый источник истины для синхронизации конкретного плейлиста.
/// </para>
///
/// <para><b>Архитектура:</b></para>
/// <list type="number">
///   <item>BuildPreviewAsync — загрузить diff между локальным и облачным состоянием</item>
///   <item>ShowSyncPlaylistDialog — показать пользователю что изменилось</item>
///   <item>ApplyAsync — применить выбранную стратегию</item>
/// </list>
/// </summary>
public sealed class PlaylistSyncService
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly MusicLibraryManager _manager;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;

    private static LocalizationService SL => LocalizationService.Instance;

    public PlaylistSyncService(
        LibraryService library,
        YoutubeProvider youtube,
        MusicLibraryManager manager,
        CookieAuthService auth,
        DialogService dialog)
    {
        _library = library;
        _youtube = youtube;
        _manager = manager;
        _auth = auth;
        _dialog = dialog;
    }

    #region Public API

    /// <summary>
    /// Полный цикл синхронизации: preview → диалог → применение.
    /// Вызывается из PlaylistViewModel.RefreshPlaylistAsync().
    /// </summary>
    /// <param name="playlistId">ID локального плейлиста.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Результат синхронизации, null если отменено пользователем.</returns>
    public async Task<PlaylistSyncResult?> SyncWithDialogAsync(
        string playlistId,
        CancellationToken ct = default)
    {
        // ═══ STEP 1: Валидация ═══
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        if (playlist.SyncMode != PlaylistSyncMode.TwoWaySync ||
            string.IsNullOrEmpty(playlist.YoutubeId))
        {
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncNotLinked"] ?? "Playlist is not linked to YouTube");
        }

        if (!_auth.IsAuthenticated)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncNotAuth"] ?? "Not authenticated");

        // ═══ STEP 2: Построить preview (diff) ═══
        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncFetchFailed"] ?? "Failed to fetch playlist data from YouTube");

        // Если нет различий — уведомляем и выходим
        if (!preview.HasAnyDifference)
        {
            await _dialog.ShowInfoAsync(
                SL["Playlist_SyncWithCloud"] ?? "Sync",
                SL["Playlist_SyncNoChanges"] ?? "Playlist is already in sync");
            return PlaylistSyncResult.NoChanges();
        }

        // ═══ STEP 3: Показать диалог выбора стратегии ═══
        var options = await _dialog.ShowPlaylistSyncDialogAsync(preview);
        if (options == null)
            return null; // Пользователь отменил

        // ═══ STEP 4: Применить стратегию ═══
        return await ApplyAsync(playlist, preview, options, ct);
    }

    /// <summary>
    /// Синхронизация без диалога — используется при первичной привязке.
    /// Стратегия задаётся программно.
    /// </summary>
    public async Task<PlaylistSyncResult> SyncDirectAsync(
        string playlistId,
        PlaylistSyncOptions options,
        CancellationToken ct = default)
    {
        var playlist = await _library.GetPlaylistAsync(playlistId, ct);
        if (playlist == null)
            return PlaylistSyncResult.Fail("Playlist not found");

        if (string.IsNullOrEmpty(playlist.YoutubeId))
            return PlaylistSyncResult.Fail("No YouTube ID");

        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail("Failed to fetch YouTube data");

        return await ApplyAsync(playlist, preview, options, ct);
    }

    #endregion

    #region Preview (Diff)

    /// <summary>
    /// Строит снимок различий между локальным и облачным состоянием плейлиста.
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Загрузка метаданных плейлиста из YouTube (название, описание, обложка)</item>
    ///   <item>Загрузка списка треков из YouTube (с setVideoId)</item>
    ///   <item>Загрузка локальных track IDs из БД</item>
    ///   <item>Вычисление diff: local-only, cloud-only, common</item>
    /// </list>
    /// </summary>
    private async Task<PlaylistSyncPreview?> BuildPreviewAsync(
        Playlist playlist,
        CancellationToken ct)
    {
        try
        {
            YoutubeProvider.ThrowIfInCooldown();

            // Параллельно загружаем метаданные и треки из YouTube + локальные треки
            var metadataTask = GetCloudMetadataAsync(playlist.YoutubeId!, ct);
            var cloudTracksTask = _youtube.GetPlaylistItemsWithSetVideoIdAsync(
                playlist.YoutubeId!, ct);
            var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

            await Task.WhenAll(metadataTask, cloudTracksTask, localTrackIdsTask);

            var metadata = await metadataTask;
            var cloudTracks = await cloudTracksTask;
            var localTrackIds = await localTrackIdsTask;

            if (metadata == null)
                return null;

            // Строим множества для diff
            // YouTube хранит raw video ID, у нас — "yt_{videoId}"
            var cloudVideoIds = new HashSet<string>(cloudTracks.Count, StringComparer.Ordinal);
            for (int i = 0; i < cloudTracks.Count; i++)
                cloudVideoIds.Add("yt_" + cloudTracks[i].VideoId);

            var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

            int commonCount = 0;
            int cloudOnlyCount = 0;
            int localOnlyCount = 0;

            // Cloud-only: есть в YouTube, нет локально
            foreach (var cloudId in cloudVideoIds)
            {
                if (localIdSet.Contains(cloudId))
                    commonCount++;
                else
                    cloudOnlyCount++;
            }

            // Local-only: есть локально, нет в YouTube
            foreach (var localId in localIdSet)
            {
                if (!cloudVideoIds.Contains(localId))
                    localOnlyCount++;
            }

            return new PlaylistSyncPreview
            {
                LocalName = playlist.Name,
                CloudName = metadata.Value.Name,
                LocalDescription = playlist.Description,
                CloudDescription = metadata.Value.Description,
                LocalThumbnailUrl = playlist.ThumbnailUrl,
                CloudThumbnailUrl = metadata.Value.ThumbnailUrl,
                LocalOnlyTrackCount = localOnlyCount,
                CloudOnlyTrackCount = cloudOnlyCount,
                CommonTrackCount = commonCount
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Preview failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Загружает метаданные плейлиста из YouTube (название, описание, обложка).
    /// Использует PlaylistClient.GetAsync() — уже умеет парсить Description.
    /// </summary>
    private async Task<(string Name, string? Description, string? ThumbnailUrl)?> GetCloudMetadataAsync(
        string youtubeId,
        CancellationToken ct)
    {
        try
        {
            var client = _youtube.GetClient();
            var plId = new PlaylistId(youtubeId);
            var cloudPlaylist = await client.Playlists.GetAsync(plId, ct);

            return (cloudPlaylist.Name, cloudPlaylist.Description, cloudPlaylist.ThumbnailUrl);
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Failed to fetch cloud metadata: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Apply Strategy

    /// <summary>
    /// Применяет выбранную стратегию синхронизации.
    /// </summary>
    private async Task<PlaylistSyncResult> ApplyAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        try
        {
            bool metadataChanged = false;
            int tracksAddedLocally = 0;
            int tracksAddedToCloud = 0;
            int tracksRemovedLocally = 0;
            int tracksRemovedFromCloud = 0;

            // ═══ Метаданные ═══
            metadataChanged = await SyncMetadataAsync(playlist, preview, options, ct);

            // ═══ Треки ═══
            if (options.SyncTracks)
            {
                var trackResult = options.Strategy switch
                {
                    PlaylistSyncStrategy.ReplaceLocal =>
                        await ReplaceLocalTracksAsync(playlist, ct),
                    PlaylistSyncStrategy.ReplaceCloud =>
                        await ReplaceCloudTracksAsync(playlist, ct),
                    PlaylistSyncStrategy.Merge =>
                        await MergeTracksAsync(playlist, ct),
                    _ => (0, 0, 0, 0)
                };

                // Деструктуризация tuple по позициям
                (tracksAddedLocally, tracksAddedToCloud, tracksRemovedLocally, tracksRemovedFromCloud) = trackResult;
            }

            // ═══ Сохраняем обновлённый плейлист ═══
            if (metadataChanged || tracksAddedLocally > 0 || tracksRemovedLocally > 0)
            {
                playlist.UpdatedAt = DateTime.Now;
                await _library.AddOrUpdatePlaylistAsync(playlist, ct);
            }

            var result = new PlaylistSyncResult
            {
                Success = true,
                MetadataChanged = metadataChanged,
                TracksAddedLocally = tracksAddedLocally,
                TracksAddedToCloud = tracksAddedToCloud,
                TracksRemovedLocally = tracksRemovedLocally,
                TracksRemovedFromCloud = tracksRemovedFromCloud
            };

            Log.Info($"[PlaylistSync] Completed: {result.Summary}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistSync] Apply failed: {ex.Message}");
            return PlaylistSyncResult.Fail(ex.Message);
        }
    }

    #endregion

    #region Metadata Sync

    /// <summary>
    /// Синхронизирует метаданные по выбранным полям.
    /// Направление определяется стратегией:
    /// - ReplaceLocal / Merge: YouTube → Local
    /// - ReplaceCloud: Local → YouTube
    /// </summary>
    private async Task<bool> SyncMetadataAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        bool changed = false;
        bool isCloudSource = options.Strategy != PlaylistSyncStrategy.ReplaceCloud;

        // ═══ Название ═══
        if (options.SyncName && preview.NameDiffers)
        {
            if (isCloudSource)
            {
                // YouTube → Local
                playlist.Name = preview.CloudName;
                changed = true;
                Log.Info($"[PlaylistSync] Name updated locally: '{preview.CloudName}'");
            }
            else
            {
                // Local → YouTube
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId!, playlist.Name);
                    Log.Info($"[PlaylistSync] Name updated in YouTube: '{playlist.Name}'");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Rename in YouTube failed: {ex.Message}");
                }
            }
        }

        // ═══ Описание ═══
        if (options.SyncDescription && preview.DescriptionDiffers)
        {
            if (isCloudSource)
            {
                // YouTube → Local
                playlist.Description = preview.CloudDescription;
                changed = true;
                Log.Info("[PlaylistSync] Description updated locally");
            }
            else
            {
                // Local → YouTube
                try
                {
                    var client = _youtube.GetClient();
                    await client.Mutations.SetPlaylistDescriptionAsync(
                        playlist.YoutubeId!, playlist.Description ?? "", ct);
                    Log.Info("[PlaylistSync] Description updated in YouTube");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistSync] Description update in YouTube failed: {ex.Message}");
                }
            }
        }

        // ═══ Обложка ═══
        if (options.SyncThumbnail)
        {
            if (isCloudSource)
            {
                // YouTube → Local: скачиваем URL
                if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl) &&
                    !string.Equals(preview.CloudThumbnailUrl, playlist.ThumbnailUrl, StringComparison.Ordinal))
                {
                    playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                    playlist.ComputedColor = null; // Пересчитается при отображении
                    changed = true;
                    Log.Info("[PlaylistSync] Thumbnail updated locally from YouTube");
                }
            }
            else
            {
                // Local → YouTube: загружаем файл
                if (!string.IsNullOrEmpty(playlist.ThumbnailUrl))
                {
                    try
                    {
                        var uploadSuccess = await UploadThumbnailToYoutubeAsync(
                            playlist.YoutubeId!, playlist.ThumbnailUrl, ct);

                        if (uploadSuccess)
                        {
                            Log.Info("[PlaylistSync] Thumbnail uploaded to YouTube");
                        }
                        else
                        {
                            Log.Warn("[PlaylistSync] Thumbnail upload to YouTube failed (non-critical)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[PlaylistSync] Thumbnail upload to YouTube failed: {ex.Message}");
                        // Не фатально — продолжаем синхронизацию
                    }
                }
            }
        }

        return changed;
    }

    #endregion

    #region Track Sync Strategies

    /// <summary>
    /// YouTube → Local: полностью заменить локальные треки облачными.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        // Загружаем треки из YouTube через PlaylistClient (полные метаданные)
        var client = _youtube.GetClient();
        var plId = new PlaylistId(youtubeId);

        // ValueTask → IReadOnlyList (CollectAsync возвращает ValueTask)
        var cloudTracks = await client.Playlists.GetVideosAsync(plId, ct).CollectAsync();

        // Загружаем облачные треки с setVideoId для будущих операций удаления
        var remoteItems = await _youtube.GetPlaylistItemsWithSetVideoIdAsync(youtubeId, ct);

        // Получаем текущие локальные треки
        var localTrackIds = await _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        // Удаляем все локальные треки из плейлиста
        int removedLocally = 0;
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            await _library.RemoveTrackFromPlaylistAsync(localTrackIds[i], playlist.Id, ct);
            removedLocally++;
        }

        // Добавляем облачные треки
        int addedLocally = 0;
        for (int i = 0; i < cloudTracks.Count; i++)
        {
            var track = cloudTracks[i];
            await _library.AddOrUpdateTrackAsync(track, ct);
            await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
            addedLocally++;
        }

        // Сохраняем setVideoId для каждого трека
        if (remoteItems.Count > 0)
        {
            var mappings = new List<(string TrackId, string SetVideoId)>(remoteItems.Count);
            for (int i = 0; i < remoteItems.Count; i++)
            {
                var item = remoteItems[i];
                mappings.Add(("yt_" + item.VideoId, item.SetVideoId));
            }
            await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);
        }

        Log.Info($"[PlaylistSync] ReplaceLocal: removed {removedLocally}, added {addedLocally}");
        return (addedLocally, 0, removedLocally, 0);
    }

    /// <summary>
    /// Local → YouTube: полностью заменить облачные треки локальными.
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceCloudTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        // Загружаем текущие облачные треки с setVideoId
        var remoteTracks = await _youtube.GetPlaylistItemsWithSetVideoIdAsync(youtubeId, ct);

        // Удаляем все треки из YouTube-плейлиста (batch)
        int removedFromCloud = 0;
        if (remoteTracks.Count > 0)
        {
            var setVideoIds = new List<string>(remoteTracks.Count);
            for (int i = 0; i < remoteTracks.Count; i++)
                setVideoIds.Add(remoteTracks[i].SetVideoId);

            await _youtube.RemoveTracksFromPlaylistAsync(youtubeId, setVideoIds);
            removedFromCloud = setVideoIds.Count;
        }

        // Загружаем локальные треки в YouTube (batch)
        var localTrackIds = await _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);
        int addedToCloud = 0;

        // Фильтруем только YouTube-треки (локальные файлы нельзя загрузить)
        var ytTrackIds = new List<string>(localTrackIds.Count);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            if (localTrackIds[i].StartsWith("yt_"))
                ytTrackIds.Add(localTrackIds[i]);
        }

        if (ytTrackIds.Count > 0)
        {
            var setVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, ytTrackIds);
            addedToCloud = ytTrackIds.Count;

            // Сохраняем новые setVideoId
            if (setVideoIds.Count > 0)
            {
                var mappings = new List<(string TrackId, string SetVideoId)>(setVideoIds.Count);
                for (int i = 0; i < setVideoIds.Count && i < ytTrackIds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(setVideoIds[i]))
                        mappings.Add((ytTrackIds[i], setVideoIds[i]!));
                }
                if (mappings.Count > 0)
                    await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);
            }
        }

        Log.Info($"[PlaylistSync] ReplaceCloud: removed {removedFromCloud}, added {addedToCloud}");
        return (0, addedToCloud, 0, removedFromCloud);
    }

    /// <summary>
    /// Двусторонний merge: добавить отсутствующие треки в обе стороны.
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Загрузить облачные треки (с полными метаданными)</item>
    ///   <item>Загрузить локальные track IDs</item>
    ///   <item>Cloud-only → добавить локально</item>
    ///   <item>Local-only → добавить в YouTube</item>
    ///   <item>Сохранить setVideoId для всех треков</item>
    /// </list>
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
      MergeTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var client = _youtube.GetClient();

        // ValueTask требует AsTask() для Task.WhenAll
        var cloudFullTask = client.Playlists.GetVideosAsync(
            new PlaylistId(youtubeId), ct).CollectAsync().AsTask();
        var remoteItemsTask = _youtube.GetPlaylistItemsWithSetVideoIdAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(cloudFullTask, remoteItemsTask, localTrackIdsTask);

        var cloudTracks = await cloudFullTask;
        var remoteItems = await remoteItemsTask;
        var localTrackIds = await localTrackIdsTask;

        var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

        // ═══ Cloud-only → добавить локально ═══
        int addedLocally = 0;
        for (int i = 0; i < cloudTracks.Count; i++)
        {
            var track = cloudTracks[i];
            if (!localIdSet.Contains(track.Id))
            {
                await _library.AddOrUpdateTrackAsync(track, ct);
                await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
                addedLocally++;
            }
        }

        // ═══ Local-only → добавить в YouTube ═══
        var cloudIdSet = new HashSet<string>(cloudTracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < cloudTracks.Count; i++)
            cloudIdSet.Add(cloudTracks[i].Id);

        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var trackId = localTrackIds[i];
            if (trackId.StartsWith("yt_") && !cloudIdSet.Contains(trackId))
                toUpload.Add(trackId);
        }

        int addedToCloud = 0;
        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload);
            addedToCloud = toUpload.Count;

            // Сохраняем setVideoId для загруженных треков
            var uploadMappings = new List<(string TrackId, string SetVideoId)>();
            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    uploadMappings.Add((toUpload[i], newSetVideoIds[i]!));
            }
            if (uploadMappings.Count > 0)
                await _library.UpdateSetVideoIdsAsync(playlist.Id, uploadMappings, ct);
        }

        // ═══ Сохраняем setVideoId для существующих облачных треков ═══
        if (remoteItems.Count > 0)
        {
            var existingMappings = new List<(string TrackId, string SetVideoId)>(remoteItems.Count);
            for (int i = 0; i < remoteItems.Count; i++)
            {
                var item = remoteItems[i];
                existingMappings.Add(("yt_" + item.VideoId, item.SetVideoId));
            }
            await _library.UpdateSetVideoIdsAsync(playlist.Id, existingMappings, ct);
        }

        Log.Info($"[PlaylistSync] Merge: +{addedLocally} local, +{addedToCloud} cloud");
        return (addedLocally, addedToCloud, 0, 0);
    }

    #endregion

    #region Thumbnail Upload Helper

    /// <summary>
    /// Загружает локальную обложку в YouTube.
    /// Поддерживает HTTP URL (скачивает) и локальные файлы.
    /// </summary>
    private async Task<bool> UploadThumbnailToYoutubeAsync(
        string youtubePlaylistId,
        string thumbnailUrl,
        CancellationToken ct)
    {
        byte[] imageData;
        string mimeType = "image/jpeg";

        // ═══ Скачиваем/читаем изображение ═══
        if (thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // HTTP URL — скачиваем
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var response = await httpClient.GetAsync(thumbnailUrl, ct);
            response.EnsureSuccessStatusCode();

            imageData = await response.Content.ReadAsByteArrayAsync(ct);

            // Определяем MIME-тип из Content-Type
            if (response.Content.Headers.ContentType?.MediaType is { } contentType)
                mimeType = contentType;
        }
        else if (thumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            // file:// URI
            var uri = new Uri(thumbnailUrl);
            var localPath = uri.LocalPath;

            if (!File.Exists(localPath))
            {
                Log.Warn($"[PlaylistSync] Thumbnail file not found: {localPath}");
                return false;
            }

            imageData = await File.ReadAllBytesAsync(localPath, ct);
            mimeType = GetMimeTypeFromExtension(Path.GetExtension(localPath));
        }
        else if (Path.IsPathRooted(thumbnailUrl) && File.Exists(thumbnailUrl))
        {
            // Абсолютный путь к файлу
            imageData = await File.ReadAllBytesAsync(thumbnailUrl, ct);
            mimeType = GetMimeTypeFromExtension(Path.GetExtension(thumbnailUrl));
        }
        else
        {
            Log.Warn($"[PlaylistSync] Invalid thumbnail URL: {thumbnailUrl}");
            return false;
        }

        // ═══ Валидация размера ═══
        if (imageData.Length == 0)
        {
            Log.Warn("[PlaylistSync] Thumbnail is empty");
            return false;
        }

        if (imageData.Length > 20 * 1024 * 1024)
        {
            Log.Warn($"[PlaylistSync] Thumbnail too large: {imageData.Length / 1024 / 1024}MB (max 20MB)");
            return false;
        }

        // ═══ Загружаем через Scotty ═══
        return await _youtube.UploadPlaylistThumbnailAsync(
            youtubePlaylistId, imageData, mimeType);
    }

    /// <summary>
    /// Определяет MIME-тип по расширению файла.
    /// </summary>
    private static string GetMimeTypeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg" // Fallback
    };

    #endregion
}