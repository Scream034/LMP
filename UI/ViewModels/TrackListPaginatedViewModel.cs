using System.Reactive.Linq;
using LMP.UI.Features.Shared;
using ReactiveUI;

namespace LMP.UI.ViewModels;

/// <summary>
/// Абстрактный базовый класс для всех экранов с пагинированным списком треков.
/// Фиксирует generic-параметры PaginatedViewModel на (TrackInfo, TrackItemViewModel)
/// и добавляет Smart Parent паттерн: O(1) обновление активного трека и прогресса загрузки.
/// </summary>
/// <remarks>
/// <para><b>Smart Parent:</b> события AudioEngine/DownloadService приходят сюда,
/// а не в каждый TrackItemViewModel. Это устраняет N подписок (по одной на каждый трек)
/// и заменяет их локальным высокопроизводительным zero-alloc сканированием видимых элементов.</para>
/// </remarks>
public abstract class TrackListPaginatedViewModel
    : PaginatedViewModel<TrackInfo, TrackItemViewModel>
{
    #region Fields

    protected readonly AudioEngine Audio;
    protected readonly DownloadService Downloads;
    protected readonly TrackViewModelFactory VmFactory;

    /// <summary>
    /// Текущая активная VM. Хранится для O(1) сброса при смене трека:
    /// не нужно обходить весь Items чтобы найти предыдущую активную VM.
    /// </summary>
    private TrackItemViewModel? _currentActiveVm;

    #endregion

    #region Constructor

    protected TrackListPaginatedViewModel(
        AudioEngine audio,
        DownloadService downloads,
        TrackViewModelFactory vmFactory)
    {
        Audio = audio;
        Downloads = downloads;
        VmFactory = vmFactory;

        SubscribeToAudioEngine();
        SubscribeToDownloadService();
    }

    #endregion

    #region Smart Parent — Audio

    /// <summary>
    /// Подписка на события AudioEngine.
    /// </summary>
    private void SubscribeToAudioEngine()
    {
        Observable.FromEvent<Action<TrackInfo?>, TrackInfo?>(
                h => Audio.OnTrackChanged += h,
                h => Audio.OnTrackChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(track => UpdatePlaybackState(track, Audio.IsPlaying))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<bool, bool>, (bool isPlaying, bool isPaused)>(
                h => (a, b) => h((a, b)),
                h => Audio.OnPlaybackStateChanged += h,
                h => Audio.OnPlaybackStateChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => UpdatePlaybackState(Audio.CurrentTrack, x.isPlaying))
            .DisposeWith(Disposables);
    }

    /// <summary>
    /// O(1): сбрасываем предыдущую активную VM и устанавливаем новую через локальный zero-alloc поиск.
    /// </summary>
    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (_currentActiveVm != null && _currentActiveVm.Id != currentTrack?.Id)
        {
            _currentActiveVm.SetActive(false, false);
            _currentActiveVm = null;
        }

        if (currentTrack is null) return;

        if (_currentActiveVm == null)
        {
            // Прямой проход по индексу ReadOnlyObservableCollection не выделяет память в куче
            var count = Items.Count;
            for (int i = 0; i < count; i++)
            {
                var vm = Items[i];
                if (vm.Id == currentTrack.Id)
                {
                    _currentActiveVm = vm;
                    break;
                }
            }
        }

        _currentActiveVm?.SetActive(true, isPlaying);
    }

    #endregion

    #region Smart Parent — Downloads

    /// <summary>
    /// Подписка на DownloadService. Транслирует прогресс в локальные ViewModels.
    /// </summary>
    private void SubscribeToDownloadService()
    {
        Observable.FromEvent<Action<string, float>, (string id, float progress)>(
                h => (id, p) => h((id, p)),
                h => Downloads.OnProgress += h,
                h => Downloads.OnProgress -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x =>
            {
                var count = Items.Count;
                for (int i = 0; i < count; i++)
                {
                    var vm = Items[i];
                    if (vm.Id == x.id)
                    {
                        vm.SetDownloadState(true, x.progress);
                        break;
                    }
                }
            })
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<string, bool, string?>, (string id, bool ok, string? path)>(
                h => (id, ok, path) => h((id, ok, path)),
                h => Downloads.OnCompleted += h,
                h => Downloads.OnCompleted -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x =>
            {
                var count = Items.Count;
                for (int i = 0; i < count; i++)
                {
                    var vm = Items[i];
                    if (vm.Id == x.id)
                    {
                        vm.SetDownloadState(false, 0f);
                        break;
                    }
                }
            })
            .DisposeWith(Disposables);
    }

    #endregion

    #region PaginatedViewModel Overrides

    protected sealed override string GetItemId(TrackInfo item) => item.Id;

    protected sealed override bool FilterItem(TrackInfo item, string query) =>
        TrackFilters.MatchesTitleOrAuthor(item, query);

    /// <summary>
    /// Создаёт или возвращает из локального кэша VM для трека.
    /// Сразу устанавливает активное состояние если трек играет.
    /// </summary>
    protected sealed override TrackItemViewModel CreateItemViewModel(TrackInfo track)
    {
        var vm = VmFactory.GetOrCreate(track, OnPlay);

        if (Audio.CurrentTrack?.Id == track.Id)
        {
            vm.SetActive(true, Audio.IsPlaying);
            _currentActiveVm = vm;
        }

        return vm;
    }

    #endregion

    #region Abstract

    /// <summary>
    /// Вызывается когда пользователь нажимает Play на треке в списке.
    /// </summary>
    protected abstract void OnPlay(TrackInfo track);

    #endregion
}