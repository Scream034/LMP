using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;

namespace LMP.UI.Features.Home;

/// <summary>
/// ViewModel главного экрана. Категории + поиск через YouTube с кэшированием.
/// </summary>
public sealed class HomeViewModel : TrackListReorderableViewModel
{
    #region Constants

    private const int DefaultFetchSize = 100;

    #endregion

    #region Fields

    private readonly YoutubeProvider _youtube;
    private readonly SearchCacheService _searchCache;
    private readonly ImageCacheService _imageCache;
    private readonly EventHandler<string> _languageChangedHandler;

    private string _currentQuery = "";
    private CancellationTokenSource? _categoryCts;
    private bool _isDisposed;
    private bool _isDataLoaded; // Признак того, что данные уже лежат в кэше ОЗУ

    #endregion

    #region Properties

    [Reactive] public string Greeting { get; private set; } = string.Empty;
    [Reactive] public CategoryItem? SelectedCategory { get; set; }

    public ObservableCollection<CategoryItem> Categories { get; } = [];

    public bool CanReorderItems => CanReorder;

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<(int oldIndex, int newIndex), Unit> MoveItemCommand { get; }

    #endregion

    #region Constructor

    public HomeViewModel(
        YoutubeProvider youtube,
        SearchCacheService searchCache,
        ImageCacheService imageCache,
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
        : base(audio, downloads, vmFactory)
    {
        _youtube = youtube;
        _searchCache = searchCache;
        _imageCache = imageCache;

        UpdateGreeting();

        _languageChangedHandler = (_, _) =>
        {
            UpdateGreeting();
            InitializeCategories();
        };
        LocalizationService.Instance.LanguageChanged += _languageChangedHandler;

        InitializeCategories();

        RefreshCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(async () => await LoadTracksAsync(force: true)));

        MoveItemCommand = CreateCommand(
            ReactiveCommand.CreateFromTask<(int oldIndex, int newIndex)>(
                async tuple => await MoveItemAsync(tuple.oldIndex, tuple.newIndex)));

        this.WhenAnyValue(x => x.FilterQuery)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(CanReorderItems)))
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.SelectedCategory)
            .WhereNotNull()
            .Skip(1)
            .Where(_ => !IsLoading) // Использовать IsLoading базового класса
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await LoadTracksAsync())
            .DisposeWith(Disposables);
    }

    #endregion

    #region Navigation

    public override async Task OnNavigatedToAsync()
    {
        if (_isDisposed) return;

        // Если данные уже в памяти, загрузку не запускаем — базовый класс 
        // автоматически переключит IsLoading в false по окончании перехода.
        if (!_isDataLoaded)
        {
            await LoadTracksAsync();
            _isDataLoaded = true;
        }

        await base.OnNavigatedToAsync(); // Запуск базового перехватчика
    }

    #endregion

    #region TrackListReorderableViewModel Implementation

    protected override void OnPlay(TrackInfo track)
    {
        Task.Run(async () => await Audio.PlayTrackAsync(track));
        _ = LibService.AddToRecentlyPlayedAsync(track);
    }

    protected override async Task<List<TrackInfo>> LoadTracksAsync(
        IEnumerable<string> ids, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentQuery)) return [];

        var cached = await _searchCache.GetAsync(_currentQuery, SearchSource.YouTube, 30);
        if (cached is { Count: > 0 })
        {
            var idSet = ids.ToHashSet();
            return [.. cached.Where(t => idSet.Contains(t.Id))];
        }

        return [];
    }

    /// <inheritdoc />
    protected override void OnAccountChanged()
    {
        base.OnAccountChanged();
        _isDataLoaded = false;

        // Если страница главного экрана активна прямо сейчас, принудительно запускаем обновление
        if (ViewModelBase.CurrentSuspendLevel == SuspendLevel.None)
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () => await LoadTracksAsync(force: true), DispatcherPriority.Background);
        }
    }

    #endregion

    #region Private Methods

    private async Task LoadTracksAsync(bool force = false)
    {
        var category = SelectedCategory;
        if (category is null) return;

        _categoryCts?.Cancel();
        _categoryCts?.Dispose();
        _categoryCts = new CancellationTokenSource();
        var ct = _categoryCts.Token;

        IsLoading = true;

        try
        {
            await Task.Delay(50, ct);

            if (category.IsSpecial)
            {
                var recent = await LibService.GetRecentlyPlayedAsync(DefaultFetchSize);
                if (ct.IsCancellationRequested) return;
                InitializeWithData(recent);
            }
            else
            {
                _currentQuery = category.Query;

                var cached = !force
                    ? await _searchCache.GetAsync(_currentQuery, SearchSource.YouTube, 30)
                    : null;

                List<TrackInfo> tracks;

                if (cached is { Count: > 0 })
                {
                    tracks = cached;
                    _ = RefreshCacheInBackgroundAsync(ct);
                }
                else
                {
                    tracks = await _youtube.SearchAsync(_currentQuery, DefaultFetchSize);
                    if (ct.IsCancellationRequested) return;

                    if (tracks.Count > 0)
                        _ = _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, tracks);
                }

                var imageUrls = tracks.Take(20)
                    .Select(static t => t.ThumbnailUrl)
                    .Where(static u => !string.IsNullOrEmpty(u));
                _ = _imageCache.PrefetchAsync(imageUrls!, ct);

                if (ct.IsCancellationRequested) return;
                InitializeWithData(tracks);
            }

            _ = HydrateCacheStatusAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[HomeVM] Load error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshCacheInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(3000, ct);
            var fresh = await _youtube.SearchAsync(_currentQuery, DefaultFetchSize);
            if (ct.IsCancellationRequested || fresh.Count == 0) return;
            await _searchCache.SetAsync(_currentQuery, SearchSource.YouTube, fresh);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[HomeVM] Background cache refresh failed: {ex.Message}");
        }
    }

    private void UpdateGreeting()
    {
        var key = DateTime.Now.Hour switch
        {
            >= 0 and < 5 => "Home_Greeting_Night",
            >= 5 and < 12 => "Home_Greeting_Morning",
            >= 12 and < 18 => "Home_Greeting_Afternoon",
            _ => "Home_Greeting_Evening"
        };
        Greeting = SL[key];
    }

    private void InitializeCategories()
    {
        ReadOnlySpan<(string key, string fallback, string query, bool special)> defs =
        [
            ("Category_RecentlyPlayed", "Recently Played", "",                         true),
            ("Category_Trending",       "Trending",        "trending music 2025",      false),
            ("Category_Pop",            "Pop",             "pop hits 2025",            false),
            ("Category_HipHop",         "Hip-Hop",         "hip hop 2025",             false),
            ("Category_Electronic",     "Electronic",      "electronic music",         false),
            ("Category_LoFi",           "Lo-Fi",           "lofi hip hop chill beats", false),
            ("Category_Rock",           "Rock",            "rock music",               false),
        ];

        for (int i = Categories.Count; i < defs.Length; i++)
            Categories.Add(new CategoryItem());

        for (int i = 0; i < defs.Length; i++)
        {
            var (key, fallback, query, special) = defs[i];
            var name = SL[key];
            if (string.IsNullOrEmpty(name) || name == key) name = fallback;

            var cat = Categories[i];
            cat.Name = name;
            cat.Query = query;
            cat.IsSpecial = special;
            cat.LocKey = key;
        }

        while (Categories.Count > defs.Length)
            Categories.RemoveAt(Categories.Count - 1);

        SelectedCategory ??= Categories.FirstOrDefault();
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug("[HomeVM] Disposing");
            LocalizationService.Instance.LanguageChanged -= _languageChangedHandler;
            _categoryCts?.Cancel();
            _categoryCts?.Dispose();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}

public sealed class CategoryItem : ReactiveObject
{
    [Reactive] public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public bool IsSpecial { get; set; }
    public string LocKey { get; set; } = "";
}