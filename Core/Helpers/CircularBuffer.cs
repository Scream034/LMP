using System.Runtime.CompilerServices;

namespace LMP.Core.Helpers;

/// <summary>
/// Lock-free циклический буфер для single-producer-single-consumer сценария.
/// Оптимизирован для аудио данных.
/// </summary>
/// <typeparam name="T">Тип элементов</typeparam>
public sealed class CircularBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    
    private volatile int _head; // Позиция записи (producer)
    private volatile int _tail; // Позиция чтения (consumer)
    
    /// <summary>
    /// Создаёт циклический буфер указанной ёмкости.
    /// </summary>
    /// <param name="capacity">Ёмкость буфера (должна быть степенью 2)</param>
    public CircularBuffer(int capacity)
    {
        // Округляем до ближайшей степени 2 для быстрой модульной арифметики
        _capacity = RoundUpToPowerOf2(capacity);
        _buffer = new T[_capacity];
    }
    
    /// <summary>Текущее количество элементов в буфере</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int head = _head;
            int tail = _tail;
            return (head - tail + _capacity) & (_capacity - 1);
        }
    }
    
    /// <summary>Ёмкость буфера</summary>
    public int Capacity => _capacity;
    
    /// <summary>Свободное место в буфере</summary>
    public int Available => _capacity - Count - 1;
    
    /// <summary>Буфер пуст</summary>
    public bool IsEmpty => _head == _tail;
    
    /// <summary>
    /// Записывает данные в буфер.
    /// </summary>
    /// <param name="data">Данные для записи</param>
    /// <returns>Количество записанных элементов</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Write(ReadOnlySpan<T> data)
    {
        int available = Available;
        int toWrite = Math.Min(data.Length, available);
        
        if (toWrite == 0) return 0;
        
        int head = _head;
        int firstPart = Math.Min(toWrite, _capacity - head);
        
        data[..firstPart].CopyTo(_buffer.AsSpan(head, firstPart));
        
        if (toWrite > firstPart)
        {
            data.Slice(firstPart, toWrite - firstPart).CopyTo(_buffer.AsSpan(0, toWrite - firstPart));
        }
        
        // Memory barrier для visibility
        Thread.MemoryBarrier();
        _head = (head + toWrite) & (_capacity - 1);
        
        return toWrite;
    }
    
    /// <summary>
    /// Читает данные из буфера.
    /// </summary>
    /// <param name="output">Буфер для чтения</param>
    /// <returns>Количество прочитанных элементов</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<T> output)
    {
        int count = Count;
        int toRead = Math.Min(output.Length, count);
        
        if (toRead == 0) return 0;
        
        int tail = _tail;
        int firstPart = Math.Min(toRead, _capacity - tail);
        
        _buffer.AsSpan(tail, firstPart).CopyTo(output[..firstPart]);
        
        if (toRead > firstPart)
        {
            _buffer.AsSpan(0, toRead - firstPart).CopyTo(output.Slice(firstPart, toRead - firstPart));
        }
        
        Thread.MemoryBarrier();
        _tail = (tail + toRead) & (_capacity - 1);
        
        return toRead;
    }
    
    /// <summary>
    /// Очищает буфер.
    /// </summary>
    public void Clear()
    {
        _head = 0;
        _tail = 0;
    }
    
    private static int RoundUpToPowerOf2(int value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}