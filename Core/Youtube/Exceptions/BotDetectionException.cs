namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Выбрасывается когда операция заблокирована из-за bot detection cooldown от YouTube.
/// Содержит информацию об оставшемся времени ожидания.
/// </summary>
public sealed class BotDetectionException(string message, TimeSpan remaining) : YoutubeExplodeException(message)
{
    /// <summary>
    /// Оставшееся время cooldown.
    /// </summary>
    public TimeSpan RemainingCooldown { get; } = remaining;

    /// <summary>
    /// Время когда cooldown закончится.
    /// </summary>
    public DateTime CooldownEndsAt { get; } = DateTime.UtcNow + remaining;

    /// <summary>
    /// Ключ локализации для сообщения.
    /// </summary>
    public const string LocalizationKey = "Error_BotDetection_Message";

    /// <summary>
    /// Форматирует оставшееся время для отображения.
    /// </summary>
    public string FormatRemainingTime()
    {
        return RemainingCooldown.TotalSeconds >= 60
            ? $"{RemainingCooldown.Minutes}:{RemainingCooldown.Seconds:D2}"
            : $"{RemainingCooldown.TotalSeconds:F0}s";
    }
}