using Avalonia.Collections;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.Core.ViewModels;

/// <summary>
/// Base class for paginated lists with caching support.
/// Optimized for Avalonia using AvaloniaList and range operations.
/// </summary>
public abstract class PaginatedViewModel<TSource, TViewModel> : ViewModelBase
    where TViewModel : class
{
    public readonly LibraryService LibService;

    private readonly List<TSource> _allItems = [];
    private readonly HashSet<string> _loadedIds = [];
    private int _displayedCount;
    private CancellationTokenSource? _loadCts;
    
    /// <summary>
    /// Флаг: можно ли ещё запрашивать данные из сети
    /// </summary>
    private bool _canFetchMore;

    // ─── Configuration ───
    protected virtual int BatchSize => LibService.Data.LoadBatchSize > 0 ? LibService.Data.LoadBatchSize : 20;
    protected virtual int LoadDelayMs => 200;
    protected virtual int PrefetchThreshold => 15;

    // ─── Reactive Properties ───
    [Reactive] public bool IsLoading { get; protected set; }
    [Reactive] public bool IsLoadingMore { get; protected set; }
    [Reactive] public bool IsFetchingFromNetwork { get; protected set; }
    [Reactive] public bool HasMoreItems { get; protected set; }
    [Reactive] public bool ReachedEnd { get; protected set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }

    /// <summary>
    /// AvaloniaList optimized for UI notifications.
    /// </summary>
    public AvaloniaList<TViewModel> Items { get; } = [];

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    protected PaginatedViewModel()
    {
        var canLoadMore = this.WhenAnyValue(
            x => x.IsLoadingMore,
            x => x.HasMoreItems,
            x => x.IsLoading,
            x => x.IsFetchingFromNetwork,
            (loading, hasMore, initial, fetching) => !loading && !initial && !fetching && hasMore);

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async _ => await LoadNextBatchAsync(), canLoadMore);
        LibService = Program.Services.GetRequiredService<LibraryService>();
        EnableSmoothLoading = LibService.Data.EnableSmoothLoading;
        LibService.OnDataChanged += () => EnableSmoothLoading = LibService.Data.EnableSmoothLoading;
    }

    // ─── Protected API ───

    protected int TotalCount => _allItems.Count;
    protected int DisplayedCount => _displayedCount;
    protected IReadOnlyList<TSource> AllItems => _allItems;
    protected CancellationToken LoadCancellationToken => _loadCts?.Token ?? CancellationToken.None;
    
    /// <summary>
    /// Можно ли ещё запрашивать данные из сети
    /// </summary>
    protected bool CanFetchMore => _canFetchMore;

    protected abstract TViewModel CreateItemViewModel(TSource item);
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    /// <summary>
    /// Переопределите для загрузки данных из сети при скролле
    /// </summary>
    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    protected virtual void OnItemCreated(TSource source, TViewModel viewModel) { }

    /// <summary>
    /// Инициализация списка
    /// </summary>
    /// <param name="items">Начальные элементы</param>
    /// <param name="canFetchMore">Можно ли загружать ещё из сети</param>
    protected async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        CancelLoading();
        _loadCts = new CancellationTokenSource();

        foreach (var oldItem in Items)
        {
            if (oldItem is IDisposable d) d.Dispose();
        }

        Items.Clear();
        _allItems.Clear();
        _loadedIds.Clear();
        _displayedCount = 0;
        ReachedEnd = false;
        _canFetchMore = canFetchMore;

        if (items != null)
        {
            AppendSourceItems(items);
        }

        // HasMoreItems = true если есть что показать ИЛИ можно загрузить ещё
        HasMoreItems = _allItems.Count > 0 || _canFetchMore;

        Log.Info($"[Paginated] Initialized with {_allItems.Count} items, canFetchMore: {_canFetchMore}");

        if (_allItems.Count > 0)
        {
            await LoadNextBatchAsync(skipDelay: true);
        }
    }

    /// <summary>
    /// Добавляет новые элементы в буфер (НЕ в UI!)
    /// Используется при потоковой загрузке
    /// </summary>
    protected void AppendToBuffer(IEnumerable<TSource> newItems)
    {
        int added = AppendSourceItems(newItems);

        if (added > 0)
        {
            HasMoreItems = true;
            ReachedEnd = false;
            Log.Info($"[Paginated] Added {added} to buffer, total: {_allItems.Count}, displayed: {_displayedCount}");
        }
    }

    /// <summary>
    /// Добавляет новые элементы и сразу показывает их в UI
    /// (для потокового поиска где нужно мгновенное отображение)
    /// </summary>
    protected void AppendAndDisplayItems(IEnumerable<TSource> newItems)
    {
        int added = AppendSourceItems(newItems);

        if (added > 0)
        {
            HasMoreItems = true;
            ReachedEnd = false;

            // Сразу создаем VM для новых элементов
            var startIndex = _displayedCount;
            var itemsToAdd = _allItems.Skip(startIndex).Take(added).ToList();

            var batchVMs = new List<TViewModel>(added);
            foreach (var item in itemsToAdd)
            {
                var vm = CreateItemViewModel(item);
                OnItemCreated(item, vm);
                batchVMs.Add(vm);
            }

            Items.AddRange(batchVMs);
            _displayedCount += batchVMs.Count;

            Log.Info($"[Paginated] Appended and displayed {added} items, total: {_displayedCount}/{_allItems.Count}");
        }
    }

    protected void ClearItems()
    {
        CancelLoading();

        foreach (var item in Items)
        {
            if (item is IDisposable d) d.Dispose();
        }

        Items.Clear();
        _allItems.Clear();
        _loadedIds.Clear();
        _displayedCount = 0;
        HasMoreItems = false;
        ReachedEnd = false;
        _canFetchMore = false;
    }

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }
    
    /// <summary>
    /// Устанавливает флаг возможности загрузки из сети
    /// </summary>
    protected void SetCanFetchMore(bool value)
    {
        _canFetchMore = value;
        UpdateHasMoreItems();
    }

    // ─── Private ───

    private int AppendSourceItems(IEnumerable<TSource> items)
    {
        int count = 0;
        foreach (var item in items)
        {
            var id = GetItemId(item);
            if (!_loadedIds.Contains(id))
            {
                _allItems.Add(item);
                _loadedIds.Add(id);
                count++;
            }
        }
        return count;
    }
    
    private void UpdateHasMoreItems()
    {
        int remaining = _allItems.Count - _displayedCount;
        HasMoreItems = remaining > 0 || _canFetchMore;
    }

    private async Task LoadNextBatchAsync(bool skipDelay = false)
    {
        int bufferRemaining = _allItems.Count - _displayedCount;
        
        Log.Info($"[Paginated] LoadNextBatch: displayed={_displayedCount}, total={_allItems.Count}, bufferRemaining={bufferRemaining}, canFetch={_canFetchMore}");

        // Если буфер пуст — пробуем загрузить из сети
        if (bufferRemaining <= 0)
        {
            if (_canFetchMore)
            {
                await TryFetchMoreAsync();
                bufferRemaining = _allItems.Count - _displayedCount;
            }

            // Если после загрузки всё ещё пусто — конец
            if (bufferRemaining <= 0 && !_canFetchMore)
            {
                HasMoreItems = false;
                ReachedEnd = true;
                Log.Info($"[Paginated] Reached end of list (no more data)");
                return;
            }
        }

        // Если после попытки загрузки буфер всё ещё пуст
        if (bufferRemaining <= 0)
        {
            Log.Info($"[Paginated] Buffer still empty after fetch attempt");
            return;
        }

        IsLoadingMore = true;

        try
        {
            if (!skipDelay && _displayedCount > 0 && LoadDelayMs > 0)
            {
                await Task.Delay(LoadDelayMs, _loadCts?.Token ?? CancellationToken.None);
            }

            int countToTake = Math.Min(BatchSize, _allItems.Count - _displayedCount);
            if (countToTake <= 0)
            {
                UpdateHasMoreItems();
                return;
            }

            var batchSource = _allItems.GetRange(_displayedCount, countToTake);
            var batchVMs = new List<TViewModel>(countToTake);

            foreach (var item in batchSource)
            {
                var vm = CreateItemViewModel(item);
                OnItemCreated(item, vm);
                batchVMs.Add(vm);
            }

            Items.AddRange(batchVMs);
            _displayedCount += batchVMs.Count;

            int remaining = _allItems.Count - _displayedCount;
            
            // ВАЖНО: HasMoreItems = true если есть что показать ИЛИ можно загрузить
            HasMoreItems = remaining > 0 || _canFetchMore;
            
            // ReachedEnd только когда буфер пуст И нельзя загрузить
            if (remaining == 0 && !_canFetchMore)
            {
                ReachedEnd = true;
            }

            Log.Info($"[Paginated] Displayed {_displayedCount}/{_allItems.Count}, remaining: {remaining}, canFetch: {_canFetchMore}, hasMore: {HasMoreItems}");

            // Prefetch: если осталось мало — подгружаем заранее
            if (remaining < PrefetchThreshold && remaining >= 0 && _canFetchMore)
            {
                Log.Info($"[Paginated] Prefetching (remaining {remaining} < threshold {PrefetchThreshold})");
                _ = TryFetchMoreAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[Paginated] Load cancelled");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task TryFetchMoreAsync()
    {
        if (IsFetchingFromNetwork || !_canFetchMore) return;

        IsFetchingFromNetwork = true;
        Log.Info($"[Paginated] Fetching more from network...");

        try
        {
            var ct = _loadCts?.Token ?? CancellationToken.None;
            var newItems = await FetchMoreFromNetworkAsync(ct);

            if (newItems != null && newItems.Count > 0)
            {
                // Добавляем в буфер, НЕ сразу в UI
                AppendToBuffer(newItems);
                Log.Info($"[Paginated] Fetched {newItems.Count} items from network");
            }
            else
            {
                // Сеть вернула пустой список — больше грузить нечего
                _canFetchMore = false;
                UpdateHasMoreItems();
                Log.Info($"[Paginated] Network returned empty, canFetchMore set to false");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[Paginated] Fetch cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"[Paginated] Fetch error: {ex.Message}");
        }
        finally
        {
            IsFetchingFromNetwork = false;
        }
    }
}

