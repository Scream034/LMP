namespace LMP.Core.Models;

public class AuthState
{
    // --- Состояние авторизации ---
    public bool IsAuthenticated { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime TokenExpiry { get; set; }

    // --- Google профиль ---
    public string? UserEmail { get; set; }
    public string? UserName { get; set; }

    // --- YouTube канал (основной источник аватара) ---
    public string? YouTubeChannelId { get; set; }
    public string? YouTubeChannelName { get; set; }
    public string? YouTubeAvatarUrl { get; set; }
}
