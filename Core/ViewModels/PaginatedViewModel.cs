using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Collections;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Core.ViewModels;

public abstract class PaginatedViewModel<TSource, TViewModel> : ViewModelBase, IDisposable
    where TViewModel : class
{
    #region Fields

    protected readonly LibraryService LibService;

    // [FIX] Объект синхронизации для защиты _allItems
    private readonly Lock _syncRoot = new();

    private readonly List<TSource> _allItems = [];
    private readonly HashSet<string> _loadedIds = [];
    
    private int _displayedCount;
    private CancellationTokenSource? _loadCts;
    private bool _canFetchMore;
    private bool _isDisposed;
    private Guid _loadGeneration = Guid.NewGuid();

    #endregion

    #region Properties

    protected virtual int BatchSize => LibService.Data.LoadBatchSize > 0 ? LibService.Data.LoadBatchSize : 20;
    protected virtual int LoadDelayMs => 200;
    protected virtual int PrefetchThreshold => 15;

    [Reactive] public bool IsLoading { get; protected set; }
    [Reactive] public bool IsLoadingMore { get; protected set; }
    [Reactive] public bool IsFetchingFromNetwork { get; protected set; }
    [Reactive] public bool HasMoreItems { get; protected set; }
    [Reactive] public bool ReachedEnd { get; protected set; }
    [Reactive] public bool EnableSmoothLoading { get; set; }

    public AvaloniaList<TViewModel> Items { get; } = [];

    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    // [FIX] Доступ к счетчику через lock не обязателен, если чтение атомарно, но для порядка используем lock внутри методов
    protected int TotalCount { get { lock(_syncRoot) return _allItems.Count; } }
    protected int DisplayedCount => _displayedCount;
    
    // [FIX] Внимание: прямой доступ к AllItems небезопасен из других потоков. 
    // Используйте GetLoadedItems() или GetSnapshot().
    protected IReadOnlyList<TSource> AllItems => _allItems; 

    protected CancellationToken LoadCancellationToken => _loadCts?.Token ?? CancellationToken.None;

    #endregion

    #region Constructors

    protected PaginatedViewModel()
    {
        LibService = Program.Services.GetRequiredService<LibraryService>();
        EnableSmoothLoading = LibService.Data.EnableSmoothLoading;
        
        LibService.OnDataChanged += OnLibraryDataChanged;

        var canLoadMore = this.WhenAnyValue(
            x => x.IsLoadingMore,
            x => x.HasMoreItems,
            x => x.IsLoading,
            x => x.IsFetchingFromNetwork,
            (loading, hasMore, initial, fetching) => !loading && !initial && !fetching && hasMore);

        LoadMoreCommand = ReactiveCommand.CreateFromTask(async _ => await LoadNextBatchAsync(), canLoadMore);
    }

    #endregion

    #region Abstract Methods

    protected abstract TViewModel CreateItemViewModel(TSource item);
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";
    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct) 
        => Task.FromResult(new List<TSource>());
    protected virtual void OnItemCreated(TSource source, TViewModel viewModel) { }

    #endregion

    #region Public Methods

    protected async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        if (_isDisposed) return;

        // [FIX] Очистка выполняется в UI потоке для безопасности Items
        await Dispatcher.UIThread.InvokeAsync(ClearItemsInternal);

        _loadCts = new CancellationTokenSource();
        _canFetchMore = canFetchMore;

        if (items != null)
        {
            lock (_syncRoot)
            {
                AppendSourceItems(items);
            }
        }

        UpdateHasMoreItems();
        
        Log.Info($"[Paginated] Initialized with {TotalCount} items.");

        if (TotalCount > 0)
        {
            await LoadNextBatchAsync(skipDelay: true);
        }
    }

    protected void ClearItems()
    {
        // [FIX] Оборачиваем в Dispatcher, чтобы гарантировать поток UI для Items.Clear
        if (Dispatcher.UIThread.CheckAccess())
            ClearItemsInternal();
        else
            Dispatcher.UIThread.Post(ClearItemsInternal);
    }

    // [FIX] Метод для безопасного получения списка ID (для передачи в SearchSession)
    protected List<string> GetLoadedItemsIds()
    {
        lock (_syncRoot)
        {
            return _allItems.Select(GetItemId).ToList();
        }
    }

    #endregion

    #region Internal Logic

    private void ClearItemsInternal()
    {
        _loadGeneration = Guid.NewGuid();
        CancelLoading();

        // Items.Clear() должен вызываться в UI потоке (гарантируется вызовом выше)
        var itemsDisposeCopy = Items.ToList(); 
        Items.Clear(); 
        
        foreach (var item in itemsDisposeCopy)
        {
            if (item is IDisposable d) d.Dispose();
        }
        
        lock (_syncRoot)
        {
            _allItems.Clear();
            _loadedIds.Clear();
        }
        
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

    protected void SetCanFetchMore(bool value)
    {
        _canFetchMore = value;
        UpdateHasMoreItems();
    }

    private void OnLibraryDataChanged()
    {
        if (_isDisposed) return;
        EnableSmoothLoading = LibService.Data.EnableSmoothLoading;
    }

    // Должен вызываться под lock(_syncRoot)
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
    
    protected void AppendToBuffer(IEnumerable<TSource> newItems)
    {
        if (_isDisposed) return;
        
        int added;
        lock (_syncRoot)
        {
            added = AppendSourceItems(newItems);
        }

        if (added > 0)
        {
            HasMoreItems = true;
            ReachedEnd = false;
        }
    }

    private void UpdateHasMoreItems()
    {
        int total;
        lock (_syncRoot) total = _allItems.Count;
        
        int remaining = total - _displayedCount;
        HasMoreItems = remaining > 0 || _canFetchMore;
    }

    private async Task LoadNextBatchAsync(bool skipDelay = false)
    {
        if (_isDisposed) return;

        var currentGen = _loadGeneration;
        int total;
        lock (_syncRoot) total = _allItems.Count;

        int bufferRemaining = total - _displayedCount;

        if (bufferRemaining <= 0)
        {
            if (_canFetchMore)
            {
                await TryFetchMoreAsync(currentGen);
                if (_loadGeneration != currentGen || _isDisposed) return;
                
                lock (_syncRoot) total = _allItems.Count;
                bufferRemaining = total - _displayedCount;
            }

            if (bufferRemaining <= 0 && !_canFetchMore)
            {
                HasMoreItems = false;
                ReachedEnd = true;
                return;
            }
        }

        if (bufferRemaining <= 0) return;

        IsLoadingMore = true;

        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;

            if (!skipDelay && _displayedCount > 0 && LoadDelayMs > 0)
                await Task.Delay(LoadDelayMs, token);

            if (_isDisposed || token.IsCancellationRequested || _loadGeneration != currentGen) return;

            // [FIX] Безопасное чтение данных под локом
            List<TSource> batchSource;
            lock (_syncRoot)
            {
                // Повторная проверка внутри лока
                total = _allItems.Count;
                int countToTake = Math.Min(BatchSize, total - _displayedCount);
                if (countToTake <= 0) return;
                batchSource = _allItems.GetRange(_displayedCount, countToTake);
            }

            var batchVMs = new List<TViewModel>(batchSource.Count);
            foreach (var item in batchSource)
            {
                var vm = CreateItemViewModel(item);
                OnItemCreated(item, vm);
                batchVMs.Add(vm);
            }

            // [FIX] Обновление UI
            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                if (!_isDisposed && _loadGeneration == currentGen)
                {
                    Items.AddRange(batchVMs);
                    _displayedCount += batchVMs.Count;
                }
                else
                {
                    foreach(var vm in batchVMs) 
                        if(vm is IDisposable d) d.Dispose();
                }
            });

            if (_loadGeneration != currentGen) return;

            lock (_syncRoot) total = _allItems.Count;
            int remaining = total - _displayedCount;
            HasMoreItems = remaining > 0 || _canFetchMore;

            if (remaining == 0 && !_canFetchMore) ReachedEnd = true;

            if (remaining < PrefetchThreshold && remaining >= 0 && _canFetchMore)
                _ = TryFetchMoreAsync(currentGen);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_loadGeneration == currentGen)
                IsLoadingMore = false;
        }
    }

    private async Task TryFetchMoreAsync(Guid expectedGen)
    {
        if (IsFetchingFromNetwork || !_canFetchMore || _isDisposed) return;

        IsFetchingFromNetwork = true;
        try
        {
            var ct = _loadCts?.Token ?? CancellationToken.None;
            var newItems = await FetchMoreFromNetworkAsync(ct);

            if (_isDisposed || ct.IsCancellationRequested || _loadGeneration != expectedGen) return;

            if (newItems != null && newItems.Count > 0)
            {
                AppendToBuffer(newItems);
            }
            else
            {
                _canFetchMore = false;
                UpdateHasMoreItems();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[Paginated] Fetch error: {ex.Message}");
        }
        finally
        {
            if (_loadGeneration == expectedGen)
                IsFetchingFromNetwork = false;
        }
    }

    #endregion

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            LibService.OnDataChanged -= OnLibraryDataChanged;
            
            // Запускаем очистку синхронно, но безопасно, т.к. это Dispose
            CancelLoading();
            
            // Важно очистить VM, даже если мы не в UI потоке
            // Копируем Items в новый лист для безопасного перебора
            var itemsToDispose = new List<TViewModel>();
            try 
            {
                // Попытка доступа к Items может быть небезопасной не из UI потока,
                // но при Dispose ViewModel уже обычно отсоединена от View.
                // Если мы здесь, значит View уже не должна рендерить это.
                if (Dispatcher.UIThread.CheckAccess())
                {
                    itemsToDispose.AddRange(Items);
                    Items.Clear();
                }
                else
                {
                    // Если мы в другом потоке, постим в UI и ждем (или просто забиваем на Items.Clear, но диспозим VM)
                    // Лучше просто забить на Items.Clear() (View сама отпишется), но вызвать Dispose у элементов.
                    // Однако доступ к Items не из UI потока - риск.
                    // Решение: Dispatcher.UIThread.Post для очистки Items, а сами элементы не достаем.
                    // Но нам НУЖНО их задиспозить.
                    // КОМПРОМИСС: Используем _allItems или предполагаем, что Items доступен (AvaloniaList не совсем thread-safe).
                    // Правильный путь:
                    Dispatcher.UIThread.Post(() => 
                    {
                        var copy = Items.ToList();
                        Items.Clear();
                        foreach(var i in copy) if (i is IDisposable d) d.Dispose();
                    });
                }
            }
            catch { } // Игнорируем ошибки доступа при уничтожении

            lock (_syncRoot)
            {
                _allItems.Clear();
                _loadedIds.Clear();
            }
        }

        _isDisposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}