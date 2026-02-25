using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio;

/// <summary>
/// Состояния конечного автомата плеера.
/// Переходы строго определены в AudioPlayerStateMachine.
/// </summary>
public enum PlayerState
{
    /// <summary>Начальное состояние, ничего не загружено.</summary>
    Idle,
    
    /// <summary>Загрузка источника и инициализация декодера.</summary>
    Loading,
    
    /// <summary>Буферизация перед стартом воспроизведения.</summary>
    Buffering,
    
    /// <summary>Активное воспроизведение.</summary>
    Playing,
    
    /// <summary>Пауза.</summary>
    Paused,
    
    /// <summary>Выполняется seek операция.</summary>
    Seeking,
    
    /// <summary>Критическая ошибка, требуется Stop.</summary>
    Error,
    
    /// <summary>Плеер уничтожен.</summary>
    Disposed
}

/// <summary>
/// Допустимые переходы состояний.
/// </summary>
public static class PlayerStateTransitions
{
    public static bool CanTransition(PlayerState from, PlayerState to)
    {
        return (from, to) switch
        {
            // Из Idle
            (PlayerState.Idle, PlayerState.Loading) => true,
            (PlayerState.Idle, PlayerState.Disposed) => true,
            
            // Из Loading
            (PlayerState.Loading, PlayerState.Buffering) => true,
            (PlayerState.Loading, PlayerState.Error) => true,
            (PlayerState.Loading, PlayerState.Idle) => true,
            (PlayerState.Loading, PlayerState.Loading) => true, // Новый Play отменяет текущий
            (PlayerState.Loading, PlayerState.Disposed) => true,
            
            // Из Buffering
            (PlayerState.Buffering, PlayerState.Playing) => true,
            (PlayerState.Buffering, PlayerState.Error) => true,
            (PlayerState.Buffering, PlayerState.Idle) => true,
            (PlayerState.Buffering, PlayerState.Loading) => true, // Новый Play
            (PlayerState.Buffering, PlayerState.Disposed) => true,
            
            // Из Playing
            (PlayerState.Playing, PlayerState.Paused) => true,
            (PlayerState.Playing, PlayerState.Seeking) => true,
            (PlayerState.Playing, PlayerState.Idle) => true,
            (PlayerState.Playing, PlayerState.Loading) => true, // Новый Play
            (PlayerState.Playing, PlayerState.Error) => true,
            (PlayerState.Playing, PlayerState.Disposed) => true,
            
            // Из Paused
            (PlayerState.Paused, PlayerState.Playing) => true,
            (PlayerState.Paused, PlayerState.Seeking) => true,
            (PlayerState.Paused, PlayerState.Idle) => true,
            (PlayerState.Paused, PlayerState.Loading) => true, // Новый Play
            (PlayerState.Paused, PlayerState.Disposed) => true,
            
            // Из Seeking
            (PlayerState.Seeking, PlayerState.Playing) => true,
            (PlayerState.Seeking, PlayerState.Paused) => true,
            (PlayerState.Seeking, PlayerState.Idle) => true,
            (PlayerState.Seeking, PlayerState.Loading) => true, // Новый Play
            (PlayerState.Seeking, PlayerState.Error) => true,
            (PlayerState.Seeking, PlayerState.Disposed) => true,
            
            // Из Error
            (PlayerState.Error, PlayerState.Idle) => true,
            (PlayerState.Error, PlayerState.Loading) => true, // Retry/новый Play
            (PlayerState.Error, PlayerState.Disposed) => true,
            
            _ => false
        };
    }

    /// <summary>
    /// Можно ли принять команду в текущем состоянии?
    /// </summary>
    public static bool CanAcceptCommand(PlayerState state, IAudioCommand command)
    {
        // Disposed — ничего не принимаем кроме Dispose
        if (state == PlayerState.Disposed)
            return command is DisposeCommand;

        return command switch
        {
            // Play можно вызвать из ЛЮБОГО состояния (кроме Disposed)
            // Новый Play отменит текущую операцию
            PlayCommand => true,
            
            // Stop можно из любого кроме Idle и Disposed
            StopCommand => state is not PlayerState.Idle,
            
            // Pause только из Playing
            PauseCommand => state is PlayerState.Playing,
            
            // Resume только из Paused
            ResumeCommand => state is PlayerState.Paused,
            
            // Seek из Playing или Paused
            SeekCommand => state is PlayerState.Playing or PlayerState.Paused,
            
            // Dispose всегда
            DisposeCommand => true,
            
            _ => false
        };
    }
}