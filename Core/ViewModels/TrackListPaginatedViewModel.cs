using System.Reactive.Linq;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Features.Shared;
using ReactiveUI;

namespace LMP.Core.ViewModels;

/// <summary>
/// Абстрактный базовый класс для всех экранов с пагинированным списком треков.
/// Фиксирует generic-параметры PaginatedViewModel на (TrackInfo, TrackItemViewModel)
/// и добавляет Smart Parent паттерн: O(1) обновление активного трека и прогресса загрузки.
///
/// <para><b>Smart Parent:</b> события AudioEngine/DownloadService приходят сюда,
/// а не в каждый TrackItemViewModel. Это устраняет N подписок (по одной на каждый трек)
/// и заменяет их двумя подписками на родителе с O(1) lookup через TrackViewModelFactory.</para>
///
/// <para><b>Наследники должны реализовать:</b>
/// <see cref="OnPlay"/> — действие при нажатии Play на треке,
/// <see cref="FetchMoreFromNetworkAsync"/> — загрузка следующей порции данных.</para>
/// </summary>
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
    /// Два события вместо N×2 (по два на каждую TrackItemViewModel).
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
    /// O(1): сбрасываем предыдущую активную VM и устанавливаем новую.
    /// TryGet — O(1) lookup в WeakReference-кэше фабрики.
    /// </summary>
    private void UpdatePlaybackState(TrackInfo? currentTrack, bool isPlaying)
    {
        if (_currentActiveVm != null && _currentActiveVm.Id != currentTrack?.Id)
        {
            _currentActiveVm.SetActive(false, false);
            _currentActiveVm = null;
        }

        if (currentTrack is null) return;

        _currentActiveVm ??= VmFactory.TryGet(currentTrack.Id);
        _currentActiveVm?.SetActive(true, isPlaying);
    }

    #endregion

    #region Smart Parent — Downloads

    private void SubscribeToDownloadService()
    {
        Observable.FromEvent<Action<string, float>, (string id, float progress)>(
                h => (id, p) => h((id, p)),
                h => Downloads.OnProgress += h,
                h => Downloads.OnProgress -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => VmFactory.TryGet(x.id)?.SetDownloadState(true, x.progress))
            .DisposeWith(Disposables);

        Observable.FromEvent<Action<string, bool, string?>, (string id, bool ok, string? path)>(
                h => (id, ok, path) => h((id, ok, path)),
                h => Downloads.OnCompleted += h,
                h => Downloads.OnCompleted -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(x => VmFactory.TryGet(x.id)?.SetDownloadState(false, 0f))
            .DisposeWith(Disposables);
    }

    #endregion

    #region PaginatedViewModel Overrides

    protected sealed override string GetItemId(TrackInfo item) => item.Id;

    protected sealed override bool FilterItem(TrackInfo item, string query) =>
        TrackFilters.MatchesTitleOrAuthor(item, query);

    /// <summary>
    /// Создаёт или возвращает из кэша VM для трека.
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
    /// Наследник определяет контекст воспроизведения (плейлист, поиск, история и т.д.).
    /// </summary>
    protected abstract void OnPlay(TrackInfo track);

    #endregion
}