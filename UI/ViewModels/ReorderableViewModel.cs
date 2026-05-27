using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive.Linq;
using LMP.UI.Features.Shell;
using Avalonia.Collections; // Добавлено

namespace LMP.UI.ViewModels;

/// <summary>
/// Базовый ViewModel для списков треков с виртуализацией, фильтрацией и перетаскиванием.
/// </summary>
public abstract class ReorderableViewModel<TSource, TViewModel> : ViewModelBase, IFilterable, ISmoothTransitionViewModel
    where TViewModel : class, IDisposable
    where TSource : notnull
{
    #region Fields

    protected readonly LibraryService LibService;

    private List<string> _masterIds = [];
    private readonly Dictionary<string, TSource> _sources = [];
    private readonly Dictionary<string, TViewModel> _vmCache = [];
    private readonly Dictionary<TViewModel, string> _vmToId = new(ReferenceEqualityComparer.Instance);
    private List<TViewModel> _rebuildBuffer = [];

    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;

    // Поля автоматического перехватчика переходов
    private bool _isDataLoading = true;
    private bool _isTransitioning;

    #endregion

    #region Properties

    /// <summary>
    /// Управляет видимостью списка. Вычисляет итоговое состояние:
    /// скелетон показывается если грузятся данные ИЛИ идет анимация перехода.
    /// </summary>
    public bool IsLoading
    {
        get => _isDataLoading || _isTransitioning;
        protected set
        {
            if (_isDataLoading == value) return;
            _isDataLoading = value;
            this.RaisePropertyChanged(nameof(IsLoading));
        }
    }

    public string FilterQuery
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public AvaloniaList<TViewModel> Items { get; } = [];

    protected int TotalCount => _masterIds.Count;

    public bool CanReorder => string.IsNullOrWhiteSpace(FilterQuery);

    #endregion

    #region Constructor

    protected ReorderableViewModel()
    {
        LibService = AppEntry.Services.GetRequiredService<LibraryService>();

        this.WhenAnyValue(static x => x.FilterQuery)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RebuildVisibleItems())
            .DisposeWith(Disposables);
    }

    #endregion

    #region ISmoothTransitionViewModel

    /// <inheritdoc />
    public virtual void PrepareForTransition()
    {
        _isTransitioning = true;
        this.RaisePropertyChanged(nameof(IsLoading));
    }

    #endregion

    #region Navigation Lifecycle

    public override async Task OnNavigatedToAsync()
    {
        _isTransitioning = false;
        this.RaisePropertyChanged(nameof(IsLoading));

        await base.OnNavigatedToAsync();
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

    private void SyncVisibleItems(List<TViewModel> newItems)
    {
        Items.Clear();
        Items.InsertRange(0, newItems);
    }

    #endregion

    #region Reordering

    public void MoveItem(int oldIndex, int newIndex)
    {
        if (!ValidateMove(oldIndex, newIndex)) return;

        var movingId = GetVmId(Items[oldIndex]);
        if (string.IsNullOrEmpty(movingId)) return;

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
        int masterNew = GetMasterIndexForVisualTarget(newIndex, oldIndex);

        if (masterOld >= 0 && masterNew >= 0 && masterOld != masterNew)
        {
            // Исправлено: перемещение во внутреннем мастер-списке по точным вычисленным индексам
            _masterIds.RemoveAt(masterOld);
            _masterIds.Insert(masterNew, movingId);
            Items.Move(oldIndex, newIndex);

            try
            {
                // Исправлено: передача корректных индексов уровня базы данных (masterNew вместо newIndex)
                await SaveMoveAsync(masterOld, masterNew, CancellationToken.None);
                Log.Info("[Reorderable] Move saved to DB");
            }
            catch (Exception ex)
            {
                Log.Error($"[Reorderable] Save move failed: {ex.Message}");

                // Исправлено: корректный откат изменений в мастер-списке при сбое БД
                _masterIds.RemoveAt(masterNew);
                _masterIds.Insert(masterOld, movingId);
                Items.Move(newIndex, oldIndex);
            }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetVmId(TViewModel vm) =>
        _vmToId.TryGetValue(vm, out var id) ? id : string.Empty;

    protected void CancelLoading()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void DisposeAndClearVmCache()
    {
        foreach (var vm in _vmCache.Values)
            vm.Dispose();
        _vmCache.Clear();
        _vmToId.Clear();
    }

    #endregion

    #region Local Mutations

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
        }

        base.Dispose(disposing);
        _isDisposed = true;
    }

    #endregion
}