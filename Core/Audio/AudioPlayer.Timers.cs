namespace LMP.Core.Audio;

public sealed partial class AudioPlayer
{
    private Timer? _positionTimer;
    private Timer? _bufferTimer;

    /// <summary>Запускает таймеры позиции и буфера.</summary>
    private void StartTimers()
    {
        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        if (_positionTimer == null)
            _positionTimer = new Timer(_ => _events.RaisePositionChanged(Position), null, 0, interval);
        else
            _positionTimer.Change(0, interval);

        if (_bufferTimer == null)
            _bufferTimer = new Timer(_ => RaiseBufferState(), null, 0, AudioConstants.BufferStateUpdateIntervalMs);
        else
            _bufferTimer.Change(0, AudioConstants.BufferStateUpdateIntervalMs);
    }

    /// <summary>Останавливает таймеры без dispose.</summary>
    private void StopTimers()
    {
        _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _bufferTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Запускает position timer с задержкой первого tick (после seek).</summary>
    private void StartPositionTimerDelayed()
    {
        int interval = (int)_options.PositionUpdateInterval.TotalMilliseconds;

        if (_positionTimer == null)
            _positionTimer = new Timer(_ => _events.RaisePositionChanged(Position), null, interval, interval);
        else
            _positionTimer.Change(interval, interval);
    }

    /// <summary>Останавливает только position timer.</summary>
    private void StopPositionTimer()
    {
        _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Полная финализация таймеров. Только при Dispose.</summary>
    private void DisposeTimers()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
        _bufferTimer?.Dispose();
        _bufferTimer = null;
    }

    /// <summary>Публикует текущее состояние буфера.</summary>
    private void RaiseBufferState()
    {
        var pipeline = _activePipeline;
        if (pipeline == null) return;

        var source = pipeline.Source;
        _events.RaiseBufferState(new BufferState(source.BufferProgress, source.IsFullyBuffered, source.GetBufferedRanges()));
    }
}