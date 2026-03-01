using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Core.Youtube.Search;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace LMP.UI.Dialogs;

// ═══ Models ═══

/// <summary>
/// Решение по одному плейлисту из объединённого диалога синхронизации.
/// Заменяет старые SyncSelection + MergeDecision двумя отдельными диалогами.
/// </summary>
public sealed record SyncDecision(PlaylistSearchResult Playlist, MergeAction Action);

/// <summary>
/// Вариант действия для ComboBox в диалоге.
/// </summary>
public sealed record SyncActionOption(MergeAction Action, string DisplayName)
{
    public override string ToString() => DisplayName;
}

// ═══ ViewModel ═══

public class SyncSelectionViewModel : ViewModelBase
{
    public ObservableCollection<SyncItemViewModel> Items { get; } = [];

    [Reactive] public string SummaryText { get; private set; } = "";

    public ReactiveCommand<Unit, List<SyncDecision>> ConfirmCommand { get; }
    public ReactiveCommand<Unit, List<SyncDecision>> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeselectAllCommand { get; }

    /// <summary>
    /// Объединённый диалог: выбор плейлистов + разрешение конфликтов в одном окне.
    /// </summary>
    /// <param name="playlists">Плейлисты с YouTube для импорта.</param>
    /// <param name="existingLocalNames">Имена локальных плейлистов — для определения конфликтов.</param>
    public SyncSelectionViewModel(
        IEnumerable<PlaylistSearchResult> playlists,
        ISet<string> existingLocalNames)
    {
        foreach (var p in playlists)
        {
            var hasConflict = existingLocalNames.Contains(p.Title);
            Items.Add(new SyncItemViewModel(p, hasConflict));
        }

        // Confirm: собираем решения (Skip = не импортировать)
        ConfirmCommand = CreateCommand(ReactiveCommand.Create(() =>
            Items.Where(x => x.SelectedAction != MergeAction.Skip)
                .Select(x => new SyncDecision(x.Original, x.SelectedAction))
                .ToList()));

        CancelCommand = CreateCommand(ReactiveCommand.Create(
            () => new List<SyncDecision>()));

        SelectAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            foreach (var item in Items)
                item.SelectedOption = item.AvailableActions
                    .FirstOrDefault(a => a.Action != MergeAction.Skip)
                    ?? item.AvailableActions[0];
        }));

        DeselectAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            foreach (var item in Items)
                item.SelectedOption = item.AvailableActions
                    .FirstOrDefault(a => a.Action == MergeAction.Skip)
                    ?? item.AvailableActions[^1];
        }));

        // Обновляем счётчик при изменении выбора любого элемента
        foreach (var item in Items)
        {
            item.WhenAnyValue(x => x.SelectedOption)
                .Subscribe(_ => UpdateSummary())
                .DisposeWith(Disposables);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = Items.Count(x => x.SelectedAction != MergeAction.Skip);
        var conflicts = Items.Count(x => x.HasConflict && x.SelectedAction != MergeAction.Skip);

        SummaryText = conflicts > 0
            ? $"{selected}/{Items.Count} • {string.Format(SL["Sync_ConflictsCount"], conflicts)}"
            : $"{selected}/{Items.Count}";
    }
}

// ═══ Item ViewModel ═══

public class SyncItemViewModel : ReactiveObject
{
    public PlaylistSearchResult Original { get; }
    public bool HasConflict { get; }

    public string Name => Original.Title;
    public string Author => Original.Author?.ChannelTitle ?? "";
    public string? ThumbnailUrl => Original.Thumbnails.FirstOrDefault()?.Url;

    /// <summary>Доступные действия (зависят от наличия конфликта).</summary>
    public List<SyncActionOption> AvailableActions { get; }

    [Reactive] public SyncActionOption? SelectedOption { get; set; }

    /// <summary>Текущее выбранное действие (для логики).</summary>
    public MergeAction SelectedAction => SelectedOption?.Action ?? MergeAction.Skip;

    /// <summary>Для биндинга локализации в DataTemplate.</summary>
    public LocalizationService L => LocalizationService.Instance;

    public SyncItemViewModel(PlaylistSearchResult original, bool hasConflict)
    {
        Original = original;
        HasConflict = hasConflict;

        var l = LocalizationService.Instance;

        AvailableActions = hasConflict
            ?
            [
                new SyncActionOption(MergeAction.Merge, l["Sync_Action_Merge"]),
                new SyncActionOption(MergeAction.Duplicate, l["Sync_Action_CreateNew"]),
                new SyncActionOption(MergeAction.Skip, l["Sync_Action_Skip"])
            ]
            :
            [
                new SyncActionOption(MergeAction.Duplicate, l["Sync_Action_Import"]),
                new SyncActionOption(MergeAction.Skip, l["Sync_Action_Skip"])
            ];

        // Дефолт: Import/Merge (первый элемент, не Skip)
        SelectedOption = AvailableActions[0];
    }
}