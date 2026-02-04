namespace LMP.Core.Models;

public sealed class AuthState
{
    // --- Состояние авторизации ---
    public bool IsAuthenticated { get; set; }

    // --- Профиль пользователя (YouTube) ---
    public string UserName { get; set; } = "Guest";
    public string UserEmail { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}