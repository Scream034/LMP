using System;

namespace MyLiteMusicPlayer.Models;

public class AuthState
{
    public bool IsAuthenticated { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime TokenExpiry { get; set; }
    public string? UserEmail { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
}