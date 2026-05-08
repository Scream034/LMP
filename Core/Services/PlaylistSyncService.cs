using LMP.Core.Models;
using LMP.Core.Youtube.Playlists;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис синхронизации одного плейлиста с YouTube.
///
/// <para><b>Зачем нужен:</b></para>
/// <para>
/// Ранее синхронизация была размазана по <c>MusicLibraryManager</c> (массовая),
/// <c>PlaylistEditService</c> (привязка/отвязка) и <c>PlaylistViewModel</c> (кнопка Refresh).
/// Этот сервис — единый источник истины для синхронизации конкретного плейлиста.
/// </para>
///
/// <para><b>Архитектура:</b></para>
/// <list type="number">
///   <item><see cref="BuildPreviewAsync"/> — загрузить diff между локальным и облачным состоянием</item>
///   <item>ShowSyncPlaylistDialog — показать пользователю что изменилось</item>
///   <item><see cref="ApplyAsync"/> — применить выбранную стратегию</item>
/// </list>
///
/// <para><b>Важно про источники данных треков:</b></para>
/// <para>
/// Для diff используется <c>GetVideosAsync</c> (только воспроизводимые треки, без удалённых).
/// Для sync-операций используется <c>GetPlaylistItemsWithSetVideoIdAsync</c> (нужен SetVideoId).
/// Это намеренное разделение: удалённые видео должны игнорироваться в diff,
/// иначе они всегда будут показываться как «только в YouTube» после каждого merge.
/// </para>
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
    /// <returns>Результат синхронизации, null если пользователь отменил диалог.</returns>
    public async Task<PlaylistSyncResult?> SyncWithDialogAsync(
        string playlistId,
        CancellationToken ct = default)
    {
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

        var preview = await BuildPreviewAsync(playlist, ct);
        if (preview == null)
            return PlaylistSyncResult.Fail(
                SL["Playlist_SyncFetchFailed"] ?? "Failed to fetch playlist data from YouTube");

        if (!preview.HasAnyDifference)
        {
            await _dialog.ShowInfoAsync(
                SL["Playlist_SyncWithCloud"] ?? "Sync",
                SL["Playlist_SyncNoChanges"] ?? "Playlist is already in sync");
            return PlaylistSyncResult.NoChanges();
        }

        var options = await _dialog.ShowPlaylistSyncDialogAsync(preview);
        if (options == null)
            return null;

        return await ApplyAsync(playlist, preview, options, ct);
    }

    /// <summary>
    /// Синхронизация без диалога — используется при первичной привязке плейлиста к YouTube.
    /// Стратегия задаётся программно без участия пользователя.
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
    ///   <item>Параллельно загружает: метаданные из YouTube, воспроизводимые треки (GetVideosAsync),
    ///         setVideoId-список и локальные track IDs</item>
    ///   <item>Строит множества cloud/local для O(n) diff по воспроизводимым трекам</item>
    ///   <item>Нормализует thumbnail URL (strip query string) для стабильного сравнения</item>
    /// </list>
    ///
    /// <para><b>Почему GetVideosAsync а не GetPlaylistItemsWithSetVideoIdAsync для diff:</b></para>
    /// <para>
    /// <c>GetPlaylistItemsWithSetVideoIdAsync</c> возвращает ВСЕ элементы плейлиста, включая
    /// удалённые и недоступные видео. Они всегда будут показываться как «только в YouTube»,
    /// создавая ложный diff после каждого успешного merge.
    /// <c>GetVideosAsync</c> возвращает только реально доступные треки — именно их
    /// обрабатывают стратегии sync (Merge, ReplaceLocal, ReplaceCloud).
    /// </para>
    ///
    /// <para><b>Thumbnail URL:</b></para>
    /// <para>YouTube нестабильно меняет query-параметры (sqp=...) при каждом запросе.
    /// Нормализуем до base URL для стабильного сравнения.</para>
    /// </summary>
    private async Task<PlaylistSyncPreview?> BuildPreviewAsync(
        Playlist playlist,
        CancellationToken ct)
    {
        try
        {
            YoutubeProvider.ThrowIfInCooldown();

            var client = _youtube.GetClient();
            var plId = new PlaylistId(playlist.YoutubeId!);

            // Параллельно загружаем все данные:
            // - метаданные (имя, описание, обложка)
            // - воспроизводимые треки для diff (те же, что обрабатывают стратегии sync)
            // - setVideoId-список (нужен только для операций удаления в Apply)
            // - локальные track IDs
            var metadataTask = GetCloudMetadataAsync(playlist.YoutubeId!, ct);
            var cloudVideosTask = client.Playlists.GetVideosAsync(plId, ct).CollectAsync().AsTask();
            var remoteItemsTask = _youtube.GetPlaylistItemsWithSetVideoIdAsync(playlist.YoutubeId!, ct);
            var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

            await Task.WhenAll(metadataTask, cloudVideosTask, remoteItemsTask, localTrackIdsTask);

            var metadata = await metadataTask;
            var cloudVideos = await cloudVideosTask;
            var localTrackIds = await localTrackIdsTask;

            if (metadata == null)
                return null;

            // Строим множество облачных ID по воспроизводимым трекам
            // (те же данные, что будут обработаны стратегиями sync)
            var cloudVideoIds = new HashSet<string>(cloudVideos.Count, StringComparer.Ordinal);
            for (int i = 0; i < cloudVideos.Count; i++)
                cloudVideoIds.Add(cloudVideos[i].Id);

            var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

            int commonCount = 0;
            int cloudOnlyCount = 0;

            foreach (var cloudId in cloudVideoIds)
            {
                if (localIdSet.Contains(cloudId))
                    commonCount++;
                else
                    cloudOnlyCount++;
            }

            int localOnlyCount = 0;
            foreach (var localId in localIdSet)
            {
                if (!cloudVideoIds.Contains(localId))
                    localOnlyCount++;
            }

            Log.Debug($"[PlaylistSync] Diff: common={commonCount}, " +
                      $"cloudOnly={cloudOnlyCount}, localOnly={localOnlyCount}");

            return new PlaylistSyncPreview
            {
                LocalName = playlist.Name,
                CloudName = metadata.Value.Name,
                LocalDescription = playlist.Description,
                CloudDescription = metadata.Value.Description,
                // Сохраняем оригинальные URL для отображения в диалоге
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
    /// Нормализует URL обложки для стабильного сравнения.
    /// Убирает query string (sqp=..., v=...) — YouTube меняет их при каждом запросе.
    /// </summary>
    private static string? NormalizeThumbnailUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var queryIndex = url.IndexOf('?');
            return queryIndex >= 0 ? url[..queryIndex] : url;
        }

        return url;
    }

    /// <summary>
    /// Загружает метаданные плейлиста из YouTube (название, описание, обложка).
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

            metadataChanged = await SyncMetadataAsync(playlist, preview, options, ct);

            if (options.SyncTracks)
            {
                (tracksAddedLocally, tracksAddedToCloud, tracksRemovedLocally, tracksRemovedFromCloud)
                    = options.Strategy switch
                    {
                        PlaylistSyncStrategy.ReplaceLocal =>
                            await ReplaceLocalTracksAsync(playlist, ct),
                        PlaylistSyncStrategy.ReplaceCloud =>
                            await ReplaceCloudTracksAsync(playlist, ct),
                        PlaylistSyncStrategy.Merge =>
                            await MergeTracksAsync(playlist, ct),
                        _ => (0, 0, 0, 0)
                    };
            }

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
    ///
    /// <para><b>Направление:</b></para>
    /// <list type="bullet">
    ///   <item>ReplaceLocal / Merge → YouTube → Local</item>
    ///   <item>ReplaceCloud → Local → YouTube</item>
    /// </list>
    /// </summary>
    private async Task<bool> SyncMetadataAsync(
        Playlist playlist,
        PlaylistSyncPreview preview,
        PlaylistSyncOptions options,
        CancellationToken ct)
    {
        bool changed = false;
        bool isCloudSource = options.Strategy != PlaylistSyncStrategy.ReplaceCloud;

        if (options.SyncName && preview.NameDiffers)
        {
            if (isCloudSource)
            {
                playlist.Name = preview.CloudName;
                changed = true;
                Log.Info($"[PlaylistSync] Name updated locally: '{preview.CloudName}'");
            }
            else
            {
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

        if (options.SyncDescription && preview.DescriptionDiffers)
        {
            if (isCloudSource)
            {
                playlist.Description = preview.CloudDescription;
                changed = true;
                Log.Info("[PlaylistSync] Description updated locally");
            }
            else
            {
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

        if (options.SyncThumbnail)
        {
            if (isCloudSource)
            {
                // Сравниваем нормализованные URL для определения реального различия,
                // но сохраняем оригинальный URL из YouTube
                var cloudNorm = NormalizeThumbnailUrl(preview.CloudThumbnailUrl);
                var localNorm = NormalizeThumbnailUrl(playlist.ThumbnailUrl);

                if (!string.IsNullOrEmpty(preview.CloudThumbnailUrl) &&
                    !string.Equals(cloudNorm, localNorm, StringComparison.OrdinalIgnoreCase))
                {
                    playlist.ThumbnailUrl = preview.CloudThumbnailUrl;
                    playlist.ComputedColor = null;
                    changed = true;
                    Log.Info("[PlaylistSync] Thumbnail updated locally from YouTube");
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(playlist.ThumbnailUrl))
                {
                    try
                    {
                        var success = await UploadThumbnailToYoutubeAsync(
                            playlist.YoutubeId!, playlist.ThumbnailUrl, ct);

                        if (success)
                            Log.Info("[PlaylistSync] Thumbnail uploaded to YouTube");
                        else
                            Log.Warn("[PlaylistSync] Thumbnail upload to YouTube skipped or failed");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[PlaylistSync] Thumbnail upload failed: {ex.Message}");
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
    ///
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Параллельно загружает воспроизводимые треки и setVideoId-список</item>
    ///   <item>Удаляет все локальные треки из плейлиста</item>
    ///   <item>Добавляет облачные воспроизводимые треки</item>
    ///   <item>Сохраняет setVideoId в БД единым батчем</item>
    /// </list>
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceLocalTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var client = _youtube.GetClient();
        var plId = new PlaylistId(youtubeId);

        var cloudTracksTask = client.Playlists.GetVideosAsync(plId, ct).CollectAsync().AsTask();
        var remoteItemsTask = _youtube.GetPlaylistItemsWithSetVideoIdAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(cloudTracksTask, remoteItemsTask, localTrackIdsTask);

        var cloudTracks = await cloudTracksTask;
        var remoteItems = await remoteItemsTask;
        var localTrackIds = await localTrackIdsTask;

        int removedLocally = 0;
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            await _library.RemoveTrackFromPlaylistAsync(localTrackIds[i], playlist.Id, ct);
            removedLocally++;
        }

        int addedLocally = 0;
        for (int i = 0; i < cloudTracks.Count; i++)
        {
            var track = cloudTracks[i];
            await _library.AddOrUpdateTrackAsync(track, ct);
            await _library.AddTrackToPlaylistAsync(track, playlist.Id, ct);
            addedLocally++;
        }

        if (remoteItems.Count > 0)
        {
            var mappings = new List<(string TrackId, string SetVideoId)>(remoteItems.Count);
            for (int i = 0; i < remoteItems.Count; i++)
            {
                var item = remoteItems[i];
                if (!string.IsNullOrEmpty(item.SetVideoId))
                    mappings.Add(("yt_" + item.VideoId, item.SetVideoId));
            }

            if (mappings.Count > 0)
                await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);
        }

        Log.Info($"[PlaylistSync] ReplaceLocal: removed={removedLocally}, added={addedLocally}");
        return (addedLocally, 0, removedLocally, 0);
    }

    /// <summary>
    /// Local → YouTube: полностью заменить облачные треки локальными.
    ///
    /// <para><b>Ограничение:</b> локальные файлы (не YouTube-треки) пропускаются —
    /// их нельзя добавить в YouTube-плейлист.</para>
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        ReplaceCloudTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;

        var remoteTracksTask = _youtube.GetPlaylistItemsWithSetVideoIdAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(remoteTracksTask, localTrackIdsTask);

        var remoteTracks = await remoteTracksTask;
        var localTrackIds = await localTrackIdsTask;

        int removedFromCloud = 0;
        if (remoteTracks.Count > 0)
        {
            var setVideoIds = new List<string>(remoteTracks.Count);
            for (int i = 0; i < remoteTracks.Count; i++)
                setVideoIds.Add(remoteTracks[i].SetVideoId);

            await _youtube.RemoveTracksFromPlaylistAsync(youtubeId, setVideoIds);
            removedFromCloud = setVideoIds.Count;
        }

        var ytTrackIds = new List<string>(localTrackIds.Count);
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            if (localTrackIds[i].StartsWith("yt_", StringComparison.Ordinal))
                ytTrackIds.Add(localTrackIds[i]);
        }

        int addedToCloud = 0;
        if (ytTrackIds.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, ytTrackIds);
            addedToCloud = ytTrackIds.Count;

            var mappings = new List<(string TrackId, string SetVideoId)>(newSetVideoIds.Count);
            for (int i = 0; i < newSetVideoIds.Count && i < ytTrackIds.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    mappings.Add((ytTrackIds[i], newSetVideoIds[i]!));
            }

            if (mappings.Count > 0)
                await _library.UpdateSetVideoIdsAsync(playlist.Id, mappings, ct);
        }

        Log.Info($"[PlaylistSync] ReplaceCloud: removed={removedFromCloud}, added={addedToCloud}");
        return (0, addedToCloud, 0, removedFromCloud);
    }

    /// <summary>
    /// Двусторонний merge: добавить отсутствующие треки в обе стороны без удаления.
    ///
    /// <para><b>SetVideoId:</b> сохраняются единым батчем после всех операций
    /// чтобы минимизировать количество запросов к БД.</para>
    /// </summary>
    private async Task<(int AddedLocally, int AddedToCloud, int RemovedLocally, int RemovedFromCloud)>
        MergeTracksAsync(Playlist playlist, CancellationToken ct)
    {
        var youtubeId = playlist.YoutubeId!;
        var client = _youtube.GetClient();

        var cloudFullTask = client.Playlists.GetVideosAsync(
            new PlaylistId(youtubeId), ct).CollectAsync().AsTask();
        var remoteItemsTask = _youtube.GetPlaylistItemsWithSetVideoIdAsync(youtubeId, ct);
        var localTrackIdsTask = _library.GetPlaylistTrackIdsAsync(playlist.Id, ct);

        await Task.WhenAll(cloudFullTask, remoteItemsTask, localTrackIdsTask);

        var cloudTracks = await cloudFullTask;
        var remoteItems = await remoteItemsTask;
        var localTrackIds = await localTrackIdsTask;

        var localIdSet = new HashSet<string>(localTrackIds, StringComparer.Ordinal);

        var cloudIdSet = new HashSet<string>(cloudTracks.Count, StringComparer.Ordinal);
        for (int i = 0; i < cloudTracks.Count; i++)
            cloudIdSet.Add(cloudTracks[i].Id);

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
        var toUpload = new List<string>();
        for (int i = 0; i < localTrackIds.Count; i++)
        {
            var trackId = localTrackIds[i];
            if (trackId.StartsWith("yt_", StringComparison.Ordinal) &&
                !cloudIdSet.Contains(trackId))
            {
                toUpload.Add(trackId);
            }
        }

        int addedToCloud = 0;
        var uploadMappings = new List<(string TrackId, string SetVideoId)>();

        if (toUpload.Count > 0)
        {
            var newSetVideoIds = await _youtube.AddTracksToPlaylistAsync(youtubeId, toUpload);
            addedToCloud = toUpload.Count;

            for (int i = 0; i < newSetVideoIds.Count && i < toUpload.Count; i++)
            {
                if (!string.IsNullOrEmpty(newSetVideoIds[i]))
                    uploadMappings.Add((toUpload[i], newSetVideoIds[i]!));
            }
        }

        // ═══ Сохраняем setVideoId единым батчем ═══
        var allMappings = new List<(string TrackId, string SetVideoId)>(
            remoteItems.Count + uploadMappings.Count);

        for (int i = 0; i < remoteItems.Count; i++)
        {
            var item = remoteItems[i];
            if (!string.IsNullOrEmpty(item.SetVideoId))
                allMappings.Add(("yt_" + item.VideoId, item.SetVideoId));
        }

        allMappings.AddRange(uploadMappings);

        if (allMappings.Count > 0)
            await _library.UpdateSetVideoIdsAsync(playlist.Id, allMappings, ct);

        Log.Info($"[PlaylistSync] Merge: +{addedLocally} local, +{addedToCloud} cloud");
        return (addedLocally, addedToCloud, 0, 0);
    }

    #endregion

    #region Thumbnail Upload Helper

    /// <summary>
    /// Загружает локальную обложку в YouTube.
    /// Поддерживает HTTP URL (скачивает) и локальные файлы (читает напрямую).
    /// </summary>
    private async Task<bool> UploadThumbnailToYoutubeAsync(
        string youtubePlaylistId,
        string thumbnailUrl,
        CancellationToken ct)
    {
        byte[] imageData;
        string mimeType = "image/jpeg";

        if (thumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var response = await httpClient.GetAsync(thumbnailUrl, ct);
            response.EnsureSuccessStatusCode();
            imageData = await response.Content.ReadAsByteArrayAsync(ct);

            if (response.Content.Headers.ContentType?.MediaType is { } contentType)
                mimeType = contentType;
        }
        else if (thumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
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
            imageData = await File.ReadAllBytesAsync(thumbnailUrl, ct);
            mimeType = GetMimeTypeFromExtension(Path.GetExtension(thumbnailUrl));
        }
        else
        {
            Log.Warn($"[PlaylistSync] Invalid thumbnail URL for upload: {thumbnailUrl}");
            return false;
        }

        if (imageData.Length == 0)
        {
            Log.Warn("[PlaylistSync] Thumbnail is empty, skipping upload");
            return false;
        }

        if (imageData.Length > 20 * 1024 * 1024)
        {
            Log.Warn($"[PlaylistSync] Thumbnail too large: {imageData.Length / 1024 / 1024}MB (max 20MB)");
            return false;
        }

        return await _youtube.UploadPlaylistThumbnailAsync(
            youtubePlaylistId, imageData, mimeType);
    }

    /// <summary>
    /// Определяет MIME-тип по расширению файла изображения.
    /// </summary>
    private static string GetMimeTypeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    #endregion
}