using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.UI.Components;

namespace MyLiteMusicPlayer.UI.Tabs;

public class SearchTab : ITab
{
    public string Id => "search";
    public string Name => "🔍 Поиск";
    public bool CanClose => false;

    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    private readonly TrackRow _trackRow;
    private readonly Action<string, string> _openPlaylistTab;

    private string _searchQuery = "";
    private List<TrackInfo> _searchResults = new();
    private bool _isSearching;
    private string _statusMessage = "";
    private QueryType _lastQueryType;
    private string? _loadedPlaylistName;

    public SearchTab(
        YoutubeProvider youtube, 
        AudioEngine audio,
        LibraryService library,
        DownloadService downloads,
        Action<TrackInfo> onStartRadio,
        Action<string, string> openPlaylistTab)
    {
        _youtube = youtube;
        _audio = audio;
        _library = library;
        _openPlaylistTab = openPlaylistTab;
        
        _trackRow = new TrackRow(
            library,
            downloads,
            track =>
            {
                audio.PlayTrack(track);
                library.AddToRecentlyPlayed(track);
                library.AddOrUpdateTrack(track);
            },
            track => audio.Enqueue(track),
            onStartRadio
        );
    }

    public void OnOpen() { }
    public void OnClose() { }

    public void Render()
    {
        ImGui.BeginChild("SearchContent", new Vector2(0, -90), ImGuiChildFlags.None);
        
        // Поле поиска
        ImGui.Text("Поиск музыки или вставьте ссылку:");
        
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 130);
        
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
        
        bool enterPressed = ImGui.InputTextWithHint(
            "##search", 
            "Название, исполнитель или URL...",
            ref _searchQuery, 
            512, 
            ImGuiInputTextFlags.EnterReturnsTrue);
        
        ImGui.PopStyleVar(2);
        
        ImGui.SameLine();
        
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.8f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 0.9f, 1f));
        
        bool searchClicked = ImGui.Button(_isSearching ? "..." : "Найти", new Vector2(100, 34));
        
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        
        if ((enterPressed || searchClicked) && !string.IsNullOrWhiteSpace(_searchQuery) && !_isSearching)
        {
            PerformSearch();
        }
        
        ImGui.Spacing();
        
        // Индикатор типа запроса
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            var queryType = _youtube.DetectQueryType(_searchQuery);
            
            (string icon, string hint, Vector4 color) = queryType switch
            {
                QueryType.DirectUrl => ("🔗", "Прямая ссылка — трек будет добавлен сразу", 
                    new Vector4(0.4f, 0.8f, 0.4f, 1f)),
                QueryType.Playlist => ("📋", "Плейлист YouTube — будут загружены все треки", 
                    new Vector4(0.4f, 0.6f, 0.9f, 1f)),
                QueryType.Search => ("🔍", "Поиск по запросу", 
                    new Vector4(0.7f, 0.7f, 0.7f, 1f)),
                _ => ("", "", new Vector4(0.5f, 0.5f, 0.5f, 1f))
            };
            
            if (!string.IsNullOrEmpty(icon))
            {
                ImGui.TextColored(color, $"{icon} {hint}");
            }
        }
        
        ImGui.Spacing();
        
        // Статус
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            if (_isSearching)
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), _statusMessage);
            }
            else
            {
                ImGui.TextDisabled(_statusMessage);
            }
        }
        
        ImGui.Separator();
        ImGui.Spacing();
        
        // Результаты
        if (_isSearching)
        {
            // Анимация загрузки
            float time = (float)ImGui.GetTime();
            string dots = new('.', (int)(time * 3) % 4);
            ImGui.TextDisabled($"Поиск{dots}");
        }
        else if (_searchResults.Count > 0)
        {
            // Кнопки быстрых действий
            ImGui.Text($"Найдено: {_searchResults.Count}");
            
            ImGui.SameLine();
            
            if (ImGui.SmallButton("Играть всё"))
            {
                PlayAllResults();
            }
            
            ImGui.SameLine();
            
            if (ImGui.SmallButton("В очередь"))
            {
                foreach (var track in _searchResults)
                    _audio.Enqueue(track);
                
                _statusMessage = $"Добавлено {_searchResults.Count} треков в очередь";
            }
            
            // Для плейлиста предлагаем сохранить
            if (_lastQueryType == QueryType.Playlist && !string.IsNullOrEmpty(_loadedPlaylistName))
            {
                ImGui.SameLine();
                
                if (ImGui.SmallButton("💾 Сохранить плейлист"))
                {
                    SavePlaylistToLibrary();
                }
            }
            
            ImGui.Spacing();
            
            for (int i = 0; i < _searchResults.Count; i++)
            {
                _trackRow.Render(_searchResults[i], i, _audio.CurrentTrack);
            }
        }
        else if (!string.IsNullOrWhiteSpace(_searchQuery) && _lastQueryType != QueryType.None)
        {
            ImGui.TextDisabled("Ничего не найдено. Попробуйте изменить запрос.");
        }
        else
        {
            // Подсказки для пустого поиска
            ImGui.Spacing();
            ImGui.TextDisabled("Введите запрос для поиска или вставьте ссылку:");
            ImGui.Spacing();
            ImGui.BulletText("Название песни или исполнителя");
            ImGui.BulletText("Ссылку на YouTube видео");
            ImGui.BulletText("Ссылку на YouTube плейлист");
            ImGui.BulletText("Ссылку из YouTube Music");
        }
        
        ImGui.EndChild();
    }

    private async void PerformSearch()
    {
        if (_isSearching) return;
        
        _isSearching = true;
        _searchResults.Clear();
        _loadedPlaylistName = null;
        _lastQueryType = _youtube.DetectQueryType(_searchQuery);
        
        try
        {
            switch (_lastQueryType)
            {
                case QueryType.DirectUrl:
                    await HandleDirectUrl();
                    break;
                
                case QueryType.Playlist:
                    await HandlePlaylist();
                    break;
                
                case QueryType.Search:
                    await HandleSearch();
                    break;
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            _isSearching = false;
        }
    }

    private async Task HandleDirectUrl()
    {
        _statusMessage = "Загрузка трека...";
        
        var track = await _youtube.GetTrackByUrlAsync(_searchQuery);
        
        if (track != null)
        {
            _searchResults.Add(track);
            _statusMessage = "Готово!";
            
            // Автоматически добавляем в библиотеку и играем
            if (_library.Data.AutoPlayOnUrlPaste)
            {
                _audio.PlayTrack(track);
                _library.AddToRecentlyPlayed(track);
                _library.AddOrUpdateTrack(track);
                _statusMessage = "Воспроизведение...";
            }
        }
        else
        {
            _statusMessage = "Не удалось загрузить видео. Проверьте ссылку.";
        }
    }

    private async Task HandlePlaylist()
    {
        _statusMessage = "Загрузка плейлиста...";
        
        var playlist = await _youtube.GetPlaylistAsync(_searchQuery);
        
        if (playlist != null)
        {
            _searchResults = playlist.Value.Tracks;
            _loadedPlaylistName = playlist.Value.Name;
            _statusMessage = $"Плейлист: {playlist.Value.Name} ({_searchResults.Count} треков)";
        }
        else
        {
            _statusMessage = "Не удалось загрузить плейлист";
        }
    }

    private async Task HandleSearch()
    {
        _statusMessage = "Поиск...";
        
        _searchResults = await _youtube.SearchAsync(_searchQuery, 25);
        
        _statusMessage = _searchResults.Count > 0 
            ? $"Найдено {_searchResults.Count} результатов" 
            : "Ничего не найдено";
    }

    private void PlayAllResults()
    {
        if (_searchResults.Count == 0) return;
        
        _audio.PlayTrack(_searchResults[0]);
        _library.AddToRecentlyPlayed(_searchResults[0]);
        
        foreach (var track in _searchResults.Skip(1))
        {
            _audio.Enqueue(track);
        }
    }

    private void SavePlaylistToLibrary()
    {
        if (string.IsNullOrEmpty(_loadedPlaylistName) || _searchResults.Count == 0)
            return;
        
        var playlist = _library.CreatePlaylist(_loadedPlaylistName);
        
        foreach (var track in _searchResults)
        {
            _library.AddOrUpdateTrack(track);
            _library.AddTrackToPlaylist(track, playlist.Id);
        }
        
        _statusMessage = $"Плейлист \"{_loadedPlaylistName}\" сохранён!";
    }
}