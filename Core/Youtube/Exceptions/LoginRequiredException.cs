namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Выбрасывается когда YouTube требует авторизацию для воспроизведения контента.
/// Обычно для возрастных ограничений (age-restricted) или приватного контента.
/// </summary>
public sealed class LoginRequiredException(
    string message,
    string videoId,
    LoginRequiredReason reason) : YoutubeExplodeException(message)
{
    /// <summary>
    /// Причина требования авторизации.
    /// </summary>
    public LoginRequiredReason Reason { get; } = reason;

    /// <summary>
    /// ID видео.
    /// </summary>
    public string VideoId { get; } = videoId;

    /// <summary>
    /// Ключ локализации для данного типа ошибки.
    /// </summary>
    public string GetLocalizationKey()
    {
        return Reason switch
        {
            LoginRequiredReason.AgeRestricted => "Error_Login_AgeRestricted",
            LoginRequiredReason.Private => "Error_Login_Private",
            LoginRequiredReason.MembersOnly => "Error_Login_MembersOnly",
            _ => "Error_Login_Required"
        };
    }
}

/// <summary>
/// Причина требования авторизации.
/// </summary>
public enum LoginRequiredReason
{
    /// <summary>Неизвестная причина.</summary>
    Unknown = 0,

    /// <summary>Возрастные ограничения.</summary>
    AgeRestricted,

    /// <summary>Приватное видео.</summary>
    Private,

    /// <summary>Только для подписчиков.</summary>
    MembersOnly
}