using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Dialogs;

/// <summary>
/// Режим выбора обложки плейлиста.
/// </summary>
public enum CoverMode
{
    /// <summary>Ручной ввод URL.</summary>
    Url,
    /// <summary>Выбор из обложек треков (мозаика).</summary>
    FromTracks,
    /// <summary>Выбор локального файла.</summary>
    File
}

public sealed class PlaylistEditorViewModel : ViewModelBase
{
    [Reactive] public string Name { get; set; }
    [Reactive] public string? ThumbnailUrl { get; set; }
    [Reactive] public string? CustomColor { get; set; }
    [Reactive] public string? Description { get; set; }

    /// <summary>Исходное описание (для определения изменения).</summary>
    private readonly string? _originalDescription;

    /// <summary>
    /// Оригинальный плейлист (для доступа к YoutubeId и SyncMode).
    /// null для режима создания.
    /// </summary>
    private readonly Playlist? _originalPlaylist;

    /// <summary>true если VM создана для редактирования (а не создания).</summary>
    private readonly bool _isForEdit;

    // ═══ ComputedColor ═══

    /// <summary>
    /// Автоматически вычисленный цвет из обложки (readonly, из БД).
    /// Показывается в UI как информационное поле.
    /// Обновляется при пересчёте через RecalculateColorCommand.
    /// </summary>
    [Reactive] public string? ComputedColor { get; private set; }

    /// <summary>
    /// Кисть превью вычисленного цвета.
    /// </summary>
    [Reactive] public IBrush ComputedColorPreviewBrush { get; private set; } = Brushes.Transparent;

    /// <summary>
    /// Идёт ли пересчёт цвета из обложки или загрузка обложки в YouTube.
    /// Используется для блокировки UI во время длительных операций.
    /// </summary>
    [Reactive] public bool IsRecalculatingColor { get; set; }

    /// <summary>
    /// Команда пересчёта доминантного цвета из текущей обложки.
    /// </summary>
    public ReactiveCommand<Unit, Unit> RecalculateColorCommand { get; }

    // ═══ Cover Mode ═══

    /// <summary>
    /// Текущий режим выбора обложки: URL, из треков, или файл.
    /// </summary>
    [Reactive] public CoverMode SelectedCoverMode { get; set; } = CoverMode.Url;

    /// <summary>true если выбран режим ручного URL.</summary>
    [Reactive] public bool IsCoverModeUrl { get; set; } = true;

    /// <summary>true если выбран режим "Из треков".</summary>
    [Reactive] public bool IsCoverModeFromTracks { get; set; }

    /// <summary>true если выбран режим "Файл".</summary>
    [Reactive] public bool IsCoverModeFile { get; set; }

    /// <summary>
    /// ViewModel выбора обложки из треков. null если треки не предоставлены.
    /// </summary>
    [Reactive] public PlaylistCoverPickerViewModel? CoverPicker { get; set; }

    /// <summary>
    /// Показывать ли переключатель режима обложки.
    /// Всегда true — минимум URL + File.
    /// </summary>
    public bool ShowCoverModeSwitch { get; } = true;

    /// <summary>Показывать ли вкладку "Из треков".</summary>
    public bool HasTracksCoverOption { get; }

    /// <summary>Путь выбранного файла (для отображения в UI).</summary>
    [Reactive] public string? SelectedFilePath { get; set; }

    /// <summary>Команда выбора файла через системный диалог.</summary>
    public ReactiveCommand<Unit, Unit> SelectFileCommand { get; }

    // ═══ Upload Thumbnail to YouTube ═══

    /// <summary>
    /// Показывать ли кнопку загрузки обложки в YouTube.
    /// Видна только для TwoWaySync плейлистов с непустой обложкой.
    /// </summary>
    [Reactive] public bool ShowUploadThumbnailButton { get; private set; }

    /// <summary>Загрузить текущую обложку в YouTube.</summary>
    public ReactiveCommand<Unit, Unit> UploadThumbnailCommand { get; }

    // ═══ Sync ═══

    [Reactive] public bool ShowSyncSection { get; set; }
    [Reactive] public bool IsSyncedToCloud { get; set; }
    public bool IsAuthenticated { get; }
    public bool HasYoutubeBinding { get; }
    public bool OriginalSyncState { get; }

    // ═══ Validation ═══
    [Reactive] public string? ErrorMessage { get; set; }
    [Reactive] public bool HasErrors { get; private set; }
    public IObservable<bool> CanSave { get; }

    // ═══ Preview ═══

    /// <summary>
    /// URL или путь для превью обложки (HTTP URL или локальный путь).
    /// </summary>
    [Reactive] public string? ThumbnailPreviewUrl { get; private set; }

    /// <summary>Есть ли превью для отображения.</summary>
    [Reactive] public bool HasThumbnailPreview { get; private set; }

    /// <summary>Превью — это HTTP URL (для AsyncImageLoader).</summary>
    [Reactive] public bool IsPreviewHttp { get; private set; }

    /// <summary>Превью — это локальный файл (для LocalFileImageConverter).</summary>
    [Reactive] public bool IsPreviewLocal { get; private set; }

    /// <summary>
    /// Bitmap превью для локальных файлов (загружается напрямую).
    /// Для HTTP URL остаётся null — используется AsyncImageLoader.
    /// </summary>
    [Reactive] public Bitmap? LocalPreviewBitmap { get; private set; }

    [Reactive] public IBrush ColorPreviewBrush { get; private set; } = Brushes.Transparent;

    public PlaylistEditorViewModel(
        string name = "",
        string? thumbnailUrl = null,
        string? customColor = null,
        string? description = null,
        string? computedColor = null,
        bool showSync = false,
        bool isSynced = false,
        bool isAuthenticated = false,
        bool hasYoutubeBinding = false,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        Playlist? originalPlaylist = null,
        bool isForEdit = false)
    {
        Name = name;
        ThumbnailUrl = thumbnailUrl;
        CustomColor = customColor;
        Description = description;
        _originalDescription = description;
        _originalPlaylist = originalPlaylist;
        _isForEdit = isForEdit;
        ComputedColor = computedColor;
        ShowSyncSection = showSync;
        IsSyncedToCloud = isSynced;
        OriginalSyncState = isSynced;
        IsAuthenticated = isAuthenticated;
        HasYoutubeBinding = hasYoutubeBinding;

        // Инициализация превью вычисленного цвета
        ComputedColorPreviewBrush = TryParseColor(computedColor);

        // Инициализация CoverPicker если есть треки с обложками
        HasTracksCoverOption = playlistTracks != null && playlistTracks.Any(t => t.HasThumbnail);
        if (HasTracksCoverOption)
        {
            CoverPicker = new PlaylistCoverPickerViewModel(playlistTracks!);

            // Когда мозаика применена — обновляем ThumbnailUrl
            CoverPicker.WhenAnyValue(x => x.ResultPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(path =>
                {
                    ThumbnailUrl = path;
                    SelectedCoverMode = CoverMode.Url;
                })
                .DisposeWith(Disposables);
        }

        // ═══ Команда выбора файла ═══
        SelectFileCommand = CreateCommand(ReactiveCommand.CreateFromTask(SelectFileAsync));

        // ═══ Команда пересчёта цвета из обложки ═══
        var canRecalculate = this.WhenAnyValue(
            x => x.ThumbnailUrl,
            x => x.IsRecalculatingColor,
            (url, isRecalc) => !string.IsNullOrWhiteSpace(url) && !isRecalc);

        RecalculateColorCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(RecalculateColorFromCoverAsync, canRecalculate));

        // ═══ Команда загрузки обложки в YouTube ═══
        var canUpload = this.WhenAnyValue(
            x => x.ThumbnailUrl,
            x => x.IsRecalculatingColor,
            x => x.ShowUploadThumbnailButton,
            (url, busy, show) => !string.IsNullOrEmpty(url) && !busy && show);

        UploadThumbnailCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(UploadThumbnailAsync, canUpload));

        // ═══ Синхронизация CoverMode ↔ Bool свойств ═══
        this.WhenAnyValue(x => x.SelectedCoverMode)
            .Subscribe(mode =>
            {
                IsCoverModeUrl = mode == CoverMode.Url;
                IsCoverModeFromTracks = mode == CoverMode.FromTracks;
                IsCoverModeFile = mode == CoverMode.File;
            })
            .DisposeWith(Disposables);

        // Валидация при изменении любого поля
        this.WhenAnyValue(x => x.Name, x => x.ThumbnailUrl, x => x.CustomColor)
            .Subscribe(_ => UpdateValidation())
            .DisposeWith(Disposables);

        // Кнопка Save активна только если нет ошибок
        CanSave = this.WhenAnyValue(x => x.HasErrors, errors => !errors);

        // ═══ Превью обложки — с поддержкой локальных путей ═══
        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateThumbnailPreview)
            .DisposeWith(Disposables);

        // ═══ Обновление видимости кнопки загрузки обложки ═══
        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Subscribe(_ => UpdateUploadButtonVisibility())
            .DisposeWith(Disposables);

        // Debounce превью цвета
        this.WhenAnyValue(x => x.CustomColor)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(colorStr => ColorPreviewBrush = TryParseColor(colorStr))
            .DisposeWith(Disposables);
    }

    #region Upload Thumbnail to YouTube

    /// <summary>
    /// Обновляет видимость кнопки загрузки обложки в YouTube.
    /// Показываем только для TwoWaySync плейлистов с непустой обложкой.
    /// </summary>
    private void UpdateUploadButtonVisibility()
    {
        ShowUploadThumbnailButton =
            _isForEdit &&
            _originalPlaylist?.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(_originalPlaylist.YoutubeId) &&
            !string.IsNullOrEmpty(ThumbnailUrl);
    }

    /// <summary>
    /// Загружает текущую обложку в YouTube через Scotty Upload Protocol.
    /// Используется для ручной загрузки без синхронизации всего плейлиста.
    /// 
    /// <para><b>Поддерживаемые источники:</b></para>
    /// <list type="bullet">
    ///   <item>HTTP/HTTPS URL — скачивается, затем загружается</item>
    ///   <item>file:// URI — читается как локальный файл</item>
    ///   <item>Абсолютный путь — читается напрямую</item>
    /// </list>
    /// </summary>
    private async Task UploadThumbnailAsync()
    {
        if (_originalPlaylist == null || string.IsNullOrEmpty(_originalPlaylist.YoutubeId))
            return;

        if (string.IsNullOrEmpty(ThumbnailUrl))
        {
            SetError(SL["EditPlaylist_NoThumbnail"] ?? "No thumbnail to upload");
            return;
        }

        IsRecalculatingColor = true; // Переиспользуем для блокировки UI
        ClearError();

        try
        {
            var youtube = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<YoutubeProvider>(Program.Services);

            byte[] imageData;
            string mimeType = "image/jpeg";

            // ═══ Скачиваем/читаем изображение ═══
            if (IsHttpUrl(ThumbnailUrl))
            {
                // HTTP URL — скачиваем
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(15);

                var response = await httpClient.GetAsync(ThumbnailUrl);
                response.EnsureSuccessStatusCode();

                imageData = await response.Content.ReadAsByteArrayAsync();

                // Определяем MIME-тип из Content-Type
                if (response.Content.Headers.ContentType?.MediaType is { } contentType)
                    mimeType = contentType;
            }
            else if (ThumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                // file:// URI
                var uri = new Uri(ThumbnailUrl);
                var localPath = uri.LocalPath;

                if (!File.Exists(localPath))
                {
                    SetError(SL["EditPlaylist_FileNotFound"] ?? "File not found");
                    return;
                }

                imageData = await File.ReadAllBytesAsync(localPath);
                mimeType = GetMimeTypeFromExtension(Path.GetExtension(localPath));
            }
            else if (Path.IsPathRooted(ThumbnailUrl) && File.Exists(ThumbnailUrl))
            {
                // Абсолютный путь к файлу
                imageData = await File.ReadAllBytesAsync(ThumbnailUrl);
                mimeType = GetMimeTypeFromExtension(Path.GetExtension(ThumbnailUrl));
            }
            else
            {
                SetError(SL["EditPlaylist_InvalidThumbnailUrl"] ?? "Invalid thumbnail URL");
                return;
            }

            // ═══ Валидация размера ═══
            if (imageData.Length == 0)
            {
                SetError(SL["EditPlaylist_EmptyImage"] ?? "Image file is empty");
                return;
            }

            if (imageData.Length > 20 * 1024 * 1024) // 20MB
            {
                SetError(SL["PlaylistSync_ThumbnailTooLarge"] ?? "Image too large (max 20MB)");
                return;
            }

            // ═══ Загружаем через Scotty Upload Protocol ═══
            var success = await youtube.UploadPlaylistThumbnailAsync(
                _originalPlaylist.YoutubeId, imageData, mimeType);

            if (success)
            {
                Log.Info($"[PlaylistEditor] Thumbnail uploaded for {_originalPlaylist.YoutubeId}");
                // Показываем временное сообщение об успехе
                ErrorMessage = "✓ " + (SL["PlaylistSync_ThumbnailUploaded"] ?? "Thumbnail uploaded to YouTube");
                HasErrors = false; // Не блокируем кнопку Save

                // Очищаем сообщение через 3 секунды
                _ = ClearSuccessMessageAsync();
            }
            else
            {
                SetError(SL["EditPlaylist_UploadFailed"] ?? "Failed to upload thumbnail");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Thumbnail upload error: {ex.Message}");
            SetError(ex.Message);
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    /// <summary>
    /// Очищает сообщение об успехе через 3 секунды.
    /// </summary>
    private async Task ClearSuccessMessageAsync()
    {
        try
        {
            await Task.Delay(3000);
            // Очищаем только если это наше сообщение об успехе
            if (ErrorMessage?.StartsWith("✓") == true)
            {
                ErrorMessage = null;
            }
        }
        catch { /* ignore */ }
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
        ".bmp" => "image/bmp",
        _ => "image/jpeg" // Fallback
    };

    #endregion

    #region Recalculate Color

    /// <summary>
    /// Пересчитывает доминантный цвет из текущей обложки.
    /// Результат записывается в ComputedColor (будет сохранён при Apply).
    /// </summary>
    private async Task RecalculateColorFromCoverAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        if (!IsValidUri(ThumbnailUrl)) return;

        IsRecalculatingColor = true;
        try
        {
            var dominantColorService = Microsoft.Extensions.DependencyInjection
                .ServiceProviderServiceExtensions
                .GetRequiredService<DominantColorService>(Program.Services);

            var color = await dominantColorService.GetDominantColorAsync(ThumbnailUrl, ct);

            if (color.HasValue)
            {
                var hex = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
                ComputedColor = hex;
                ComputedColorPreviewBrush = new SolidColorBrush(color.Value);
                Log.Info($"[PlaylistEditor] Recalculated color: {hex}");
            }
            else
            {
                Log.Warn("[PlaylistEditor] Could not extract dominant color");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Color recalculation failed: {ex.Message}");
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    #endregion

    #region Thumbnail Preview

    /// <summary>
    /// Определяет, является ли URL HTTP/HTTPS ссылкой.
    /// </summary>
    private static bool IsHttpUrl(string url) =>
        url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Преобразует file:// URI или абсолютный путь в локальный путь.
    /// </summary>
    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }

        if (Path.IsPathRooted(url))
            return url;

        return null;
    }

    /// <summary>
    /// Обновляет превью обложки с автоматическим определением типа источника.
    /// HTTP/HTTPS/avares → AsyncImageLoader, локальный файл → Bitmap напрямую.
    /// </summary>
    private void UpdateThumbnailPreview(string? url)
    {
        var oldBitmap = LocalPreviewBitmap;

        if (string.IsNullOrWhiteSpace(url))
        {
            HasThumbnailPreview = false;
            IsPreviewHttp = false;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = null;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        // ═══ HTTP/HTTPS → AsyncImageLoader ═══
        if (IsHttpUrl(url))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        // ═══ avares:// → AsyncImageLoader ═══
        if (url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        // ═══ Локальный файл ═══
        var localPath = ResolveLocalPath(url);
        if (localPath != null && File.Exists(localPath))
        {
            try
            {
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bitmap = new Bitmap(stream);

                HasThumbnailPreview = true;
                IsPreviewHttp = false;
                IsPreviewLocal = true;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = bitmap;
                oldBitmap?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"[PlaylistEditor] Failed to load local preview '{localPath}': {ex.Message}");
                HasThumbnailPreview = false;
                IsPreviewHttp = false;
                IsPreviewLocal = false;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = null;
                oldBitmap?.Dispose();
            }
            return;
        }

        // ═══ Нераспознанный источник ═══
        HasThumbnailPreview = false;
        IsPreviewHttp = false;
        IsPreviewLocal = false;
        ThumbnailPreviewUrl = null;
        LocalPreviewBitmap = null;
        oldBitmap?.Dispose();
    }

    #endregion

    #region File Selection

    /// <summary>
    /// Открывает системный диалог выбора файла изображения.
    /// </summary>
    private async Task SelectFileAsync(CancellationToken ct)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null)
        {
            Log.Warn("[PlaylistEditor] Cannot open file picker: no TopLevel found");
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = SL["CoverPicker_SelectFile"] ?? "Выберите изображение",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(SL["CoverPicker_ImageFiles"] ?? "Изображения")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"]
                }
            ]
        });

        if (files.Count == 0) return;

        var filePath = files[0].Path.LocalPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        SelectedFilePath = filePath;
        ThumbnailUrl = filePath;

        // Немедленное обновление превью (без ожидания debounce)
        UpdateThumbnailPreview(filePath);
    }

    private static TopLevel? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                if (window.IsActive) return window;
            }
            return desktop.MainWindow;
        }
        return null;
    }

    #endregion

    #region Validation

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            SetError(SL["Error_EmptyName"] ?? "Name cannot be empty");
            return;
        }

        if (!string.IsNullOrWhiteSpace(ThumbnailUrl)
            && !IsValidUri(ThumbnailUrl))
        {
            SetError(SL["Error_InvalidUrl"] ?? "Invalid cover URL format");
            return;
        }

        if (!string.IsNullOrWhiteSpace(CustomColor) && !IsValidColor(CustomColor))
        {
            SetError(SL["Error_InvalidColor"] ?? "Invalid HEX color (example: #FF5555)");
            return;
        }

        ClearError();
    }

    private void SetError(string msg) { ErrorMessage = msg; HasErrors = true; }
    private void ClearError() { ErrorMessage = null; HasErrors = false; }

    #endregion

    #region Helpers

    /// <summary>
    /// Проверяет, является ли строка валидным источником изображения.
    /// 
    /// <para><b>Допустимые форматы:</b></para>
    /// <list type="bullet">
    ///   <item><c>http://</c> / <c>https://</c> — URL из интернета</item>
    ///   <item><c>avares://</c> — встроенный ресурс Avalonia</item>
    ///   <item><c>file://</c> — явный file URI</item>
    ///   <item>Абсолютный путь файловой системы (C:\..., /home/...)</item>
    /// </list>
    /// 
    /// <para><b>ВАЖНО:</b> <c>Uri.TryCreate</c> с <c>UriKind.Absolute</c> парсит
    /// hex-строки вида "F1A2B3..." как scheme "f" с хостом "1a2b3...:80",
    /// что приводит к HTTP-запросам на несуществующие хосты.
    /// Поэтому проверяем scheme по белому списку.</para>
    /// </summary>
    internal static bool IsValidUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Локальный абсолютный путь — проверяем ДО Uri.TryCreate,
        // т.к. "C:\path" парсится как URI со scheme "c"
        if (Path.IsPathRooted(url))
            return true;

        // URI с проверкой scheme по белому списку
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme switch
            {
                "http" or "https" => true,
                "avares" => true,
                "file" => true,
                _ => false
            };
        }

        return false;
    }

    private static bool IsValidColor(string? colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr)) return false;
        try { Color.Parse(colorStr); return true; }
        catch { return false; }
    }

    private static IBrush TryParseColor(string? colorStr)
    {
        if (IsValidColor(colorStr))
            return new SolidColorBrush(Color.Parse(colorStr!));
        return Brushes.Transparent;
    }

    #endregion

    #region Cover Mode Commands

    public void SetCoverModeUrl() => SelectedCoverMode = CoverMode.Url;
    public void SetCoverModeFromTracks() => SelectedCoverMode = CoverMode.FromTracks;
    public void SetCoverModeFile() => SelectedCoverMode = CoverMode.File;

    #endregion

    #region Factory Methods

    public static PlaylistEditorViewModel ForCreate() =>
        new(name: "", thumbnailUrl: null, customColor: null, description: null,
            computedColor: null,
            showSync: false, isSynced: false,
            isAuthenticated: false, hasYoutubeBinding: false,
            playlistTracks: null,
            originalPlaylist: null,
            isForEdit: false);

    public static PlaylistEditorViewModel ForEdit(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null) =>
        new(name: playlist.Name,
            thumbnailUrl: playlist.ThumbnailUrl,
            customColor: playlist.CustomColor,
            description: playlist.Description,
            computedColor: playlist.ComputedColor,
            showSync: isAuthenticated || playlist.IsFromAccount,
            isSynced: playlist.IsFromAccount,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: !string.IsNullOrEmpty(playlist.YoutubeId),
            playlistTracks: playlistTracks,
            originalPlaylist: playlist,
            isForEdit: true);

    #endregion

    #region Result

    /// <summary>
    /// Собирает результат редактирования.
    /// Description включается всегда — PlaylistEditService сам определит, изменилось ли.
    /// ComputedColor включается если был пересчитан.
    /// </summary>
    public EditPlaylistResult ToResult() => new()
    {
        Name = Name.Trim(),
        ThumbnailUrl = string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : ThumbnailUrl.Trim(),
        CustomColor = string.IsNullOrWhiteSpace(CustomColor) ? null : CustomColor.Trim(),
        Description = Description?.Trim(),
        ComputedColor = ComputedColor
    };

    public bool SyncStateChanged => IsSyncedToCloud != OriginalSyncState;
    public bool DescriptionChanged => !string.Equals(Description?.Trim(), _originalDescription?.Trim(), StringComparison.Ordinal);

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalPreviewBitmap?.Dispose();
            CoverPicker?.Dispose();
        }
        base.Dispose(disposing);
    }
}