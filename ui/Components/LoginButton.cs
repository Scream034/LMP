using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Services;

namespace MyLiteMusicPlayer.UI.Components;

public class LoginButton
{
    private readonly GoogleAuthService _auth;
    private bool _isLoggingIn;

    public LoginButton(GoogleAuthService auth)
    {
        _auth = auth;
    }

    public void Render()
    {
        if (_auth.IsAuthenticated)
        {
            RenderUserInfo();
        }
        else
        {
            RenderLoginButton();
        }
    }

    public void RenderCompact()
    {
        if (_auth.IsAuthenticated)
        {
            // Аватар пользователя
            var drawList = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();
            
            drawList.AddCircleFilled(pos + new Vector2(15, 15), 15,
                ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 1f)));
            
            // Первая буква имени
            string initial = !string.IsNullOrEmpty(_auth.State.UserName) 
                ? _auth.State.UserName[0].ToString().ToUpper() 
                : "?";
            
            drawList.AddText(pos + new Vector2(10, 7), 
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), initial);
            
            ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(0, 0));
            
            if (ImGui.InvisibleButton("user_menu", new Vector2(30, 30)))
            {
                ImGui.OpenPopup("UserMenuPopup");
            }
            
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(_auth.State.UserEmail ?? "Пользователь");
            }
            
            if (ImGui.BeginPopup("UserMenuPopup"))
            {
                ImGui.Text(_auth.State.UserName ?? "Пользователь");
                ImGui.TextDisabled(_auth.State.UserEmail ?? "");
                ImGui.Separator();
                
                if (ImGui.MenuItem("Выйти"))
                {
                    _auth.Logout();
                }
                
                ImGui.EndPopup();
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.4f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.5f, 0.9f, 1f));
            
            if (ImGui.Button(_isLoggingIn ? "..." : "Войти", new Vector2(60, 28)))
            {
                if (!_isLoggingIn)
                {
                    StartLogin();
                }
            }
            
            ImGui.PopStyleColor(2);
        }
    }

    private void RenderLoginButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.4f, 0.8f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.5f, 0.9f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        
        string buttonText = _isLoggingIn ? "Вход..." : "🔑 Войти через Google";
        
        if (ImGui.Button(buttonText, new Vector2(200, 40)))
        {
            if (!_isLoggingIn)
            {
                StartLogin();
            }
        }
        
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        
        if (!_isLoggingIn)
        {
            ImGui.TextDisabled("Для доступа к рекомендациям и библиотеке");
        }
    }

    private void RenderUserInfo()
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector2 startPos = ImGui.GetCursorScreenPos();
        
        // Аватар
        float avatarSize = 40;
        drawList.AddCircleFilled(startPos + new Vector2(avatarSize / 2, avatarSize / 2), 
            avatarSize / 2, ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 1f)));
        
        string initial = !string.IsNullOrEmpty(_auth.State.UserName) 
            ? _auth.State.UserName[0].ToString().ToUpper() 
            : "?";
        
        drawList.AddText(startPos + new Vector2(avatarSize / 2 - 5, avatarSize / 2 - 8),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), initial);
        
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(avatarSize + 10, 0));
        
        ImGui.BeginGroup();
        ImGui.Text(_auth.State.UserName ?? "Пользователь");
        ImGui.TextDisabled(_auth.State.UserEmail ?? "");
        ImGui.EndGroup();
        
        ImGui.SameLine();
        
        if (ImGui.SmallButton("Выйти"))
        {
            _auth.Logout();
        }
    }

    private async void StartLogin()
    {
        _isLoggingIn = true;
        
        try
        {
            await _auth.StartLoginAsync();
        }
        finally
        {
            _isLoggingIn = false;
        }
    }
}