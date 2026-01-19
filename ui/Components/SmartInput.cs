using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;

namespace MyLiteMusicPlayer.UI.Components;

public class SmartInput
{
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    
    private string _inputText = "";
    private QueryType _detectedType = QueryType.None;
    private bool _isProcessing;
    private string _statusMessage = "";
    private TrackInfo? _detectedTrack;
    private bool _showQuickAddPopup;

    public event Action<List<TrackInfo>>? OnSearchResults;
    public event Action<string>? OnStatusChanged;

    public SmartInput(YoutubeProvider youtube, AudioEngine audio, LibraryService library)
    {
        _youtube = youtube;
        _audio = audio;
        _library = library;
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            _inputText = value;
            _detectedType = _youtube.DetectQueryType(_inputText);
        }
    }

    public void Render()
    {
        ImGui.Text("Поиск или вставьте ссылку:");
        
        // Поле ввода
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 120);
        
        bool enterPressed = ImGui.InputText("##smartinput", ref _inputText, 512, 
            ImGuiInputTextFlags.EnterReturnsTrue);
        
        // Определяем тип при изменении
        if (ImGui.IsItemEdited())
        {
            _detectedType = _youtube.DetectQueryType(_inputText);
            _detectedTrack = null;
            _statusMessage = "";
        }
        
        ImGui.SameLine();
        
        // Кнопка действия
        string buttonText = _detectedType switch
        {
            QueryType.DirectUrl => "Добавить",
            QueryType.Playlist => "Загрузить",
            QueryType.Search => "Найти",
            _ => "Найти"
        };
        
        bool canProcess = !_isProcessing && !string.IsNullOrWhiteSpace(_inputText);
        if (!canProcess) ImGui.BeginDisabled();
        
        bool buttonClicked = ImGui.Button(buttonText, new Vector2(100, 0));
        
        if (!canProcess) ImGui.EndDisabled();
        
        if ((enterPressed || buttonClicked) && canProcess)
        {
            ProcessInput();
        }
        
        // Подсказка о типе запроса
        if (!string.IsNullOrWhiteSpace(_inputText))
        {
            string hint = _detectedType switch
            {
                QueryType.DirectUrl => "🔗 Ссылка на видео — будет добавлено напрямую",
                QueryType.Playlist => "📋 Ссылка на плейлист",
                QueryType.Search => "🔍 Текстовый поиск",
                _ => ""
            };
            
            Vector4 hintColor = _detectedType switch
            {
                QueryType.DirectUrl => new Vector4(0.4f, 0.8f, 0.4f, 1f),
                QueryType.Playlist => new Vector4(0.4f, 0.6f, 1f, 1f),
                _ => new Vector4(0.6f, 0.6f, 0.6f, 1f)
            };
            
            ImGui.TextColored(hintColor, hint);
        }
        
        // Статус
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            Vector4 statusColor = _isProcessing 
                ? new Vector4(1f, 1f, 0.3f, 1f) 
                : new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(statusColor, _statusMessage);
        }
        
        // Quick Add popup для прямых ссылок
        RenderQuickAddPopup();
    }

    private async void ProcessInput()
    {
        if (_isProcessing) return;
        
        _isProcessing = true;
        _statusMessage = "Обработка...";
        
        try
        {
            switch (_detectedType)
            {
                case QueryType.DirectUrl:
                    await ProcessDirectUrl();
                    break;
                
                case QueryType.Playlist:
                    await ProcessPlaylist();
                    break;
                
                case QueryType.Search:
                    await ProcessSearch();
                    break;
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async Task ProcessDirectUrl()
    {
        _statusMessage = "Загрузка информации о треке...";
        
        var track = await _youtube.GetTrackByUrlAsync(_inputText);
        
        if (track != null)
        {
            _detectedTrack = track;
            _showQuickAddPopup = true;
            _statusMessage = $"Найдено: {track.Title}";
        }
        else
        {
            _statusMessage = "Не удалось загрузить видео";
        }
    }

    private async Task ProcessPlaylist()
    {
        _statusMessage = "Загрузка плейлиста...";
        
        var playlist = await _youtube.GetPlaylistAsync(_inputText);
        
        if (playlist != null)
        {
            _statusMessage = $"Плейлист: {playlist.Value.Name} ({playlist.Value.Tracks.Count} треков)";
            OnSearchResults?.Invoke(playlist.Value.Tracks);
        }
        else
        {
            _statusMessage = "Не удалось загрузить плейлист";
        }
    }

    private async Task ProcessSearch()
    {
        _statusMessage = "Поиск...";
        
        var results = await _youtube.SearchAsync(_inputText, 25);
        
        _statusMessage = results.Count > 0 
            ? $"Найдено: {results.Count}" 
            : "Ничего не найдено";
        
        OnSearchResults?.Invoke(results);
    }

    private void RenderQuickAddPopup()
    {
        if (!_showQuickAddPopup || _detectedTrack == null) return;
        
        ImGui.OpenPopup("QuickAddTrack");
        
        Vector2 center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(400, 0));
        
        if (ImGui.BeginPopupModal("QuickAddTrack", ref _showQuickAddPopup, 
            ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Добавить трек:");
            ImGui.Separator();
            
            // Информация о треке
            ImGui.Spacing();
            ImGui.Text(_detectedTrack.Title);
            ImGui.TextDisabled(_detectedTrack.Author);
            ImGui.TextDisabled($"Длительность: {_detectedTrack.Duration:m\\:ss}");
            ImGui.Spacing();
            
            ImGui.Separator();
            ImGui.Spacing();
            
            // Кнопки действий
            if (ImGui.Button("▶ Воспроизвести", new Vector2(180, 30)))
            {
                _audio.PlayTrack(_detectedTrack);
                _library.AddOrUpdateTrack(_detectedTrack);
                _library.AddToRecentlyPlayed(_detectedTrack);
                _showQuickAddPopup = false;
                _inputText = "";
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.SameLine();
            
            if (ImGui.Button("+ В очередь", new Vector2(180, 30)))
            {
                _audio.Enqueue(_detectedTrack);
                _library.AddOrUpdateTrack(_detectedTrack);
                _showQuickAddPopup = false;
                _inputText = "";
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.Spacing();
            
            // Добавить в плейлист
            if (ImGui.CollapsingHeader("Добавить в плейлист"))
            {
                foreach (var playlist in _library.GetAllPlaylists())
                {
                    if (playlist.Id == "liked") continue;
                    
                    if (ImGui.Selectable(playlist.Name))
                    {
                        _library.AddOrUpdateTrack(_detectedTrack);
                        _library.AddTrackToPlaylist(_detectedTrack, playlist.Id);
                        _showQuickAddPopup = false;
                        _inputText = "";
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            
            ImGui.Spacing();
            
            if (ImGui.Button("Отмена", new Vector2(-1, 0)))
            {
                _showQuickAddPopup = false;
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.EndPopup();
        }
    }

    public void Clear()
    {
        _inputText = "";
        _detectedType = QueryType.None;
        _detectedTrack = null;
        _statusMessage = "";
    }
}