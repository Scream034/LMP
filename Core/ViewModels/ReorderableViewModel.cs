using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.ViewModels;

public abstract class ReorderableViewModel<TSource, TViewModel> : ViewModelBase, IFilterable
    where TViewModel : class, IDisposable
    where TSource : notnull
{
    #region Fields

    protected readonly LibraryService LibService;

    private List<string> _masterIds = [];
    private readonly Dictionary<string, TSource> _loadedSources = [];
    private readonly Dictionary<string, TViewModel> _vmCache = [];
    private readonly ObservableCollection<TViewModel> _visibleItems = [];

    private CancellationTokenSource? _loadCts;
    private int _loadedCount;
    private bool _isDisposed;
    private string _filterQuery = string.Empty;

    #endregion

    #region Properties

    protected virtual int BatchSize => LibService.Settings.LoadBatchSize > 0
        ? LibService.Settings.LoadBatchSize
        : 30;

    [Reactive] public bool IsLoading { get; protected set; }
    [Reactive] public bool IsLoadingMore { get; protected set; }
    [Reactive] public bool HasMoreItems { get; protected set; }
    [Reactive] public bool ReachedEnd { get; protected set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }

    public string FilterQuery
    {
        get => _filterQuery;
        set
        {
            if (_filterQuery == value) return;
            this.RaiseAndSetIfChanged(ref _filterQuery, value);
            RebuildVisibleItems();
        }
    }

    public ObservableCollection<TViewModel> Items => _visibleItems;

    protected int TotalCount => _masterIds.Count;
    protected int LoadedCount => _loadedCount;

    public bool CanReorder => string.IsNullOrWhiteSpace(_filterQuery);

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    #endregion

    #region Constructor

    protected ReorderableViewModel()
    {
        LibService = Program.Services.GetRequiredService<LibraryService>();
        EnableSmoothLoading = LibService.Settings.EnableSmoothLoading;

        var canLoadMore = this.WhenAnyValue(
            x => x.IsLoadingMore,
            x => x.IsLoading,
            x => x.HasMoreItems,
            (more, init, hasMore) => !more && !init && hasMore);

        // FIX: Используем CreateCommand из базового класса
        LoadMoreCommand = CreateCommand(ReactiveCommand.CreateFromTask(LoadNextBatchAsync, canLoadMore));
    }

    #endregion

    #region Abstract Methods

    protected abstract string GetItemId(TSource item);
    protected abstract TViewModel CreateViewModel(TSource item);
    protected abstract bool MatchesFilter(TSource item, string query);
    protected abstract Task<List<TSource>> LoadItemsByIdsAsync(IEnumerable<string> ids, CancellationToken ct);
    protected virtual Task SaveMoveAsync(int fromIndex, int toIndex, CancellationToken ct) => Task.CompletedTask;

    #endregion

    #region Initialization

    protected async Task InitializeAsync(List<string> allIds, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        CancelLoading();
        _loadCts = new CancellationTokenSource();

        _masterIds = [.. allIds];
        _loadedSources.Clear();
        _loadedCount = 0;

        foreach (var vm in _vmCache.Values)
            vm.Dispose();
        _vmCache.Clear();
        _visibleItems.Clear();

        UpdateState();

        if (_masterIds.Count > 0)
        {
            await LoadNextBatchAsync();
        }
    }

    protected void InitializeWithData(IEnumerable<TSource> items)
    {
        if (_isDisposed) return;

        CancelLoading();

        var itemsList = items.ToList();
        _masterIds = itemsList.Select(GetItemId).ToList();

        _loadedSources.Clear();
        foreach (var item in itemsList)
        {
            _loadedSources[GetItemId(item)] = item;
        }
        _loadedCount = itemsList.Count;

        foreach (var vm in _vmCache.Values)
            vm.Dispose();
        _vmCache.Clear();
        _visibleItems.Clear();

        RebuildVisibleItems();
        UpdateState();
    }

    #endregion

    #region Loading

    private async Task LoadNextBatchAsync()
    {
        if (_isDisposed || IsLoadingMore || _loadedCount >= _masterIds.Count)
            return;

        IsLoadingMore = true;

        try
        {
            var ct = _loadCts?.Token ?? CancellationToken.None;

            var idsToLoad = _masterIds
                .Skip(_loadedCount)
                .Take(BatchSize)
                .Where(id => !_loadedSources.ContainsKey(id))
                .ToList();

            if (idsToLoad.Count == 0)
            {
                _loadedCount = _masterIds.Count;
                UpdateState();
                return;
            }

            var loaded = await LoadItemsByIdsAsync(idsToLoad, ct);
            if (ct.IsCancellationRequested) return;

            foreach (var item in loaded)
            {
                var id = GetItemId(item);
                _loadedSources[id] = item;
            }

            _loadedCount += BatchSize;
            if (_loadedCount > _masterIds.Count)
                _loadedCount = _masterIds.Count;

            AppendNewItemsToVisible(loaded);
            UpdateState();
        }
        catch (Exception ex)
        {
            Log.Error($"[Reorderable] Load error: {ex.Message}");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private void AppendNewItemsToVisible(IEnumerable<TSource> newItems)
    {
        var query = _filterQuery;
        foreach (var item in newItems)
        {
            if (!MatchesFilter(item, query)) continue;

            var id = GetItemId(item);
            if (_vmCache.ContainsKey(id)) continue;

            var vm = CreateViewModel(item);
            _vmCache[id] = vm;

            int insertIndex = FindInsertIndex(id);
            if (insertIndex >= 0 && insertIndex <= _visibleItems.Count)
                _visibleItems.Insert(insertIndex, vm);
            else
                _visibleItems.Add(vm);
        }
    }

    private int FindInsertIndex(string itemId)
    {
        int masterIndex = _masterIds.IndexOf(itemId);
        if (masterIndex < 0) return _visibleItems.Count;

        for (int i = 0; i < _visibleItems.Count; i++)
        {
            var existingId = GetVmId(_visibleItems[i]);
            int existingMasterIndex = _masterIds.IndexOf(existingId);

            if (existingMasterIndex > masterIndex)
                return i;
        }

        return _visibleItems.Count;
    }

    private string GetVmId(TViewModel vm)
    {
        foreach (var kvp in _vmCache)
        {
            if (ReferenceEquals(kvp.Value, vm))
                return kvp.Key;
        }
        return "";
    }

    #endregion

    #region Filtering

    protected void RebuildVisibleItems()
    {
        var query = _filterQuery;
        var newVisible = new List<TViewModel>();

        foreach (var id in _masterIds)
        {
            if (!_loadedSources.TryGetValue(id, out var source))
                continue;

            if (!MatchesFilter(source, query))
                continue;

            if (!_vmCache.TryGetValue(id, out var vm))
            {
                vm = CreateViewModel(source);
                _vmCache[id] = vm;
            }

            newVisible.Add(vm);
        }

        SyncVisibleItems(newVisible);
    }

    private void SyncVisibleItems(List<TViewModel> newItems)
    {
        while (_visibleItems.Count > newItems.Count)
            _visibleItems.RemoveAt(_visibleItems.Count - 1);

        for (int i = 0; i < newItems.Count; i++)
        {
            if (i < _visibleItems.Count)
            {
                if (!ReferenceEquals(_visibleItems[i], newItems[i]))
                    _visibleItems[i] = newItems[i];
            }
            else
            {
                _visibleItems.Add(newItems[i]);
            }
        }
    }

    #endregion

    #region Reordering

    public void MoveItem(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex) return;
        if (oldIndex < 0 || oldIndex >= _visibleItems.Count) return;
        if (newIndex < 0 || newIndex >= _visibleItems.Count) return;

        if (!CanReorder)
        {
            Log.Warn("[Move] Cannot reorder with active filter");
            return;
        }

        var movingVm = _visibleItems[oldIndex];
        var movingId = GetVmId(movingVm);
        if (string.IsNullOrEmpty(movingId)) return;

        Log.Info($"[Move] {oldIndex} → {newIndex}");

        _masterIds.RemoveAt(oldIndex);
        _masterIds.Insert(newIndex, movingId);

        _visibleItems.Move(oldIndex, newIndex);
    }

    public async Task MoveItemAsync(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex) return;
        if (oldIndex < 0 || oldIndex >= _visibleItems.Count) return;
        if (newIndex < 0 || newIndex >= _visibleItems.Count) return;

        if (!CanReorder)
        {
            Log.Warn("[Move] Cannot reorder with active filter");
            return;
        }

        var movingVm = _visibleItems[oldIndex];
        var movingId = GetVmId(movingVm);
        if (string.IsNullOrEmpty(movingId)) return;

        Log.Info($"[Move] {oldIndex} → {newIndex}");

        _masterIds.RemoveAt(oldIndex);
        _masterIds.Insert(newIndex, movingId);
        _visibleItems.Move(oldIndex, newIndex);

        try
        {
            await SaveMoveAsync(oldIndex, newIndex, CancellationToken.None);
            Log.Info("[Move] Saved to DB");
        }
        catch (Exception ex)
        {
            Log.Error($"[Move] Save failed: {ex.Message}");
            _masterIds.RemoveAt(newIndex);
            _masterIds.Insert(oldIndex, movingId);
            _visibleItems.Move(newIndex, oldIndex);
        }
    }

    #endregion

    #region Helpers

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        IsLoading = false;
        IsLoadingMore = false;
    }

    private void UpdateState()
    {
        HasMoreItems = _loadedCount < _masterIds.Count;
        ReachedEnd = !HasMoreItems && _masterIds.Count > 0;
    }

    protected List<TSource> GetLoadedItemsSnapshot()
    {
        return _masterIds
            .Where(_loadedSources.ContainsKey)
            .Select(id => _loadedSources[id])
            .ToList();
    }

    protected List<string> GetAllIds() => [.. _masterIds];

    #endregion

    #region IDisposable - ИСПРАВЛЕНО: override вместо new

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        
        if (disposing)
        {
            Log.Debug($"[ReorderableVM] Disposing, cleaning {_vmCache.Count} cached VMs");
            
            CancelLoading();

            // КРИТИЧНО: Диспозим все закешированные VM
            foreach (var vm in _vmCache.Values)
            {
                vm.Dispose();
            }

            _vmCache.Clear();
            _visibleItems.Clear();
            _loadedSources.Clear();
            _masterIds.Clear();
        }
        
        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}