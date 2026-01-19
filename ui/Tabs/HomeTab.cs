using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.UI.Components;

namespace MyLiteMusicPlayer.UI.Tabs;

public class HomeTab : ITab
{
    public string Id => "home";
    public string Name => "🏠 Главная";
    public bool CanClose => false;

    private readonly LibraryService _library;
    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;
    private readonly GoogleAuthService _auth;
    private readonly TrackRow _trackRow;
    private readonly LoginButton _loginButton;
    
    private List<TrackInfo> _recentlyPlayed = new();
    private List<TrackInfo> _recommendations = new();
    private List<TrackInfo> _trending = new();
    private bool _isLoadingRecommendations;
    private bool _isLoadingTrending;
    private string? _errorMessage;

    public HomeTab(
        LibraryService library, 
        AudioEngine audio, 
        YoutubeProvider youtube,
        GoogleAuthService auth,
        DownloadService downloads, 
        Action<TrackInfo> onStartRadio)
    {
        _library = library;
        _audio = audio;
        _youtube = youtube;
        _auth = auth;
        
        _loginButton = new LoginButton(auth);
        
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
        
        _auth.OnAuthStateChanged += OnAuthChanged;
    }

    private void OnAuthChanged()
    {
        // Перезагружаем рекомендации при изменении состояния авторизации
        LoadRecommendations();
    }

    public void OnOpen()
    {
        _recentlyPlayed = _library.GetRecentlyPlayed(10);
        LoadRecommendations();
        
        if (!_auth.IsAuthenticated)
        {
            LoadTrending();
        }
    }

    public void OnClose() { }

    private async void LoadRecommendations()
    {
        if (_isLoadingRecommendations) return;
        
        _isLoadingRecommendations = true;
        _errorMessage = null;
        
        try
        {
            if (_auth.IsAuthenticated)
            {
                // Персональные рекомендации
                _recommendations = await _youtube.GetPersonalRecommendationsAsync(15);
            }
            else if (_recentlyPlayed.Count > 0)
            {
                // Рекомендации на основе последнего прослушанного
                var seed = _recentlyPlayed[Random.Shared.Next(_recentlyPlayed.Count)];
                _recommendations = await _youtube.GetRadioAsync(seed, 15);
            }
            else
            {
                _recommendations = new List<TrackInfo>();
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Ошибка загрузки: {ex.Message}";
            _recommendations = new List<TrackInfo>();
        }
        finally
        {
            _isLoadingRecommendations = false;
        }
    }

    private async void LoadTrending()
    {
        if (_isLoadingTrending) return;
        
        _isLoadingTrending = true;
        
        try
        {
            _trending = await _youtube.GetTrendingAsync(20);
        }
        catch
        {
            _trending = new List<TrackInfo>();
        }
        finally
        {
            _isLoadingTrending = false;
        }
    }

    public void Render()
    {
        ImGui.BeginChild("HomeContent", new Vector2(0, -90), ImGuiChildFlags.None);
        
        // Приветствие
        string greeting = DateTime.Now.Hour switch
        {
            < 6 => "Доброй ночи",
            < 12 => "Доброе утро",
            < 18 => "Добрый день",
            _ => "Добрый вечер"
        };
        
        ImGui.PushFont(ImGui.GetFont());
        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), $"{greeting}!");
        ImGui.PopFont();
        
        // Кнопка входа если не авторизован
        if (!_auth.IsAuthenticated)
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 210);
            _loginButton.Render();
        }
        
        ImGui.Spacing();
        ImGui.Spacing();
        
        // Ошибка если есть
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _errorMessage);
            ImGui.Spacing();
        }
        
        // Недавно прослушанное
        if (_recentlyPlayed.Count > 0)
        {
            RenderSection("Недавно прослушанное", _recentlyPlayed, 0);
            ImGui.Spacing();
            ImGui.Spacing();
        }
        
        // Рекомендации или тренды
        if (_auth.IsAuthenticated)
        {
            RenderSection("Рекомендации для вас", _recommendations, 100, _isLoadingRecommendations);
        }
        else
        {
            if (_recommendations.Count > 0)
            {
                RenderSection("Похожие на прослушанное", _recommendations, 100, _isLoadingRecommendations);
                ImGui.Spacing();
                ImGui.Spacing();
            }
            
            RenderSection("Популярное", _trending, 200, _isLoadingTrending);
        }
        
        ImGui.EndChild();
    }

    private void RenderSection(string title, List<TrackInfo> tracks, int indexOffset, bool isLoading = false)
    {
        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1f), title);
        
        ImGui.SameLine();
        
        if (tracks.Count > 0)
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 100);
            
            if (ImGui.SmallButton($"Играть все##{title}"))
            {
                if (tracks.Count > 0)
                {
                    _audio.PlayTrack(tracks[0]);
                    _library.AddToRecentlyPlayed(tracks[0]);
                    
                    foreach (var track in tracks.Skip(1))
                        _audio.Enqueue(track);
                }
            }
        }
        
        ImGui.Separator();
        ImGui.Spacing();
        
        if (isLoading)
        {
            ImGui.TextDisabled("Загрузка...");
        }
        else if (tracks.Count == 0)
        {
            ImGui.TextDisabled("Нет данных");
        }
        else
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                _trackRow.Render(tracks[i], indexOffset + i, _audio.CurrentTrack);
            }
        }
    }
}