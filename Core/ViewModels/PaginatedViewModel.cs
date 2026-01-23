// ============================================================================
// Файл: Core/ViewModels/PaginatedViewModel.cs
// Описание: Базовый абстрактный класс для ViewModel, поддерживающих пагинацию.
// Исправления:
//   - [FIX] Полная реализация IDisposable.
//   - [FIX] Принудительный вызов Dispose() для всех элементов списка при очистке.
//   - [FIX] Защита от выполнения асинхронных операций после уничтожения объекта.
// ============================================================================

using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Collections;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyLiteMusicPlayer.Core.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace MyLiteMusicPlayer.Core.ViewModels;

/// <summary>
/// Базовый класс для ViewModel, отображающих списки с поддержкой ленивой загрузки (Infinite Scroll).
/// Управляет жизненным циклом элементов списка и предотвращает утечки памяти.
/// </summary>
/// <typeparam name="TSource">Тип модели данных (например, TrackInfo).</typeparam>
/// <typeparam name="TViewModel">Тип ViewModel элемента списка (например, TrackItemViewModel).</typeparam>
public abstract class PaginatedViewModel<TSource, TViewModel> : ViewModelBase, IDisposable
    where TViewModel : class
{
    #region Constants

    private const int DefaultLoadDelayMs = 200;
    private const int DefaultPrefetchThreshold = 15;

    #endregion

    #region Fields

    /// <summary>
    /// Ссылка на сервис библиотеки для доступа к глобальным настройкам.
    /// </summary>
    protected readonly LibraryService LibService;

    // [FIX] Храним полный список исходных данных.
    private readonly List<TSource> _allItems = [];

    // [FIX] Храним ID загруженных элементов для исключения дубликатов.
    private readonly HashSet<string> _loadedIds = [];

    // [FIX] Токен отмены для текущей операции загрузки.
    private CancellationTokenSource? _loadCts;

    // [FIX] Флаг освобождения ресурсов.
    private bool _isDisposed;

    // Количество элементов, уже добавленных в Items (UI).
    private int _displayedCount;

    // Флаг, указывающий на возможность загрузки дополнительных данных из внешнего источника.
    private bool _canFetchMore;

    #endregion

    #region Properties

    /// <summary>
    /// Размер порции данных для загрузки за один раз.
    /// </summary>
    protected virtual int BatchSize => LibService.Data.LoadBatchSize > 0 ? LibService.Data.LoadBatchSize : 20;

    /// <summary>
    /// Искусственная задержка перед загрузкой для плавности UI.
    /// </summary>
    protected virtual int LoadDelayMs => DefaultLoadDelayMs;

    /// <summary>
    /// Количество оставшихся элементов, при котором начинается фоновая загрузка следующей порции.
    /// </summary>
    protected virtual int PrefetchThreshold => DefaultPrefetchThreshold;

    /// <summary>
    /// Указывает, происходит ли сейчас первичная загрузка или полная перезагрузка.
    /// </summary>
    [Reactive] public bool IsLoading { get; protected set; }

    /// <summary>
    /// Указывает, происходит ли добавление элементов в конец списка (локально или из сети).
    /// </summary>
    [Reactive] public bool IsLoadingMore { get; protected set; }

    /// <summary>
    /// Указывает, выполняется ли активный сетевой запрос.
    /// </summary>
    [Reactive] public bool IsFetchingFromNetwork { get; protected set; }

    /// <summary>
    /// Указывает, есть ли еще данные для отображения.
    /// </summary>
    [Reactive] public bool HasMoreItems { get; protected set; }

    /// <summary>
    /// Указывает, что достигнут конец списка и данных больше нет.
    /// </summary>
    [Reactive] public bool ReachedEnd { get; protected set; }

    /// <summary>
    /// Включает отображение скелетонов/плавной загрузки.
    /// </summary>
    [Reactive] public bool EnableSmoothLoading { get; set; }

    /// <summary>
    /// Коллекция ViewModel для привязки к UI.
    /// </summary>
    public AvaloniaList<TViewModel> Items { get; } = [];

    /// <summary>
    /// Команда для загрузки следующей порции данных.
    /// </summary>
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

    /// <summary>
    /// Общее количество элементов в буфере.
    /// </summary>
    protected int TotalCount => _allItems.Count;

    /// <summary>
    /// Количество элементов, отображаемых в данный момент.
    /// </summary>
    protected int DisplayedCount => _displayedCount;

    /// <summary>
    /// Доступ только для чтения к исходным данным.
    /// </summary>
    protected IReadOnlyList<TSource> AllItems => _allItems;

    /// <summary>
    /// Токен отмены текущей операции.
    /// </summary>
    protected CancellationToken LoadCancellationToken => _loadCts?.Token ?? CancellationToken.None;

    #endregion

    #region Constructors

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PaginatedViewModel{TSource, TViewModel}"/>.
    /// </summary>
    protected PaginatedViewModel()
    {
        LibService = Program.Services.GetRequiredService<LibraryService>();
        EnableSmoothLoading = LibService.Data.EnableSmoothLoading;

        // [FIX] Подписка на событие изменения настроек. 
        // В Dispose обязательно должна быть отписка.
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

    /// <summary>
    /// Фабричный метод для создания ViewModel элемента.
    /// </summary>
    protected abstract TViewModel CreateItemViewModel(TSource item);

    /// <summary>
    /// Получает уникальный ID элемента для дедупликации.
    /// </summary>
    protected virtual string GetItemId(TSource item) => item?.GetHashCode().ToString() ?? "";

    /// <summary>
    /// Загружает данные из внешнего источника (сети/базы).
    /// </summary>
    protected virtual Task<List<TSource>> FetchMoreFromNetworkAsync(CancellationToken ct)
        => Task.FromResult(new List<TSource>());

    /// <summary>
    /// Хук, вызываемый после создания ViewModel.
    /// </summary>
    protected virtual void OnItemCreated(TSource source, TViewModel viewModel) { }

    #endregion

    #region Public Methods

    /// <summary>
    /// Инициализирует список начальным набором данных.
    /// </summary>
    protected async Task InitializeItemsAsync(IEnumerable<TSource> items, bool canFetchMore = true)
    {
        if (_isDisposed) return;

        // [FIX] Полная очистка предыдущего состояния
        ClearItemsInternal();

        _loadCts = new CancellationTokenSource();
        _canFetchMore = canFetchMore;

        if (items != null)
        {
            AppendSourceItems(items);
        }

        UpdateHasMoreItems();

        Log.Info($"[Paginated] Initialized with {_allItems.Count} items.");

        // Сразу загружаем первую порцию данных
        if (_allItems.Count > 0)
        {
            await LoadNextBatchAsync(skipDelay: true);
        }
    }

    /// <summary>
    /// Очищает список и освобождает ресурсы.
    /// </summary>
    protected void ClearItems()
    {
        ClearItemsInternal();
    }

    #endregion

    #region Internal Logic

    /// <summary>
    /// Внутренняя логика очистки с освобождением ресурсов.
    /// </summary>
    private void ClearItemsInternal()
    {
        CancelLoading();

        // [FIX] Критически важно: вызываем Dispose для каждого элемента, который сейчас в списке.
        // Это разрывает подписки на события в TrackItemViewModel.
        foreach (var item in Items)
        {
            if (item is IDisposable d)
            {
                d.Dispose();
            }
        }

        Items.Clear();
        _allItems.Clear();
        _loadedIds.Clear();

        _displayedCount = 0;
        HasMoreItems = false;
        ReachedEnd = false;
        _canFetchMore = false;
    }

    /// <summary>
    /// Отменяет текущую задачу загрузки.
    /// </summary>
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
        int added = AppendSourceItems(newItems);
        if (added > 0)
        {
            HasMoreItems = true;
            ReachedEnd = false;
        }
    }

    private void UpdateHasMoreItems()
    {
        int remaining = _allItems.Count - _displayedCount;
        HasMoreItems = remaining > 0 || _canFetchMore;
    }

    private async Task LoadNextBatchAsync(bool skipDelay = false)
    {
        if (_isDisposed) return;

        int bufferRemaining = _allItems.Count - _displayedCount;

        // Если буфер пуст, пробуем загрузить из сети
        if (bufferRemaining <= 0)
        {
            if (_canFetchMore)
            {
                await TryFetchMoreAsync();
                // Пересчитываем после попытки загрузки
                bufferRemaining = _allItems.Count - _displayedCount;
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

            if (_isDisposed || token.IsCancellationRequested) return;

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

            // [FIX] Обновляем коллекцию строго в UI потоке
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isDisposed)
                {
                    Items.AddRange(batchVMs);
                    _displayedCount += batchVMs.Count;
                }
                else
                {
                    foreach (var vm in batchVMs) if (vm is IDisposable d) d.Dispose();
                }
            });

            int remaining = _allItems.Count - _displayedCount;
            HasMoreItems = remaining > 0 || _canFetchMore;
            if (remaining == 0 && !_canFetchMore) ReachedEnd = true;

            if (remaining < PrefetchThreshold && remaining >= 0 && _canFetchMore)
                _ = TryFetchMoreAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task TryFetchMoreAsync()
    {
        if (IsFetchingFromNetwork || !_canFetchMore || _isDisposed) return;

        IsFetchingFromNetwork = true;
        try
        {
            var ct = _loadCts?.Token ?? CancellationToken.None;
            var newItems = await FetchMoreFromNetworkAsync(ct);

            if (_isDisposed || ct.IsCancellationRequested) return;

            if (newItems != null && newItems.Count > 0)
            {
                // [FIX] Добавляем в буфер, но НЕ трогаем UI коллекцию Items напрямую отсюда.
                // LoadNextBatchAsync подхватит эти элементы при следующем вызове.
                AppendSourceItems(newItems);

                // Если это была первая загрузка и UI пуст - инициируем отображение
                if (_displayedCount == 0)
                {
                    // Вызов через LoadNextBatch, чтобы пройти через стандартный путь
                    _ = LoadNextBatchAsync(skipDelay: true);
                }
                else
                {
                    // Просто обновляем флаги
                    HasMoreItems = true;
                    ReachedEnd = false;
                }
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
            IsFetchingFromNetwork = false;
        }
    }

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Освобождает ресурсы.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            // [FIX] Отписка от событий
            LibService.OnDataChanged -= OnLibraryDataChanged;

            // [FIX] Полная очистка
            ClearItemsInternal();
        }

        _isDisposed = true;
    }

    /// <summary>
    /// Освобождает все ресурсы, связанные с этой ViewModel.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}