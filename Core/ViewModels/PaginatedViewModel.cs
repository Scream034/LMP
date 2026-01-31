// Core/ViewModels/PaginatedViewModel.cs
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Models;
using LMP.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.ViewModels;

public abstract class PaginatedViewModel<TSource, TViewModel> : ViewModelBase, IDisposable, IFilterable
    where TViewModel : IDisposable
    where TSource : notnull
{
    #region Fields

    protected readonly LibraryService LibService;

    private readonly SourceList<TSource> _sourceList = new();
    private readonly ReadOnlyObservableCollection<TViewModel> _items;
    private readonly CompositeDisposable _cleanUp = [];

    private int _consecutiveEmptyLoads = 0;
    private const int MaxConsecutiveEmptyLoads = 5;

    private CancellationTokenSource? _loadCts;
    private bool _canFetchMore;
    private bool _isDisposed;
    private int _totalSourceCount;

    private string _filterQuery = string.Empty;
    private ContentFilterType _filterType = ContentFilterType.All;

    #endregion

    #region Properties

    // Changed: Access Settings property instead of Data
    protected virtual int BatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize
        : 20;
    protected virtual int PrefetchThreshold => 10;

    [Reactive] public bool IsLoading { get; protected set; }
    [Reactive] public bool IsLoadingMore { get; protected set; }
    [Reactive] public bool IsFetchingFromNetwork { get; protected set; }
    [Reactive] public bool HasMoreItems { get; protected set; }
    [Reactive] public bool ReachedEnd { get; protected set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }

    public string FilterQuery
    {
        get => _filterQuery;
        set => this.RaiseAndSetIfChanged(ref _filterQuery, value);
    }

    public ContentFilterType FilterType
    {
        get => _filterType;
        set => this.RaiseAndSetIfChanged(ref _filterType, value);
    }

    public ReadOnlyObservableCollection<TViewModel> Items => _items;
    protected int TotalCount => _totalSourceCount;

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<string, Unit> SetFilterTypeCommand { get; }

    #endregion

    #region Constructor

    protected PaginatedViewModel()
    {
        LibService = Program.Services.GetRequiredService<LibraryService>();
        // Changed: Access Settings property
        EnableSmoothLoading = LibService.Settings.EnableSmoothLoading;

        SetFilterTypeCommand = ReactiveCommand.Create<string>(typeStr =>
        {
            if (Enum.TryParse<ContentFilterType>(typeStr, true, out var result))
            {
                FilterType = result;
            }
        });

        var filterPredicate = this.WhenAnyValue(x => x.FilterQuery, x => x.FilterType)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Select(tuple => BuildFilterPredicate(tuple.Item1, tuple.Item2))
            .StartWith(BuildFilterPredicate(_filterQuery, _filterType));

        _sourceList.Connect()
            .Filter(filterPredicate)
            .Transform(CreateItemViewModel)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _items)
            .DisposeMany()
            .Subscribe()
            .DisposeWith(_cleanUp);

        var canLoadMore = this.WhenAnyValue(
            x => x.IsLoadingMore,
            x => x.IsLoading,
            x => x.IsFetchingFromNetwork,
            x => x.HasMoreItems,
            (more, init, net, hasMore) => !more && !init && !net && hasMore);

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async _ => await LoadNextBatchAsync(), canLoadMore);

        _sourceList.Connect()
            .Filter(filterPredicate)
            .Count()
            .Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(visibleCount =>
            {
                // Если после фильтрации осталось мало элементов и есть ещё данные
                if (visibleCount < PrefetchThreshold && HasMoreItems && !IsLoading && !IsLoadingMore && !IsFetchingFromNetwork)
                {
                    if (_consecutiveEmptyLoads < MaxConsecutiveEmptyLoads)
                    {
                        Log.Debug($"[Paginated] Auto-loading more (visible: {visibleCount}, threshold: {PrefetchThreshold})");
                        _ = LoadNextBatchAsync();
                    }
                }
            })
            .DisposeWith(_cleanUp);

        this.WhenAnyValue(x => x.FilterQuery, x => x.FilterType)
            .Subscribe(_ => _consecutiveEmptyLoads = 0)
            .DisposeWith(_cleanUp);
    }

    #endregion

    #region Abstract Methods

    protected abstract TViewModel CreateItemViewModel(TSource item);
    protected abstract bool FilterItem(TSource item, string query, ContentFilterType filterType);
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    #endregion

    #region Private Helpers

    private Func<TSource, bool> BuildFilterPredicate(string query, ContentFilterType filterType)
    {
        return item => FilterItem(item, query, filterType);
    }

    #endregion

    #region Public Methods

    protected void MoveSourceItem(int oldIndex, int newIndex)
    {
        _sourceList.Edit(list =>
        {
            if (oldIndex >= 0 && oldIndex < list.Count &&
                newIndex >= 0 && newIndex < list.Count)
            {
                list.Move(oldIndex, newIndex);
            }
        });
    }

    protected async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        if (_isDisposed) return;

        CancelLoading();
        _loadCts = new CancellationTokenSource();
        _canFetchMore = canFetchMore;
        _consecutiveEmptyLoads = 0;

        var itemsList = items?.ToList() ?? [];

        _sourceList.Edit(innerList =>
        {
            innerList.Clear();
            innerList.AddRange(itemsList);
        });

        _totalSourceCount = _sourceList.Count;
        UpdateState();

        if (_totalSourceCount == 0 && canFetchMore)
        {
            await LoadNextBatchAsync();
        }
    }

    protected void ClearItems()
    {
        _sourceList.Clear();
        _totalSourceCount = 0;
        _canFetchMore = false;
        UpdateState();
    }

    protected List<TSource> GetItemsSnapshot() => [.. _sourceList.Items];
    protected List<string> GetLoadedItemsIds() => [.. _sourceList.Items.Select(GetItemId)];

    #endregion

    #region Loading Logic

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        IsLoading = false;
        IsLoadingMore = false;
        IsFetchingFromNetwork = false;
    }

    private void UpdateState()
    {
        HasMoreItems = _canFetchMore;
        ReachedEnd = !_canFetchMore && _totalSourceCount > 0;
    }

    protected void SetCanFetchMore(bool value)
    {
        _canFetchMore = value;
        UpdateState();
    }

    private async Task LoadNextBatchAsync()
    {
        if (_isDisposed || IsLoadingMore || IsFetchingFromNetwork || !_canFetchMore) return;

        IsLoadingMore = true;
        IsFetchingFromNetwork = true;

        int visibleBefore = _items.Count;

        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;
            var newItems = await FetchMoreFromNetworkAsync(token);

            if (token.IsCancellationRequested || _isDisposed) return;

            if (newItems != null && newItems.Count > 0)
            {
                _sourceList.Edit(list =>
                {
                    var existingIds = list.Select(GetItemId).ToHashSet();
                    foreach (var item in newItems)
                    {
                        if (!existingIds.Contains(GetItemId(item)))
                        {
                            list.Add(item);
                            _totalSourceCount++;
                        }
                    }
                });

                int visibleAfter = _items.Count;
                if (visibleAfter == visibleBefore) _consecutiveEmptyLoads++;
                else _consecutiveEmptyLoads = 0;
            }
            else
            {
                _canFetchMore = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Paginated] Load Error: {ex.Message}");
            _canFetchMore = false;
        }
        finally
        {
            IsLoadingMore = false;
            IsFetchingFromNetwork = false;
            UpdateState();
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            CancelLoading();
            _cleanUp.Dispose();
            _sourceList.Dispose();
        }
        _isDisposed = true;
    }

    #endregion
}