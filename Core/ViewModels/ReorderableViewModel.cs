using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using LMP.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.Core.ViewModels;

/// <summary>
/// Базовый ViewModel для списков треков с виртуализацией, фильтрацией и перетаскиванием.
/// 
/// Пагинация намеренно удалена: VirtualizingStackPanel создаёт только ~15-20 контейнеров
/// независимо от размера коллекции, поэтому загрузка всех данных сразу дешевле и проще
/// чем батчинг с InfiniteScroll.
/// </summary>
public abstract class ReorderableViewModel<TSource, TViewModel> : ViewModelBase, IFilterable
    where TViewModel : class, IDisposable
    where TSource : notnull
{
    #region Fields

    protected readonly LibraryService LibService;

    // Мастер-список всех ID в правильном порядке (source of truth для порядка).
    private List<string> _masterIds = [];
    private readonly Dictionary<string, TSource> _sources = [];
    private readonly Dictionary<string, TViewModel> _vmCache = [];

    /// <summary>
    /// Обратный индекс VM → ID для O(1) поиска идентификатора по экземпляру ViewModel.
    /// Без него <see cref="GetVmId"/> требовал O(n) обход всего _vmCache.
    /// Поддерживается атомарно вместе с _vmCache.
    /// </summary>
    private readonly Dictionary<TViewModel, string> _vmToId =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Переиспользуемый буфер для <see cref="RebuildVisibleItems"/>.
    /// Избегает аллокации List на каждый вызов фильтра (горячий путь при наборе текста).
    /// </summary>
    private List<TViewModel> _rebuildBuffer = [];

    /// <summary>
    /// Переиспользуемый Dictionary для SyncVisibleItems.
    /// Избегает аллокации на каждый rebuild (горячий путь при скролле + фильтре).
    /// Очищается через Clear() — capacity сохраняется для следующего вызова.
    /// </summary>
    private readonly Dictionary<TViewModel, int> _syncPositions =
        new(ReferenceEqualityComparer.Instance);

    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;

    #endregion

    #region Properties

    [Reactive] public bool IsLoading { get; protected set; }

    public string FilterQuery
    {
        get;
        set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            RebuildVisibleItems();
        }
    } = string.Empty;

    public ObservableCollection<TViewModel> Items { get; } = [];

    protected int TotalCount => _masterIds.Count;

    /// <summary>
    /// Сортировка и перетаскивание доступны только при отсутствии фильтра.
    /// С фильтром индексы видимых элементов не совпадают с мастер-списком.
    /// </summary>
    public bool CanReorder => string.IsNullOrWhiteSpace(FilterQuery);

    #endregion

    #region Constructor

    protected ReorderableViewModel()
    {
        LibService = Program.Services.GetRequiredService<LibraryService>();
    }

    #endregion

    #region Abstract Methods

    protected abstract string GetItemId(TSource item);
    protected abstract TViewModel CreateViewModel(TSource item);
    protected abstract bool MatchesFilter(TSource item, string query);

    /// <summary>
    /// Загрузка данных по списку ID. Реализуется в наследнике (БД, сеть и т.д.).
    /// </summary>
    protected abstract Task<List<TSource>> LoadItemsByIdsAsync(IEnumerable<string> ids, CancellationToken ct);

    protected virtual Task SaveMoveAsync(int fromIndex, int toIndex, CancellationToken ct) => Task.CompletedTask;

    #endregion

    #region Initialization

    /// <summary>
    /// Инициализация по списку ID: загружает все данные одним запросом, затем строит Items.
    /// Skeleton-first: IsLoading управляется снаружи до вызова этого метода.
    /// </summary>
    protected async Task InitializeAsync(List<string> allIds, CancellationToken ct = default)
    {
        if (_isDisposed) return;

        CancelLoading();
        _loadCts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token, ct).Token;

        _masterIds = [.. allIds];
        _sources.Clear();

        DisposeAndClearVmCache();
        Items.Clear();

        if (_masterIds.Count == 0) return;

        try
        {
            var loaded = await LoadItemsByIdsAsync(_masterIds, linkedCt);
            if (linkedCt.IsCancellationRequested) return;

            foreach (var item in loaded)
                _sources[GetItemId(item)] = item;

            RebuildVisibleItems();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"[ReorderableVM] Load error: {ex.Message}");
        }
    }

    /// <summary>
    /// Инициализация с уже загруженными данными (например, из кэша).
    /// </summary>
    protected void InitializeWithData(IEnumerable<TSource> items)
    {
        if (_isDisposed) return;

        CancelLoading();

        var list = items.ToList();
        _masterIds = [.. list.Select(GetItemId)];

        _sources.Clear();
        foreach (var item in list)
            _sources[GetItemId(item)] = item;

        DisposeAndClearVmCache();
        Items.Clear();

        RebuildVisibleItems();
    }

    #endregion

    #region Filtering

    protected void RebuildVisibleItems()
    {
        var query = FilterQuery;

        _rebuildBuffer.Clear();
        if (_rebuildBuffer.Capacity < _masterIds.Count)
            _rebuildBuffer.Capacity = _masterIds.Count;

        foreach (var id in _masterIds)
        {
            if (!_sources.TryGetValue(id, out var source)) continue;
            if (!MatchesFilter(source, query)) continue;

            if (!_vmCache.TryGetValue(id, out var vm))
            {
                vm = CreateViewModel(source);
                _vmCache[id] = vm;
                _vmToId[vm] = id;
            }

            _rebuildBuffer.Add(vm);
        }

        SyncVisibleItems(_rebuildBuffer);
    }

    /// <summary>
    /// In-place синхронизация Items с новым списком без Replace-операций.
    ///
    /// <para><b>Критично для ItemsRepeater:</b> замена элемента по индексу
    /// (<c>Items[i] = x</c>) эмитирует <c>Replace</c> в <c>ObservableCollection</c>,
    /// что вызывает переработку anchor-элемента в <c>ScrollContentPresenter</c>
    /// и приводит к фризам/прыжкам скролла (Avalonia issue #7593, PR #16347).</para>
    ///
    /// <para><b>Стратегия:</b> только Add/Remove/Move — никаких Replace.
    /// Move не меняет anchor candidates, Remove/Add — изолированные операции.</para>
    ///
    /// <para><b>O(n) вместо O(n²):</b> используем Dictionary с ReferenceEquality
    /// для поиска существующих элементов за O(1) вместо линейного сканирования.</para>
    /// </summary>
    private void SyncVisibleItems(List<TViewModel> newItems)
    {
        while (Items.Count > newItems.Count)
            Items.RemoveAt(Items.Count - 1);

        // Переиспользуем _syncPositions вместо new Dictionary на каждый вызов.
        _syncPositions.Clear();
        for (int i = 0; i < Items.Count; i++)
            _syncPositions[Items[i]] = i;

        for (int i = 0; i < newItems.Count; i++)
        {
            if (i < Items.Count)
            {
                if (ReferenceEquals(Items[i], newItems[i])) continue;

                if (_syncPositions.TryGetValue(newItems[i], out int existingAt)
                    && existingAt > i
                    && existingAt < Items.Count
                    && ReferenceEquals(Items[existingAt], newItems[i]))
                {
                    Items.Move(existingAt, i);
                    _syncPositions[newItems[i]] = i;
                    for (int k = i + 1; k <= existingAt && k < Items.Count; k++)
                        _syncPositions[Items[k]] = k;
                }
                else
                {
                    Items.Insert(i, newItems[i]);
                    _syncPositions[newItems[i]] = i;
                    for (int k = i + 1; k < Items.Count; k++)
                        _syncPositions[Items[k]] = k;
                }
            }
            else
            {
                Items.Add(newItems[i]);
                _syncPositions[newItems[i]] = i;
            }
        }
    }

    #endregion

    #region Reordering

    public void MoveItem(int oldIndex, int newIndex)
    {
        if (!ValidateMove(oldIndex, newIndex)) return;

        var movingId = GetVmId(Items[oldIndex]);
        if (string.IsNullOrEmpty(movingId)) return;

        // Синхронизируем мастер-список и видимый список атомарно
        int masterOld = _masterIds.IndexOf(movingId);
        int masterNew = GetMasterIndexForVisualTarget(newIndex, oldIndex);

        if (masterOld >= 0 && masterNew >= 0 && masterOld != masterNew)
        {
            _masterIds.RemoveAt(masterOld);
            _masterIds.Insert(masterNew, movingId);
        }

        Items.Move(oldIndex, newIndex);
    }

    public async Task MoveItemAsync(int oldIndex, int newIndex)
    {
        if (!ValidateMove(oldIndex, newIndex)) return;

        var movingId = GetVmId(Items[oldIndex]);
        if (string.IsNullOrEmpty(movingId)) return;

        int masterOld = _masterIds.IndexOf(movingId);

        _masterIds.RemoveAt(oldIndex);
        _masterIds.Insert(newIndex, movingId);
        Items.Move(oldIndex, newIndex);

        try
        {
            await SaveMoveAsync(masterOld, newIndex, CancellationToken.None);
            Log.Info("[Reorderable] Move saved to DB");
        }
        catch (Exception ex)
        {
            Log.Error($"[Reorderable] Save move failed: {ex.Message}");

            // Rollback при ошибке
            _masterIds.RemoveAt(newIndex);
            _masterIds.Insert(oldIndex, movingId);
            Items.Move(newIndex, oldIndex);
        }
    }

    private bool ValidateMove(int oldIndex, int newIndex)
    {
        if (!CanReorder)
        {
            Log.Warn("[Reorderable] Cannot reorder with active filter");
            return false;
        }

        return oldIndex != newIndex
            && oldIndex >= 0 && oldIndex < Items.Count
            && newIndex >= 0 && newIndex < Items.Count;
    }

    private int GetMasterIndexForVisualTarget(int visualTarget, int visualSource)
    {
        // При наличии скрытых (отфильтрованных) элементов визуальный индекс
        // не равен мастер-индексу — пересчитываем через ID видимого элемента.
        if (visualTarget >= Items.Count) return _masterIds.Count - 1;
        var targetId = GetVmId(Items[visualTarget]);
        return string.IsNullOrEmpty(targetId) ? visualTarget : _masterIds.IndexOf(targetId);
    }

    #endregion

    #region Helpers

    protected TViewModel? GetCachedVm(string id) => _vmCache.GetValueOrDefault(id);

    protected List<TSource> GetLoadedItemsSnapshot() =>
        [.. _masterIds.Where(_sources.ContainsKey).Select(id => _sources[id])];

    protected List<string> GetAllIds() => [.. _masterIds];

    /// <summary>
    /// O(1) поиск ID по экземпляру ViewModel через обратный индекс.
    /// Ранее — O(n) полный обход _vmCache.Values с ReferenceEquals.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetVmId(TViewModel vm) =>
        _vmToId.TryGetValue(vm, out var id) ? id : string.Empty;

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    /// <summary>
    /// Диспозит все VM в кэше и очищает оба словаря (_vmCache + _vmToId) атомарно.
    /// </summary>
    private void DisposeAndClearVmCache()
    {
        foreach (var vm in _vmCache.Values)
            vm.Dispose();
        _vmCache.Clear();
        _vmToId.Clear();
    }

    #endregion

    #region Local Mutations

    /// <summary>
    /// Удаляет элемент из всех внутренних структур без полного rebuild.
    /// O(n) по _masterIds (List.Remove), O(1) по словарям и ObservableCollection.Remove.
    /// 
    /// <para><b>VM не диспозится:</b> экземпляр может быть shared через
    /// <see cref="TrackViewModelFactory"/> (WeakReference cache).
    /// Dispose произойдёт при GC или при полном Dispose ReorderableViewModel.</para>
    /// </summary>
    /// <returns>true если элемент был найден и удалён.</returns>
    protected bool RemoveItemLocally(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        bool removed = _masterIds.Remove(id);
        _sources.Remove(id);

        if (_vmCache.Remove(id, out var vm))
        {
            _vmToId.Remove(vm);
            Items.Remove(vm);
        }

        return removed;
    }

    #endregion

    #region IDisposable

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            Log.Debug($"[ReorderableVM] Disposing {_vmCache.Count} cached VMs");

            CancelLoading();
            DisposeAndClearVmCache();

            Items.Clear();
            _sources.Clear();
            _masterIds.Clear();
            _rebuildBuffer = null!;
            _syncPositions.Clear();
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}