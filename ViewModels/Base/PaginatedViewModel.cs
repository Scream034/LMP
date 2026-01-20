// ViewModels/Base/PaginatedViewModel.cs
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;

namespace MyLiteMusicPlayer.ViewModels;

/// <summary>
/// Базовый класс для ViewModel с пагинированным списком и поддержкой кэширования.
/// </summary>
public abstract class PaginatedViewModel<TSource, TViewModel> : ViewModelBase
    where TViewModel : class
{
    private readonly List<TSource> _allItems = [];
    private readonly HashSet<string> _loadedIds = [];
    private int _displayedCount;
    private CancellationTokenSource? _loadCts;

    // ─── Конфигурация (переопределяемая) ───
    protected virtual int BatchSize => 30;
    protected virtual int LoadDelayMs => 200;
    protected virtual int PrefetchThreshold => 15; // Когда начинать подгружать ещё

    // ─── Reactive Properties ───
    [Reactive] public bool IsLoading { get; protected set; }
    [Reactive] public bool IsLoadingMore { get; protected set; }
    [Reactive] public bool IsFetchingFromNetwork { get; protected set; } // Загрузка из интернета
    [Reactive] public bool HasMoreItems { get; protected set; }
    [Reactive] public bool ReachedEnd { get; protected set; } // Достигли конца списка

    /// <summary>Отображаемый список ViewModel элементов</summary>
    public ObservableCollection<TViewModel> Items { get; } = [];

    /// <summary>Команда загрузки следующей порции</summary>
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
    }

    // ─── Protected API ───

    protected int TotalCount => _allItems.Count;
    protected int DisplayedCount => _displayedCount;
    protected IReadOnlyList<TSource> AllItems => _allItems;
    protected CancellationToken LoadCancellationToken => _loadCts?.Token ?? CancellationToken.None;

    /// <summary>Преобразование исходного элемента в ViewModel</summary>
    protected abstract TViewModel CreateItemViewModel(TSource item);

    /// <summary>Получить уникальный ID элемента (для дедупликации)</summary>
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    /// <summary>Вызывается когда нужно подгрузить ещё данные из сети</summary>
    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    /// <summary>Вызывается после создания ViewModel</summary>
    protected virtual void OnItemCreated(TSource source, TViewModel viewModel) { }

    /// <summary>Инициализирует список новыми данными</summary>
    protected async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        CancelLoading();
        _loadCts = new CancellationTokenSource();

        Items.Clear();
        _allItems.Clear();
        _loadedIds.Clear();
        _displayedCount = 0;
        ReachedEnd = false;

        foreach (var item in items)
        {
            var id = GetItemId(item);
            if (!_loadedIds.Contains(id))
            {
                _allItems.Add(item);
                _loadedIds.Add(id);
            }
        }

        HasMoreItems = _allItems.Count > 0 || canFetchMore;

        if (_allItems.Count > 0)
        {
            await LoadNextBatchAsync(skipDelay: true);
        }
    }

    /// <summary>Добавляет элементы к существующему списку (для prefetch)</summary>
    protected void AppendItems(IEnumerable<TSource> newItems)
    {
        int added = 0;
        foreach (var item in newItems)
        {
            var id = GetItemId(item);
            if (!_loadedIds.Contains(id))
            {
                _allItems.Add(item);
                _loadedIds.Add(id);
                added++;
            }
        }

        if (added > 0)
        {
            HasMoreItems = true;
            ReachedEnd = false;
            Log.Info($"Appended {added} new items, total: {_allItems.Count}");
        }
    }

    /// <summary>Полная очистка</summary>
    protected void ClearItems()
    {
        CancelLoading();
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

    private async Task LoadNextBatchAsync(bool skipDelay = false)
    {
        // Если показали всё из кэша - пробуем подгрузить из сети
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
            // Задержка для плавности (показать скелеты)
            if (!skipDelay && _displayedCount > 0 && LoadDelayMs > 0)
            {
                await Task.Delay(LoadDelayMs, _loadCts?.Token ?? CancellationToken.None);
            }

            var batch = _allItems
                .Skip(_displayedCount)
                .Take(BatchSize)
                .ToList();

            foreach (var item in batch)
            {
                var vm = CreateItemViewModel(item);
                OnItemCreated(item, vm);
                Items.Add(vm);
            }

            _displayedCount += batch.Count;

            int remaining = _allItems.Count - _displayedCount;
            HasMoreItems = remaining > 0;

            Log.Info($"Displayed {_displayedCount}/{_allItems.Count}, remaining: {remaining}");

            // Prefetch если осталось мало
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

            if (newItems.Count > 0)
            {
                AppendItems(newItems);
            }
            else
            {
                Log.Info($"No more items from network");
            }
        }
        catch (OperationCanceledException)
        {
            // Игнорируем
        }
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