using Avalonia.Collections;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

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

    protected abstract TViewModel CreateItemViewModel(TSource item);
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    protected virtual void OnItemCreated(TSource source, TViewModel viewModel) { }

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

        if (items != null)
        {
            AppendSourceItems(items);
        }

        HasMoreItems = _allItems.Count > 0 || canFetchMore;

        if (_allItems.Count > 0)
        {
            await LoadNextBatchAsync(skipDelay: true);
        }
    }

    protected void AppendItems(IEnumerable<TSource> newItems)
    {
        int added = AppendSourceItems(newItems);

        if (added > 0)
        {
            HasMoreItems = true;
            ReachedEnd = false;
            Log.Info($"Appended {added} new items, total: {_allItems.Count}");
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
    }

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
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

    private async Task LoadNextBatchAsync(bool skipDelay = false)
    {
        if (_displayedCount >= _allItems.Count)
        {
            await TryFetchMoreAsync();

            if (_displayedCount >= _allItems.Count)
            {
                HasMoreItems = false;
                ReachedEnd = true;
                Log.Info($"Reached end of list");
                return;
            }
        }

        IsLoadingMore = true;

        try
        {
            if (!skipDelay && _displayedCount > 0 && LoadDelayMs > 0)
            {
                await Task.Delay(LoadDelayMs, _loadCts?.Token ?? CancellationToken.None);
            }

            // Optimization: GetRange is faster than LINQ Skip/Take
            int countToTake = Math.Min(BatchSize, _allItems.Count - _displayedCount);
            if (countToTake <= 0) return;

            var batchSource = _allItems.GetRange(_displayedCount, countToTake);
            var batchVMs = new List<TViewModel>(countToTake);

            foreach (var item in batchSource)
            {
                var vm = CreateItemViewModel(item);
                OnItemCreated(item, vm);
                batchVMs.Add(vm);
            }

            // Optimization: AddRange sends a single NotifyCollectionChanged event
            Items.AddRange(batchVMs);
            _displayedCount += batchVMs.Count;

            int remaining = _allItems.Count - _displayedCount;
            HasMoreItems = remaining > 0;

            Log.Info($"Displayed {_displayedCount}/{_allItems.Count}, remaining: {remaining}");

            if (remaining < PrefetchThreshold && remaining > 0)
            {
                _ = TryFetchMoreAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"Load cancelled");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task TryFetchMoreAsync()
    {
        if (IsFetchingFromNetwork) return;

        IsFetchingFromNetwork = true;
        Log.Info($"Fetching more from network...");

        try
        {
            var newItems = await FetchMoreFromNetworkAsync(_loadCts?.Token ?? CancellationToken.None);

            if (newItems != null && newItems.Count > 0)
            {
                AppendItems(newItems);
            }
            else
            {
                Log.Info($"No more items from network");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info($"Fetch error: {ex.Message}");
        }
        finally
        {
            IsFetchingFromNetwork = false;
        }
    }
}