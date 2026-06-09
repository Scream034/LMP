namespace LMP.Core.Models;

public sealed class AuthState
{
    public bool IsAuthenticated { get; set; }

    public string UserName { get; set; } = "Guest";
    public string UserEmail { get; set; } = "";
    public string AvatarUrl { get; set; } = "";

    public string PageId { get; set; } = "";

    /// <summary>
    /// Индекс сессии мульти-авторизации Google активного аккаунта.
    /// По умолчанию пустая строка — это позволяет доверять флагу IsSelected от YouTube 
    /// при первичном входе через расширение.
    /// </summary>
    public string AuthUser { get; set; } = "";

    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}