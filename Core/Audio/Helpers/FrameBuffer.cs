using System.Collections.Concurrent;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Буфер фреймов для producer-consumer паттерна.
/// Producer: сетевой поток загружает фреймы.
/// Consumer: декодер читает фреймы.
/// </summary>
public sealed class FrameBuffer(int maxFrames = 100) : IDisposable
{
    private readonly ConcurrentQueue<AudioFrame> _frames = new();
    private readonly SemaphoreSlim _dataAvailable = new(0);
    private readonly SemaphoreSlim _spaceAvailable = new(maxFrames, maxFrames);
    private readonly int _maxFrames = maxFrames;
    
    private volatile bool _isCompleted;
    private volatile bool _isDisposed;

    /// <summary>Количество фреймов в буфере</summary>
    public int Count => _frames.Count;
    
    /// <summary>Буфер заполнен</summary>
    public bool IsFull => _frames.Count >= _maxFrames;
    
    /// <summary>Достигнут конец потока</summary>
    public bool IsCompleted => _isCompleted && _frames.IsEmpty;
    
    /// <summary>
    /// Добавляет фрейм в буфер (producer).
    /// </summary>
    public async ValueTask<bool> WriteAsync(AudioFrame frame, CancellationToken ct = default)
    {
        if (_isDisposed || _isCompleted) return false;
        
        await _spaceAvailable.WaitAsync(ct).ConfigureAwait(false);
        
        if (_isDisposed) return false;
        
        _frames.Enqueue(frame);
        _dataAvailable.Release();
        
        return true;
    }
    
    /// <summary>
    /// Читает фрейм из буфера (consumer).
    /// </summary>
    public async ValueTask<AudioFrame?> ReadAsync(CancellationToken ct = default)
    {
        while (!_isDisposed)
        {
            if (_frames.TryDequeue(out var frame))
            {
                _spaceAvailable.Release();
                return frame;
            }
            
            if (_isCompleted && _frames.IsEmpty)
            {
                return null;
            }
            
            try
            {
                await _dataAvailable.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Пытается прочитать фрейм без ожидания.
    /// </summary>
    public bool TryRead(out AudioFrame frame)
    {
        if (_frames.TryDequeue(out frame))
        {
            _spaceAvailable.Release();
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Отмечает конец потока.
    /// </summary>
    public void Complete()
    {
        _isCompleted = true;
        _dataAvailable.Release(); // Разбудить ждущих
    }
    
    /// <summary>
    /// Очищает буфер.
    /// </summary>
    public void Clear()
    {
        while (_frames.TryDequeue(out _))
        {
            _spaceAvailable.Release();
        }
        _isCompleted = false;
    }
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        _dataAvailable.Dispose();
        _spaceAvailable.Dispose();
    }
}