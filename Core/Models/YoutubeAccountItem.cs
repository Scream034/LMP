namespace LMP.Core.Models;

public sealed class YoutubeAccountItem
{
    public static LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Порядковый номер аккаунта в списке.
    /// </summary>
    public int Index { get; set; }

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string PageId { get; set; } = ""; // Пусто, если это основной аккаунт
    
    /// <summary>
    /// Идентификатор Gaia основного аккаунта.
    /// </summary>
    public string GaiaId { get; set; } = "";

    /// <summary>
    /// Тег канала (например, @handle).
    /// </summary>
    public string Handle { get; set; } = "";

    /// <summary>
    /// Возвращает приоритетный ID для отображения (PageId для бренда или GaiaId для основного профиля).
    /// </summary>
    public string DisplayId => !string.IsNullOrEmpty(PageId) ? PageId : GaiaId;

    /// <summary>
    /// Индекс сессии мульти-авторизации Google (обычно "0" для основного, "1", "2" для дополнительных).
    /// </summary>
    public string AuthUser { get; set; } = "0";
    
    public bool IsSelected { get; set; }
}