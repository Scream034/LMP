using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.UI.Components;

namespace MyLiteMusicPlayer.UI.Tabs;

public class SettingsTab : ITab
{
    public string Id => "settings";
    public string Name => "⚙ Настройки";
    public bool CanClose => false;

    private readonly LibraryService _library;
    private readonly GoogleAuthService _auth;
    private readonly LoginButton _loginButton;
    
    private string _downloadPath;
    private bool _discordRpcEnabled;
    private bool _autoPlayOnUrl;
    private bool _showResetConfirm;

    public SettingsTab(LibraryService library, GoogleAuthService auth)
    {
        _library = library;
        _auth = auth;
        _loginButton = new LoginButton(auth);
        
        LoadSettings();
    }

    private void LoadSettings()
    {
        _downloadPath = _library.DownloadPath;
        _discordRpcEnabled = _library.Data.DiscordRpcEnabled;
        _autoPlayOnUrl = _library.Data.AutoPlayOnUrlPaste;
    }

    public void OnOpen() 
    {
        LoadSettings();
    }
    
    public void OnClose() { }

    public void Render()
    {
        ImGui.BeginChild("SettingsContent", new Vector2(0, -90), ImGuiChildFlags.None);
        
        ImGui.Text("Настройки");
        ImGui.Separator();
        ImGui.Spacing();
        
        RenderAccountSection();
        ImGui.Spacing();
        
        RenderPlaybackSection();
        ImGui.Spacing();
        
        RenderDownloadsSection();
        ImGui.Spacing();
        
        RenderIntegrationsSection();
        ImGui.Spacing();
        
        RenderDataSection();
        ImGui.Spacing();
        
        RenderAboutSection();
        
        ImGui.EndChild();
    }

    private void RenderAccountSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        
        if (ImGui.CollapsingHeader("👤 Аккаунт Google", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(20);
            ImGui.Spacing();
            
            _loginButton.Render();
            
            if (_auth.IsAuthenticated)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Авторизация позволяет синхронизировать плейлисты");
                ImGui.TextDisabled("и получать персональные рекомендации");
            }
            
            ImGui.Spacing();
            ImGui.Unindent(20);
        }
        
        ImGui.PopStyleColor();
    }

    private void RenderPlaybackSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        
        if (ImGui.CollapsingHeader("▶ Воспроизведение", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(20);
            ImGui.Spacing();
            
            if (ImGui.Checkbox("Автовоспроизведение при вставке URL", ref _autoPlayOnUrl))
            {
                _library.Data.AutoPlayOnUrlPaste = _autoPlayOnUrl;
                _library.Save();
            }
            
            ImGui.TextDisabled("При вставке ссылки на трек — сразу начать воспроизведение");
            
            ImGui.Spacing();
            ImGui.Unindent(20);
        }
        
        ImGui.PopStyleColor();
    }

    private void RenderDownloadsSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        
        if (ImGui.CollapsingHeader("⬇ Загрузки", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(20);
            ImGui.Spacing();
            
            ImGui.Text("Папка для скачивания:");
            ImGui.SetNextItemWidth(400);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
            ImGui.InputText("##downloadPath", ref _downloadPath, 512, ImGuiInputTextFlags.ReadOnly);
            ImGui.PopStyleVar();
            
            ImGui.SameLine();
            
            if (ImGui.Button("Обзор..."))
            {
                OpenFolderDialog();
            }
            
            ImGui.Spacing();
            
            if (ImGui.Button("📂 Открыть папку загрузок"))
            {
                OpenDownloadFolder();
            }
            
            ImGui.Spacing();
            ImGui.Unindent(20);
        }
        
        ImGui.PopStyleColor();
    }

    private void OpenFolderDialog()
    {
        // Используем Windows Forms диалог через reflection или внешний процесс
        try
        {
            // var dialog = new System.Windows.Forms.FolderBrowserDialog
            // {
            //     Description = "Выберите папку для загрузок",
            //     SelectedPath = _downloadPath,
            //     ShowNewFolderButton = true
            // };
            
            // if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            // {
            //     _downloadPath = dialog.SelectedPath;
            //     _library.DownloadPath = _downloadPath;
            // }
        }
        catch
        {
            // Fallback: просто показываем текущую папку
            OpenDownloadFolder();
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(_downloadPath);
            System.Diagnostics.Process.Start("explorer.exe", _downloadPath);
        }
        catch { }
    }

    private void RenderIntegrationsSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        
        if (ImGui.CollapsingHeader("🔗 Интеграции", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(20);
            ImGui.Spacing();
            
            if (ImGui.Checkbox("Discord Rich Presence", ref _discordRpcEnabled))
            {
                _library.Data.DiscordRpcEnabled = _discordRpcEnabled;
                _library.Save();
            }
            
            ImGui.TextDisabled("Показывать текущий трек в статусе Discord");
            
            ImGui.Spacing();
            ImGui.Unindent(20);
        }
        
        ImGui.PopStyleColor();
    }

    private void RenderDataSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        
        if (ImGui.CollapsingHeader("💾 Данные", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent(20);
            ImGui.Spacing();
            
            ImGui.Text("Статистика библиотеки:");
            ImGui.Spacing();
            
            ImGui.BulletText($"Треков: {_library.Data.Tracks.Count}");
            ImGui.BulletText($"Плейлистов: {_library.Data.Playlists.Count}");
            ImGui.BulletText($"В истории: {_library.Data.RecentlyPlayedIds.Count}");
            
            int downloadedCount = _library.Data.Tracks.Values.Count(t => t.IsDownloaded);
            ImGui.BulletText($"Скачано: {downloadedCount}");
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            
            if (ImGui.Button("🗑 Очистить историю"))
            {
                _library.ClearHistory();
            }
            
            ImGui.SameLine();
            
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.3f, 0.3f, 1f));
            
            if (ImGui.Button("⚠ Сбросить всё"))
            {
                _showResetConfirm = true;
                ImGui.OpenPopup("ConfirmResetPopup");
            }
            
            ImGui.PopStyleColor(2);
            
            RenderResetConfirmPopup();
            
            ImGui.Spacing();
            ImGui.Unindent(20);
        }
        
        ImGui.PopStyleColor();
    }

    private void RenderResetConfirmPopup()
    {
        Vector2 center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 20));
        
        if (ImGui.BeginPopupModal("ConfirmResetPopup", ref _showResetConfirm, 
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.Text("⚠ Внимание!");
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.TextWrapped("Это действие удалит ВСЕ данные:");
            ImGui.BulletText("Все плейлисты");
            ImGui.BulletText("Историю прослушивания");
            ImGui.BulletText("Лайки и настройки");
            
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Скачанные файлы НЕ будут удалены.");
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.3f, 0.3f, 1f));
            
            if (ImGui.Button("Да, сбросить", new Vector2(120, 30)))
            {
                _library.Reset();
                LoadSettings();
                _showResetConfirm = false;
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.PopStyleColor(2);
            
            ImGui.SameLine();
            
            if (ImGui.Button("Отмена", new Vector2(120, 30)))
            {
                _showResetConfirm = false;
                ImGui.CloseCurrentPopup();
            }
            
            ImGui.EndPopup();
        }
        
        ImGui.PopStyleVar(2);
    }

    private void RenderAboutSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        
        if (ImGui.CollapsingHeader("ℹ О программе"))
        {
            ImGui.Indent(20);
            ImGui.Spacing();
            
            ImGui.Text("YTM Player");
            ImGui.TextDisabled("Версия 2.0.0");
            
            ImGui.Spacing();
            
            ImGui.TextWrapped("Легковесный музыкальный плеер с поддержкой YouTube Music.");
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.TextDisabled("Зависимости:");
            ImGui.BulletText("yt-dlp — загрузка аудио");
            ImGui.BulletText("FFmpeg — конвертация");
            ImGui.BulletText("NAudio — воспроизведение");
            ImGui.BulletText("ImGui.NET — интерфейс");
            
            ImGui.Spacing();
            
            if (ImGui.Button("📂 Открыть папку приложения"))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", _library.AppFolder);
                }
                catch { }
            }
            
            ImGui.Spacing();
            ImGui.Unindent(20);
        }
        
        ImGui.PopStyleColor();
    }
}