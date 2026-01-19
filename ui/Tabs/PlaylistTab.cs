using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.UI.Components;

namespace MyLiteMusicPlayer.UI.Tabs;

public class PlaylistTab : ITab
{
    public string Id { get; }
    public string Name { get; private set; }
    public bool CanClose => _playlist.Id != "liked";

    private readonly Playlist _playlist;
    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly TrackRow _trackRow;
    
    private List<TrackInfo> _tracks = new();
    private string _searchFilter = "";

    public PlaylistTab(
        Playlist playlist,
        LibraryService library,
        AudioEngine audio,
        DownloadService downloads,
        Action<TrackInfo> onStartRadio)
    {
        _playlist = playlist;
        _library = library;
        _audio = audio;
        Id = $"playlist_{playlist.Id}";
        
        UpdateName();
        
        _trackRow = new TrackRow(
            library,
            downloads,
            track =>
            {
                audio.PlayTrack(track);
                library.AddToRecentlyPlayed(track);
            },
            track => audio.Enqueue(track),
            onStartRadio
        );
    }

    private void UpdateName()
    {
        Name = _playlist.Id == "liked" ? "♥ Любимое" : $"📋 {_playlist.Name}";
    }

    public void OnOpen()
    {
        RefreshTracks();
        _library.OnDataChanged += RefreshTracks;
    }

    public void OnClose()
    {
        _library.OnDataChanged -= RefreshTracks;
    }

    private void RefreshTracks()
    {
        _tracks = _library.GetPlaylistTracks(_playlist.Id);
        UpdateName();
    }

    public void Render()
    {
        ImGui.BeginChild("PlaylistContent", new Vector2(0, -90), ImGuiChildFlags.None);
        
        // Заголовок плейлиста
        RenderHeader();
        
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        
        // Фильтр
        if (_tracks.Count > 5)
        {
            ImGui.SetNextItemWidth(300);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            ImGui.InputTextWithHint("##filter", "🔍 Фильтр...", ref _searchFilter, 100);
            ImGui.PopStyleVar();
            ImGui.Spacing();
        }
        
        // Список треков
        if (_tracks.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Плейлист пуст");
            ImGui.Spacing();
            ImGui.TextDisabled("Добавьте треки из поиска, нажав + на треке");
        }
        else
        {
            var filteredTracks = string.IsNullOrWhiteSpace(_searchFilter)
                ? _tracks
                : _tracks.Where(t => 
                    t.Title.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                    t.Author.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                  .ToList();
            
            if (filteredTracks.Count == 0)
            {
                ImGui.TextDisabled("Ничего не найдено");
            }
            else
            {
                for (int i = 0; i < filteredTracks.Count; i++)
                {
                    _trackRow.Render(filteredTracks[i], i, _audio.CurrentTrack, _playlist.Id);
                }
            }
        }
        
        ImGui.EndChild();
    }

    private void RenderHeader()
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector2 startPos = ImGui.GetCursorScreenPos();
        
        // Обложка плейлиста
        float coverSize = 120;
        
        uint coverColor = _playlist.Id == "liked" 
            ? ImGui.GetColorU32(new Vector4(0.7f, 0.2f, 0.2f, 1f))
            : ImGui.GetColorU32(new Vector4(0.2f, 0.3f, 0.5f, 1f));
        
        drawList.AddRectFilled(startPos, startPos + new Vector2(coverSize, coverSize), coverColor, 8f);
        
        // Иконка
        string icon = _playlist.Id == "liked" ? "♥" : "♪";
        Vector2 iconPos = startPos + new Vector2(coverSize / 2 - 15, coverSize / 2 - 15);
        drawList.AddText(iconPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.8f)), icon);
        
        // Информация справа от обложки
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(coverSize + 20, 10));
        
        ImGui.BeginGroup();
        
        ImGui.TextDisabled(_playlist.IsFromAccount ? "ПЛЕЙЛИСТ YOUTUBE" : "ПЛЕЙЛИСТ");
        
        // Название (большое)
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
        ImGui.SetWindowFontScale(1.5f);
        ImGui.Text(_playlist.Name);
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();
        
        // Статистика
        TimeSpan totalDuration = TimeSpan.Zero;
        foreach (var track in _tracks)
        {
            totalDuration += track.Duration;
        }
        
        ImGui.TextDisabled($"{_tracks.Count} треков • {FormatTotalDuration(totalDuration)}");
        
        ImGui.Spacing();
        
        // Кнопки управления
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 20f);
        
        // Кнопка Play
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.7f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.8f, 0.4f, 1f));
        
        if (ImGui.Button("▶ Воспроизвести", new Vector2(140, 40)))
        {
            PlayAll(false);
        }
        
        ImGui.PopStyleColor(2);
        
        ImGui.SameLine();
        
        // Кнопка Shuffle
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.35f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.4f, 0.45f, 1f));
        
        if (ImGui.Button("🔀 Перемешать", new Vector2(130, 40)))
        {
            PlayAll(true);
        }
        
        ImGui.PopStyleColor(2);
        
        ImGui.PopStyleVar();
        
        ImGui.EndGroup();
        
        // Смещаем курсор ниже обложки
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + coverSize - 60);
    }

    private void PlayAll(bool shuffle)
    {
        if (_tracks.Count == 0) return;
        
        var tracksToPlay = shuffle 
            ? _tracks.OrderBy(_ => Random.Shared.Next()).ToList()
            : _tracks.ToList();
        
        _audio.PlayTrack(tracksToPlay[0]);
        _library.AddToRecentlyPlayed(tracksToPlay[0]);
        
        foreach (var track in tracksToPlay.Skip(1))
        {
            _audio.Enqueue(track);
        }
    }

    private static string FormatTotalDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} ч {duration.Minutes} мин";
        }
        return $"{(int)duration.TotalMinutes} мин";
    }
}