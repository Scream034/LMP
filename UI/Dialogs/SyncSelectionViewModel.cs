using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Collections.ObjectModel;
using System.Reactive;
using LMP.Core.Youtube.Search;

namespace LMP.UI.Dialogs;

public class SyncSelectionViewModel : ViewModelBase
{
    [Reactive] public string Title { get; set; } = "Выберите плейлисты";

    // Список элементов для выбора
    public ObservableCollection<SyncItemViewModel> Items { get; } = [];

    public ReactiveCommand<Unit, List<PlaylistSearchResult>> SyncCommand { get; }
    public ReactiveCommand<Unit, List<PlaylistSearchResult>> CancelCommand { get; }

    public SyncSelectionViewModel(IEnumerable<PlaylistSearchResult> playlists)
    {
        foreach (var p in playlists)
        {
            Items.Add(new SyncItemViewModel(p));
        }

        // Возвращаем список выбранных
        SyncCommand = CreateCommand(ReactiveCommand.Create(() => 
            Items.Where(x => x.IsSelected).Select(x => x.Original).ToList()));

        // Возвращаем пустой список (отмена)
        CancelCommand = CreateCommand(ReactiveCommand.Create(() => new List<PlaylistSearchResult>()));
    }
}

public class SyncItemViewModel(PlaylistSearchResult original) : ReactiveObject
{
    public PlaylistSearchResult Original { get; } = original;

    [Reactive] public bool IsSelected { get; set; } = true;
    public string Name => Original.Title;
    public string Author => Original.Author?.ChannelTitle ?? "";
    public string? ThumbnailUrl => Original.Thumbnails.FirstOrDefault()?.Url;
}

