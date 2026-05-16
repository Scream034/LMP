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
    /// <remarks>
    /// Алгоритм: семафор является единственным источником правды о наличии данных.
    /// <see cref="Complete"/> делает Release, после которого consumer проснётся, увидит
    /// <see cref="_isCompleted"/> == true и при пустой очереди вернёт null.
    /// Это исключает race condition между проверкой флага и ожиданием семафора.
    /// </remarks>
    public async ValueTask<AudioFrame?> ReadAsync(CancellationToken ct = default)
    {
        while (!_isDisposed)
        {
            try
            {
                await _dataAvailable.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            // После Release от Complete() очередь может быть пуста — это сигнал завершения.
            if (_frames.TryDequeue(out var frame))
            {
                _spaceAvailable.Release();
                return frame;
            }

            // Проснулись по сигналу Complete(), но очередь пуста — конец потока.
            if (_isCompleted)
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
    /// Release семафора гарантирует, что ожидающий consumer проснётся
    /// и обнаружит пустую очередь + <see cref="_isCompleted"/> == true.
    /// </summary>
    public void Complete()
    {
        _isCompleted = true;
        // Разбудить ждущего consumer — он увидит _isCompleted и пустую очередь
        try { _dataAvailable.Release(); }
        catch (SemaphoreFullException) { /* Consumer уже не ждёт */ }
    }

    /// <summary>
    /// Очищает буфер. Вызывать между остановкой и запуском потока.
    /// </summary>
    /// <remarks>
    /// Дренирует оба семафора для восстановления консистентного состояния:
    /// <see cref="_spaceAvailable"/> возвращается к maxFrames,
    /// <see cref="_dataAvailable"/> — к 0.
    /// </remarks>
    public void Clear()
    {
        while (_frames.TryDequeue(out _))
        {
            // Не делаем Release _spaceAvailable — пересоздаём состояние ниже
        }

        // Дренируем _dataAvailable: убираем все "призрачные" Release.
        while (_dataAvailable.Wait(0)) { }

        // Восстанавливаем _spaceAvailable до maxFrames.
        // Текущий CurrentCount может быть < maxFrames из-за не-returned слотов.
        int missing = _maxFrames - _spaceAvailable.CurrentCount;
        if (missing > 0)
        {
            _spaceAvailable.Release(missing);
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