using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.UI.Components;

namespace MyLiteMusicPlayer.UI.Tabs;

public class LibraryTab : ITab
{
    public string Id => "library";
    public string Name => "📚 Библиотека";
    public bool CanClose => false;

    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;
    private readonly YoutubeProvider _youtube;
    private readonly Action<string> _openPlaylist;
    private readonly LoginButton _loginButton;
    
    private string _newPlaylistName = "";
    private bool _showCreateDialog;
    private bool _isLoadingAccountPlaylists;
    private List<Playlist> _accountPlaylists = new();

    public LibraryTab(
        LibraryService library, 
        GoogleAuthService auth,
        YoutubeProvider youtube,
        Action<string> openPlaylist)
    {
        _library = library;
        _auth = auth;
        _youtube = youtube;
        _openPlaylist = openPlaylist;
        _loginButton = new LoginButton(auth);
        
        _auth.OnAuthStateChanged += OnAuthChanged;
    }

    private void OnAuthChanged()
    {
        if (_auth.IsAuthenticated)
        {
            LoadAccountPlaylists();
        }
        else
        {
            _accountPlaylists.Clear();
        }
    }

    public void OnOpen()
    {
        if (_auth.IsAuthenticated)
        {
            LoadAccountPlaylists();
        }
    }

    public void OnClose() { }

    private async void LoadAccountPlaylists()
    {
        if (_isLoadingAccountPlaylists) return;
        
        _isLoadingAccountPlaylists = true;
        
        try
        {
            _accountPlaylists = await _youtube.GetUserPlaylistsAsync();
            _library.MergeAccountPlaylists(_accountPlaylists);
        }
        catch
        {
            _accountPlaylists = new List<Playlist>();
        }
        finally
        {
            _isLoadingAccountPlaylists = false;
        }
    }

    public void Render()
    {
        ImGui.BeginChild("LibraryContent", new Vector2(0, -90), ImGuiChildFlags.None);
        
        // Заголовок
        ImGui.Text("Ваша библиотека");
        
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 180);
        
        // Кнопка входа или создания плейлиста
        if (!_auth.IsAuthenticated)
        {
            _loginButton.RenderCompact();
            ImGui.SameLine();
        }
        
        if (ImGui.Button("+ Создать плейлист"))
        {
            _showCreateDialog = true;
            _newPlaylistName = "";
        }
        
        ImGui.Separator();
        ImGui.Spacing();
        
        // Локальные плейлисты
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Локальные плейлисты");
        ImGui.Spacing();
        
        RenderPlaylistGrid(_library.GetAllPlaylists().Where(p => p.IsLocal));
        
        ImGui.Spacing();
        ImGui.Spacing();
        
        // Плейлисты из аккаунта
        if (_auth.IsAuthenticated)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), "Плейлисты YouTube");
            
            if (_isLoadingAccountPlaylists)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(загрузка...)");
            }
            
            ImGui.Spacing();
            
            if (_accountPlaylists.Count > 0)
            {
                RenderPlaylistGrid(_accountPlaylists);
            }
            else if (!_isLoadingAccountPlaylists)
            {
                ImGui.TextDisabled("Нет плейлистов в аккаунте");
            }
        }
        else
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Войдите, чтобы увидеть плейлисты из YouTube");
        }
        
        // Диалог создания плейлиста
        RenderCreateDialog();
        
        ImGui.EndChild();
    }

    private void RenderPlaylistGrid(IEnumerable<Playlist> playlists)
    {
        float cardWidth = 160;
        float cardHeight = 180;
        float spacing = 15;
        
        float availWidth = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)((availWidth + spacing) / (cardWidth + spacing)));
        
        int col = 0;
        foreach (var playlist in playlists)
        {
            if (col > 0)
                ImGui.SameLine(0, spacing);
            
            RenderPlaylistCard(playlist, cardWidth, cardHeight);
            
            col++;
            if (col >= columns)
            {
                col = 0;
            }
        }
    }

    private void RenderPlaylistCard(Playlist playlist, float width, float height)
    {
        ImGui.PushID(playlist.Id);
        
        var drawList = ImGui.GetWindowDrawList();
        
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.12f, 0.14f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        
        ImGui.BeginChild($"card_{playlist.Id}", new Vector2(width, height), ImGuiChildFlags.None);
        
        Vector2 startPos = ImGui.GetCursorScreenPos();
        
        // Обложка
        float coverSize = width - 20;
        float coverX = 10;
        float coverY = 10;
        
        Vector2 coverPos = startPos + new Vector2(coverX, coverY);
        
        // Цвет обложки
        uint coverColor = playlist.Id == "liked" 
            ? ImGui.GetColorU32(new Vector4(0.7f, 0.2f, 0.2f, 1f))
            : playlist.IsFromAccount 
                ? ImGui.GetColorU32(new Vector4(0.2f, 0.4f, 0.7f, 1f))
                : ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.25f, 1f));
        
        drawList.AddRectFilled(coverPos, coverPos + new Vector2(coverSize, coverSize), coverColor, 8f);
        
        // Иконка
        Vector2 iconCenter = coverPos + new Vector2(coverSize / 2, coverSize / 2);
        string icon = playlist.Id == "liked" ? "♥" : playlist.IsFromAccount ? "▶" : "♪";
        drawList.AddText(iconCenter - new Vector2(8, 10), 
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.8f)), icon);
        
        // Кликабельная область
        ImGui.SetCursorPos(new Vector2(coverX, coverY));
        ImGui.InvisibleButton("cover_btn", new Vector2(coverSize, coverSize));
        
        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(coverPos, coverPos + new Vector2(coverSize, coverSize),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.1f)), 8f);
            
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        
        if (ImGui.IsItemClicked())
        {
            _openPlaylist(playlist.Id);
        }
        
        // Контекстное меню
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup($"playlist_menu_{playlist.Id}");
        }
        
        RenderPlaylistContextMenu(playlist);
        
        // Название
        ImGui.SetCursorPos(new Vector2(10, coverSize + 20));
        ImGui.Text(TruncateText(playlist.Name, 15));
        
        // Количество треков
        ImGui.SetCursorPos(new Vector2(10, coverSize + 40));
        ImGui.TextDisabled($"{playlist.TrackCount} треков");
        
        ImGui.EndChild();
        
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        
        ImGui.PopID();
    }

    private void RenderPlaylistContextMenu(Playlist playlist)
    {
        if (ImGui.BeginPopup($"playlist_menu_{playlist.Id}"))
        {
            if (ImGui.MenuItem("▶ Открыть"))
            {
                _openPlaylist(playlist.Id);
            }
            
            if (playlist.Id != "liked" && playlist.IsLocal)
            {
                ImGui.Separator();
                
                if (ImGui.MenuItem("✏ Переименовать"))
                {
                    // TODO: Диалог переименования
                }
                
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                
                if (ImGui.MenuItem("🗑 Удалить"))
                {
                    _library.DeletePlaylist(playlist.Id);
                }
                
                ImGui.PopStyleColor();
            }
            
            ImGui.EndPopup();
        }
    }

    private void RenderCreateDialog()
    {
        if (_showCreateDialog)
        {
            ImGui.OpenPopup("CreatePlaylistDialog");
        }
        
        Vector2 center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 20));
        
        if (ImGui.BeginPopupModal("CreatePlaylistDialog", ref _showCreateDialog, 
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.Text("Создать новый плейлист");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.Text("Название:");
            ImGui.SetNextItemWidth(300);
            
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            
            bool enterPressed = ImGui.InputText("##name", ref _newPlaylistName, 100, 
                ImGuiInputTextFlags.EnterReturnsTrue);
            
            ImGui.PopStyleVar();
            
            ImGui.Spacing();
            ImGui.Spacing();
            
            // Кнопки
            float buttonWidth = 140;
            float totalWidth = buttonWidth * 2 + 10;
            float startX = (300 - totalWidth) / 2 + 20;
            
            ImGui.SetCursorPosX(startX);
            
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 0.9f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            
            if ((ImGui.Button("Создать", new Vector2(buttonWidth, 35)) || enterPressed) 
                && !string.IsNullOrWhiteSpace(_newPlaylistName))
            {
                var playlist = _library.CreatePlaylist(_newPlaylistName.Trim());
                _openPlaylist(playlist.Id);
                _showCreateDialog = false;
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            
            ImGui.SameLine();
            
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            
            if (ImGui.Button("Отмена", new Vector2(buttonWidth, 35)))
            {
                _showCreateDialog = false;
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.PopStyleVar();
            
            ImGui.EndPopup();
        }
        
        ImGui.PopStyleVar(2);
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }
}