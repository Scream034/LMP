namespace LMP.Core.Audio;

/// <summary>
/// Состояние воспроизведения аудио
/// </summary>
public enum PlaybackState
{
    /// <summary>Остановлен, ничего не загружено</summary>
    Stopped,
    
    /// <summary>Загрузка и инициализация</summary>
    Loading,
    
    /// <summary>Буферизация данных</summary>
    Buffering,
    
    /// <summary>Активное воспроизведение</summary>
    Playing,
    
    /// <summary>Пауза</summary>
    Paused,
    
    /// <summary>Ошибка воспроизведения</summary>
    Error
}