using LMP.Core.Models;
using LMP.UI.Dialogs;

namespace LMP.Core.Services;

/// <summary>
/// Централизованный сервис редактирования плейлистов.
/// 
/// <para><b>Зачем нужен:</b></para>
/// <para>
/// Логика редактирования плейлиста (переименование, обложка, цвет, привязка/отвязка YouTube)
/// ранее дублировалась в <c>LibraryViewModel.EditPlaylistFromCardAsync</c> и
/// <c>PlaylistViewModel.EditPlaylistAsync</c> (~80% одинакового кода).
/// Этот сервис — единый источник истины (Single Source of Truth).
/// </para>
/// 
/// <para><b>Принцип работы:</b></para>
/// <list type="number">
///   <item>Загружает актуальное состояние плейлиста из БД</item>
///   <item>Показывает диалог редактирования</item>
///   <item>Применяет изменения: sync → name → thumbnail → description → color</item>
///   <item>Сохраняет в БД (что триггерит <c>OnPlaylistChanged</c>)</item>
/// </list>
/// 
/// <para><b>Зависимости:</b></para>
/// <para>Не знает о UI-страницах. Навигационную блокировку получает через callback-и.</para>
/// </summary>
public sealed class PlaylistEditService
{
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly CookieAuthService _auth;
    private readonly DialogService _dialog;
    private readonly PlaylistSyncService _syncService;
    private readonly NotificationService _notifications;

    /// <summary>Быстрый доступ к локализации.</summary>
    private static LocalizationService SL => LocalizationService.Instance;

    /// <param name="library">Сервис библиотеки для чтения/записи данных.</param>
    /// <param name="youtube">Провайдер YouTube API для облачных операций.</param>
    /// <param name="auth">Сервис авторизации для проверки доступа к YouTube.</param>
    /// <param name="dialog">Сервис диалогов для показа UI.</param>
    public PlaylistEditService(
        LibraryService library,
        YoutubeProvider youtube,
        CookieAuthService auth,
        PlaylistSyncService syncService,
        NotificationService notifications,
        DialogService dialog)
    {
        _library = library;
        _youtube = youtube;
        _auth = auth;
        _syncService = syncService;
        _notifications = notifications;
        _dialog = dialog;
    }

    /// <summary>
    /// Результат операции редактирования.
    /// </summary>
    /// <param name="Changed">Были ли фактические изменения в плейлисте.</param>
    /// <param name="Playlist">Обновлённый объект плейлиста.</param>
    public sealed record EditResult(bool Changed, Playlist Playlist);

    /// <summary>
    /// Выполняет полный цикл редактирования плейлиста.
    /// 
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Загружает свежее состояние из БД (чтобы не работать с устаревшими данными)</item>
    ///   <item>Показывает диалог <see cref="EditPlaylistDialog"/></item>
    ///   <item>Обрабатывает изменение синхронизации (link/unlink YouTube)</item>
    ///   <item>Обрабатывает переименование (с синхронизацией в YouTube если привязан)</item>
    ///   <item>Обрабатывает обложку (валидация URI + локальные пути для мозаик)</item>
    ///   <item>Обрабатывает описание</item>
    ///   <item>Обрабатывает цвет (CustomColor + ComputedColor)</item>
    ///   <item>Сохраняет всё одним вызовом в БД</item>
    /// </list>
    /// 
    /// <para><b>Порядок шагов важен:</b> sync toggle обрабатывается ПЕРЕД rename,
    /// потому что после привязки к YouTube переименование должно идти через API.</para>
    /// </summary>
    /// <param name="playlistId">ID плейлиста для редактирования.</param>
    /// <param name="lockNavigation">
    /// Callback для блокировки навигации на время сетевых операций.
    /// Принимает строку-сообщение (например "Переименование...").
    /// </param>
    /// <param name="unlockNavigation">Callback для разблокировки навигации.</param>
    /// <returns>
    /// <see cref="EditResult"/> с флагом изменений и обновлённым плейлистом.
    /// <c>null</c> если пользователь отменил диалог или плейлист не найден.
    /// </returns>
    public async Task<EditResult?> EditPlaylistAsync(
        string playlistId,
        Action<string> lockNavigation,
        Action unlockNavigation)
    {
        // ═══ Загружаем свежее состояние из БД ═══
        // Важно: не используем переданный объект, т.к. он может быть устаревшим
        var playlist = await _library.GetPlaylistAsync(playlistId);
        if (playlist == null) return null;

        // ═══ Показываем диалог редактирования ═══
        var result = await _dialog.ShowEditPlaylistDialogAsync(playlist);
        if (result == null) return null;

        bool changed = false;

        // ═══ STEP 1: Sync toggle ═══
        if (result.SyncToCloud.HasValue && result.SyncToCloud.Value != playlist.IsFromAccount)
        {
            bool wantsSync = result.SyncToCloud.Value;

            if (wantsSync && !playlist.IsFromAccount && _auth.IsAuthenticated)
            {
                changed |= await TryLinkToCloudAsync(
                    playlist, playlistId, lockNavigation, unlockNavigation);
            }
            else if (!wantsSync && playlist.IsFromAccount)
            {
                changed |= await TryUnlinkFromCloudAsync(playlist);
            }
        }

        // ═══ STEP 2: Rename ═══
        var newName = result.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(newName) &&
            !string.Equals(newName, playlist.Name, StringComparison.Ordinal))
        {
            if (playlist.IsFromAccount && !string.IsNullOrEmpty(playlist.YoutubeId))
            {
                lockNavigation(SL["Playlist_Renaming"] ?? "Переименование...");
                try
                {
                    await _youtube.RenamePlaylistAsync(playlist.YoutubeId, newName);
                    playlist.Name = newName;
                    changed = true;
                    Log.Info($"[PlaylistEdit] Переименован на YouTube: {playlist.YoutubeId} → '{newName}'");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistEdit] Ошибка переименования на YouTube: {ex.Message}");

                    // БЫЛО: await _dialog.ShowInfoAsync(...)
                    // СТАЛО: Toast-уведомление
                    await _notifications.ShowToastAsync(
                        titleKey: "Dialog_Error_Title",
                        messageKey: "Playlist_RenameCloudFailed",
                        messageArgs: new object[] { ex.Message },
                        severity: NotificationSeverity.Error,
                        durationMs: 5000);

                    _notifications.PlayErrorSound();
                }
                finally
                {
                    unlockNavigation();
                }
            }
            else
            {
                playlist.Name = newName;
                changed = true;
            }
        }

        // ═══ STEP 3: Thumbnail ═══
        if (!string.Equals(result.ThumbnailUrl, playlist.ThumbnailUrl, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            {
                if (IsValidThumbnail(result.ThumbnailUrl))
                {
                    playlist.ThumbnailUrl = result.ThumbnailUrl;
                    changed = true;
                    playlist.ComputedColor = null;
                }
                else
                {
                    Log.Warn($"[PlaylistEdit] Невалидный путь обложки: {result.ThumbnailUrl}");

                    // БЫЛО: await _dialog.ShowInfoAsync(...)
                    // СТАЛО: Toast-уведомление
                    await _notifications.ShowToastAsync(
                        titleKey: "Dialog_Warning_Title",
                        messageKey: "Error_InvalidThumbnailUrl",
                        severity: NotificationSeverity.Warning,
                        durationMs: 4000);
                }
            }
            else
            {
                playlist.ThumbnailUrl = null;
                playlist.ComputedColor = null;
                changed = true;
            }
        }

        // ═══ STEP 3.5: Description ═══
        if (!string.Equals(result.Description?.Trim(), playlist.Description?.Trim(), StringComparison.Ordinal))
        {
            var newDesc = result.Description?.Trim();

            if (playlist.IsFromAccount && !string.IsNullOrEmpty(playlist.YoutubeId))
            {
                try
                {
                    await _youtube.EditPlaylistDescriptionAsync(playlist.YoutubeId, newDesc ?? "");
                    playlist.Description = newDesc;
                    changed = true;
                    Log.Info($"[PlaylistEdit] Описание обновлено на YouTube: {playlist.YoutubeId}");
                }
                catch (Exception ex)
                {
                    Log.Error($"[PlaylistEdit] Ошибка обновления описания на YouTube: {ex.Message}");
                    // Описание менее критично — применяем локально даже при ошибке
                    playlist.Description = newDesc;
                    changed = true;
                }
            }
            else
            {
                playlist.Description = newDesc;
                changed = true;
            }
        }

        // ═══ STEP 4: Custom Color ═══
        if (!string.Equals(result.CustomColor, playlist.CustomColor, StringComparison.Ordinal))
        {
            playlist.CustomColor = result.CustomColor;
            changed = true;
        }

        // ═══ STEP 4.5: Computed Color ═══
        if (result.ComputedColor != null &&
            !string.Equals(result.ComputedColor, playlist.ComputedColor, StringComparison.OrdinalIgnoreCase))
        {
            playlist.ComputedColor = result.ComputedColor;
            changed = true;
        }

        // ═══ STEP 5: Save ═══
        if (changed)
        {
            playlist.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(playlist);

            Log.Info($"[PlaylistEdit] Сохранён: Id={playlist.Id}, " +
                     $"SyncMode={playlist.SyncMode}, " +
                     $"YoutubeId={playlist.YoutubeId ?? "null"}, " +
                     $"Name={playlist.Name}");

            // Успешное сохранение — показываем toast
            await _notifications.ShowToastAsync(
                titleKey: "EditPlaylist_Saved",
                messageKey: "EditPlaylist_Saved",
                severity: NotificationSeverity.Success,
                durationMs: 2000);

            NotificationService.PlaySuccessSound();
        }

        return new EditResult(changed, playlist);
    }

    /// <summary>
    /// Проверяет валидность значения обложки.
    /// 
    /// <para>Допустимые форматы:</para>
    /// <list type="bullet">
    ///   <item>HTTP/HTTPS URL — обложка из интернета</item>
    ///   <item><c>avares://</c> URI — встроенный ресурс приложения</item>
    ///   <item>Абсолютный путь к существующему файлу — мозаика или выбранный файл</item>
    ///   <item><c>file://</c> URI — локальный файл</item>
    /// </list>
    /// </summary>
    private static bool IsValidThumbnail(string? thumbnailValue)
    {
        if (string.IsNullOrWhiteSpace(thumbnailValue)) return false;

        // HTTP/HTTPS — допустимо (валидность проверяется при загрузке)
        if (thumbnailValue.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            thumbnailValue.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;

        // avares:// — встроенный ресурс
        if (thumbnailValue.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            return true;

        // file:// URI → проверяем существование
        if (thumbnailValue.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(thumbnailValue, UriKind.Absolute, out var fileUri))
                return File.Exists(fileUri.LocalPath);
            return false;
        }

        // Абсолютный путь файловой системы
        if (Path.IsPathRooted(thumbnailValue))
            return File.Exists(thumbnailValue);

        return false;
    }

    /// <summary>
    /// Привязывает локальный плейлист к YouTube Music.
    /// 
    /// <para><b>Алгоритм:</b></para>
    /// <list type="number">
    ///   <item>Блокирует навигацию (показывает спиннер)</item>
    ///   <item>Создаёт пустой плейлист в YouTube через API</item>
    ///   <item>Устанавливает <c>YoutubeId</c> и <c>SyncMode=TwoWaySync</c></item>
    ///   <item>Сохраняет плейлист (чтобы YoutubeId был в БД)</item>
    ///   <item>Запускает синхронизацию треков через PlaylistSyncService</item>
    /// </list>
    /// </summary>
    /// <returns><c>true</c> если привязка успешна.</returns>
    private async Task<bool> TryLinkToCloudAsync(
       Playlist playlist,
       string localPlaylistId,
       Action<string> lockNavigation,
       Action unlockNavigation)
    {
        lockNavigation(SL["Playlist_LinkingToCloud"] ?? "Привязка к YouTube Music...");
        try
        {
            var ytId = await _youtube.CreatePlaylistAsync(playlist.Name);

            if (string.IsNullOrEmpty(ytId))
            {
                // БЫЛО: await _dialog.ShowInfoAsync(...)
                // СТАЛО: Toast
                await _notifications.ShowToastAsync(
                    titleKey: "Dialog_Error_Title",
                    messageKey: "Playlist_CloudCreateFailed",
                    severity: NotificationSeverity.Error,
                    durationMs: 4000);

                _notifications.PlayErrorSound();
                return false;
            }

            playlist.YoutubeId = ytId;
            playlist.SyncMode = PlaylistSyncMode.TwoWaySync;

            playlist.UpdatedAt = DateTime.Now;
            await _library.AddOrUpdatePlaylistAsync(playlist);

            Log.Info($"[PlaylistEdit] Привязан к YouTube: {ytId}");

            unlockNavigation();

            // Sync tracks
            var trackIds = await _library.GetPlaylistTrackIdsAsync(localPlaylistId);
            if (trackIds.Count > 0)
            {
                var syncOptions = new PlaylistSyncOptions
                {
                    Strategy = PlaylistSyncStrategy.ReplaceCloud,
                    SyncName = false,
                    SyncDescription = false,
                    SyncThumbnail = false,
                    SyncTracks = true
                };

                var syncResult = await _syncService.SyncDirectAsync(
                    localPlaylistId, syncOptions);

                if (syncResult.Success)
                {
                    Log.Info($"[PlaylistEdit] Loaded {syncResult.TracksAddedToCloud} tracks to YouTube");
                }
                else
                {
                    Log.Warn($"[PlaylistEdit] Track sync after link failed: {syncResult.ErrorMessage}");
                }
            }

            // Успешная привязка — toast
            await _notifications.ShowToastAsync(
                titleKey: "EditPlaylist_Linked",
                messageKey: "EditPlaylist_Linked",
                severity: NotificationSeverity.Success,
                durationMs: 3000);

            NotificationService.PlaySuccessSound();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEdit] Ошибка привязки к облаку: {ex.Message}");

            // БЫЛО: await _dialog.ShowInfoAsync(...)
            // СТАЛО: Toast
            await _notifications.ShowToastAsync(
                titleKey: "Dialog_Error_Title",
                messageKey: "Playlist_CloudLinkFailed",
                messageArgs: [ex.Message],
                severity: NotificationSeverity.Error,
                durationMs: 5000);

            _notifications.PlayErrorSound();

            return false;
        }
        finally
        {
            unlockNavigation();
        }
    }

    /// <summary>
    /// Отвязывает плейлист от YouTube Music.
    /// 
    /// <para><b>Важно:</b> плейлист НЕ удаляется из YouTube-аккаунта,
    /// просто локальная копия перестаёт синхронизироваться.</para>
    /// </summary>
    /// <returns><c>true</c> если пользователь подтвердил отвязку.</returns>
    private async Task<bool> TryUnlinkFromCloudAsync(Playlist playlist)
    {
        var confirm = await _dialog.ConfirmAsync(
            SL["Dialog_Confirm_Title"] ?? "Подтверждение",
            SL["Playlist_UnlinkConfirm"]
                ?? "Отвязать этот плейлист от YouTube Music?\n\n" +
                   "Плейлист останется в вашем аккаунте YouTube, " +
                   "но локальные изменения больше не будут синхронизироваться.",
            SL["Playlist_Unlink"] ?? "Отвязать",
            SL["Button_Cancel"] ?? "Отмена");

        if (!confirm) return false;

        playlist.SyncMode = PlaylistSyncMode.LocalOnly;
        playlist.YoutubeId = null;

        Log.Info($"[PlaylistEdit] Отвязан от YouTube: {playlist.Id}");

        // Успешная отвязка — toast
        await _notifications.ShowToastAsync(
            titleKey: "EditPlaylist_Unlinked",
            messageKey: "EditPlaylist_Unlinked",
            severity: NotificationSeverity.Info,
            durationMs: 2500);

        return true;
    }
}